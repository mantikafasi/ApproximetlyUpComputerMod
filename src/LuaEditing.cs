using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ApproximatelyUp.ComputerMod;

// Offsets are UTF-16 string offsets, not Unity 6's rendered code-point indices.
internal readonly record struct LuaEdit(string Text, int Cursor, int Anchor);

internal static class LuaEditing
{
    internal const int MaxLength = 16384;
    internal const int MaxSuggestions = 8;
    private static readonly string[] Globals =
        "input input_bool input_vec3 output output_vec3 state dt tick_id tick math graph assert error type".Split(' ');
    private static readonly string[] Keywords =
        "and break do else elseif end false for function goto if in local nil not or repeat return then true until while".Split(' ');
    // Keep this aligned with the allowlist in LuaComputer, not the stock Lua math library.
    private static readonly string[] MathNames =
        "abs acos asin atan atan2 ceil cos deg exp floor fmod log max min pi pow rad random randomseed sin sqrt tan".Split(' ');
    private static readonly string[] GraphNames = "axes clear marker series".Split(' ');

    internal static bool Valid(LuaEdit edit) => edit.Text.Length <= MaxLength &&
        Boundary(edit.Text, edit.Cursor) && Boundary(edit.Text, edit.Anchor);

    private static bool Boundary(string text, int index) => index >= 0 && index <= text.Length &&
        !(index > 0 && index < text.Length && char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]));

    internal static bool Replace(LuaEdit edit, int start, int end, string insert, out LuaEdit result)
    {
        result = edit;
        if (!Valid(edit) || !Boundary(edit.Text, start) || !Boundary(edit.Text, end) || end < start ||
            (long)edit.Text.Length - (end - start) + insert.Length > MaxLength) return false;
        result = new(edit.Text.Remove(start, end - start).Insert(start, insert), start + insert.Length, start + insert.Length);
        return true;
    }

    internal static bool Indent(LuaEdit edit, bool unindent, out LuaEdit result)
    {
        result = edit;
        if (!Valid(edit)) return false;
        int lo = Math.Min(edit.Cursor, edit.Anchor), hi = Math.Max(edit.Cursor, edit.Anchor);
        if (!unindent && lo == hi) return Replace(edit, lo, hi, "    ", out result);
        string text = edit.Text;
        int first = LineStart(text, lo), last = LineStart(text, hi == lo ? hi : hi - 1);
        var changes = new List<(int Start, int Remove)>();
        int delta = 0;
        for (int start = first; start <= last;)
        {
            int remove = 0;
            if (unindent)
            {
                if (start < text.Length && text[start] == '\t') remove = 1;
                else while (remove < 4 && start + remove < text.Length && text[start + remove] == ' ') remove++;
            }
            changes.Add((start, remove));
            delta += unindent ? -remove : 4;
            int next = start;
            while (next < text.Length && text[next] is not ('\r' or '\n')) next++;
            if (next == text.Length) break;
            if (text[next++] == '\r' && next < text.Length && text[next] == '\n') next++;
            start = next;
        }
        if (text.Length + delta > MaxLength) return false;
        var output = new StringBuilder(text.Length + delta);
        int copied = 0, cursor = edit.Cursor, anchor = edit.Anchor;
        foreach (var change in changes)
        {
            output.Append(text, copied, change.Start - copied);
            if (!unindent) output.Append("    ");
            copied = change.Start + change.Remove;
            if (edit.Cursor >= change.Start) cursor += unindent ? -Math.Min(change.Remove, edit.Cursor - change.Start) : 4;
            if (edit.Anchor >= change.Start) anchor += unindent ? -Math.Min(change.Remove, edit.Anchor - change.Start) : 4;
        }
        output.Append(text, copied, text.Length - copied);
        result = new(output.ToString(), cursor, anchor);
        return true;
    }

    internal static bool Newline(LuaEdit edit, out LuaEdit result)
    {
        result = edit;
        if (!Valid(edit)) return false;
        int lo = Math.Min(edit.Cursor, edit.Anchor), start = LineStart(edit.Text, lo), indentEnd = start;
        while (indentEnd < lo && edit.Text[indentEnd] is ' ' or '\t') indentEnd++;
        string indent = edit.Text.Substring(start, indentEnd - start);
        string code = Code(edit.Text, lo, out _).Substring(start, lo - start).TrimEnd();
        // ponytail: recognize complete single-line headers only; no parser, formatter, or automatic end insertion.
        if (Regex.IsMatch(code, @"\b(then|do|else|repeat)$|\bfunction\s*(?:[\w.:]+\s*)?\([^()]*\)$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) indent += "    ";
        string newline = edit.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" :
            edit.Text.Contains('\r') && !edit.Text.Contains('\n') ? "\r" : "\n";
        return Replace(edit, lo, Math.Max(edit.Cursor, edit.Anchor), newline + indent, out result);
    }

    private static int LineStart(string text, int position)
    {
        while (position > 0 && text[position - 1] != '\n' &&
            (text[position - 1] != '\r' || position < text.Length && text[position] == '\n')) position--;
        return position;
    }

    internal static string[] Complete(LuaEdit edit, bool explicitRequest, out int start, out int end)
    {
        start = end = edit.Cursor;
        if (!Valid(edit) || edit.Cursor != edit.Anchor) return Array.Empty<string>();
        string code = Code(edit.Text, edit.Cursor, out bool blocked);
        if (blocked) return Array.Empty<string>();
        while (start > 0 && IdentifierPart(code[start - 1])) start--;
        while (end < code.Length && IdentifierPart(code[end])) end++;
        string prefix = code.Substring(start, edit.Cursor - start);
        if (prefix.Length > 64 || prefix.Length > 0 && !IdentifierStart(prefix[0])) return Array.Empty<string>();
        int previous = start - 1;
        while (previous >= 0 && char.IsWhiteSpace(code[previous])) previous--;
        string members = "";
        if (previous >= 0 && code[previous] == '.')
        {
            int qualifierEnd = previous;
            while (qualifierEnd > 0 && char.IsWhiteSpace(code[qualifierEnd - 1])) qualifierEnd--;
            int qualifier = qualifierEnd;
            while (qualifier > 0 && IdentifierPart(code[qualifier - 1])) qualifier--;
            members = code.Substring(qualifier, qualifierEnd - qualifier);
            if (members is not ("math" or "graph")) return Array.Empty<string>();
            int before = qualifier - 1;
            while (before >= 0 && char.IsWhiteSpace(code[before])) before--;
            if (before >= 0 && code[before] is '.' or ':') return Array.Empty<string>();
        }
        else if (previous >= 0 && code[previous] == ':') return Array.Empty<string>();
        if (prefix.Length == 0 && !explicitRequest && members.Length == 0) return Array.Empty<string>();
        var found = new List<string>(MaxSuggestions);
        void Offer(string name)
        {
            if (found.Count < MaxSuggestions && name.StartsWith(prefix, StringComparison.Ordinal) &&
                (explicitRequest || name != prefix) && !found.Contains(name)) found.Add(name);
        }
        foreach (string name in members == "math" ? MathNames : members == "graph" ? GraphNames : Globals) Offer(name);
        if (members.Length == 0)
        {
            foreach (string name in Keywords) Offer(name);
            // Lexical names, including locals/parameters, not scope or type inference. Bound both scan and name count.
            int identifiers = 0;
            for (int i = 0; i < code.Length && found.Count < MaxSuggestions && identifiers < 256;)
            {
                if (!IdentifierPart(code[i])) { i++; continue; }
                int word = i++;
                while (i < code.Length && IdentifierPart(code[i])) i++;
                if (!IdentifierStart(code[word]) || i - word > 64 || word == start) continue;
                identifiers++;
                int before = word - 1;
                while (before >= 0 && char.IsWhiteSpace(code[before])) before--;
                if (before >= 0 && code[before] is '.' or ':') continue;
                Offer(code.Substring(word, i - word));
            }
        }
        return found.ToArray();
    }

    private static bool IdentifierStart(char c) => c == '_' || char.IsLetter(c);
    private static bool IdentifierPart(char c) => IdentifierStart(c) || char.IsDigit(c);

    // Mask strings/comments without changing UTF-16 offsets or line boundaries. Lua long brackets may use any number of '='.
    private static string Code(string text, int caret, out bool blocked)
    {
        var code = text.ToCharArray();
        bool inside = false;
        void Mask(int start, int end, bool closed)
        {
            if (caret > start && (caret < end || !closed && caret == end)) inside = true;
            for (int p = start; p < end; p++) if (code[p] is not ('\r' or '\n')) code[p] = ' ';
        }
        for (int i = 0; i < text.Length;)
        {
            int start = i;
            bool comment = i + 1 < text.Length && text[i] == '-' && text[i + 1] == '-';
            if (comment) i += 2;
            int bracket = i, equals = 0;
            if (i < text.Length && text[i] == '[')
            {
                int p = i + 1;
                while (p < text.Length && text[p] == '=') { p++; equals++; }
                if (p < text.Length && text[p] == '[')
                {
                    string close = "]" + new string('=', equals) + "]";
                    int finish = text.IndexOf(close, p + 1, StringComparison.Ordinal);
                    i = finish < 0 ? text.Length : finish + close.Length;
                    Mask(start, i, finish >= 0);
                    continue;
                }
            }
            if (comment)
            {
                while (i < text.Length && text[i] is not ('\r' or '\n')) i++;
                Mask(start, i, false);
                continue;
            }
            i = bracket;
            if (text[i] is '\'' or '"')
            {
                char quote = text[i++];
                bool closed = false;
                while (i < text.Length)
                {
                    if (text[i] == quote) { i++; closed = true; break; }
                    if (text[i] is '\r' or '\n') break;
                    if (text[i++] == '\\' && i < text.Length)
                    {
                        char escaped = text[i++];
                        if (escaped == 'z') while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                        else if (escaped == '\r' && i < text.Length && text[i] == '\n') i++;
                    }
                }
                Mask(start, i, closed);
                continue;
            }
            i++;
        }
        blocked = inside;
        return new string(code);
    }
}

// Unity's IMGUI TextEditor only exposes a transient backup, not a redo history.
internal sealed class LuaEditHistory
{
    internal const int Capacity = 32;
    private readonly List<LuaEdit> undo = new(), redo = new();
    internal void Clear() { undo.Clear(); redo.Clear(); }
    internal void Record(LuaEdit before, LuaEdit after)
    {
        if (before.Text == after.Text || !LuaEditing.Valid(before) || !LuaEditing.Valid(after)) return;
        Push(undo, before);
        redo.Clear();
    }
    internal LuaEdit Move(LuaEdit current, bool forward)
    {
        var from = forward ? redo : undo;
        if (from.Count == 0 || !LuaEditing.Valid(current)) return current;
        Push(forward ? undo : redo, current);
        var restored = from[^1];
        from.RemoveAt(from.Count - 1);
        return restored;
    }
    private static void Push(List<LuaEdit> list, LuaEdit edit)
    {
        if (list.Count == Capacity) list.RemoveAt(0);
        list.Add(edit);
    }
}
