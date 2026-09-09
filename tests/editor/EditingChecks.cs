#if LUA_EDITING_CHECKS
using ApproximatelyUp.ComputerMod;

int checks = 0;
void Check(bool pass, string name)
{
    if (!pass) throw new InvalidOperationException("Lua editing check failed: " + name);
    checks++;
}
void Indent(string text, int cursor, int anchor, bool reverse, string expected, int c, int a, string name)
{
    Check(LuaEditing.Indent(new(text, cursor, anchor), reverse, out var result) && result == new LuaEdit(expected, c, a), name);
}
void Newline(string text, int cursor, int anchor, string expected, int caret, string name)
{
    Check(LuaEditing.Newline(new(text, cursor, anchor), out var result) && result == new LuaEdit(expected, caret, caret), name);
}
string[] Complete(string marked, bool force = false)
{
    int caret = marked.IndexOf('|');
    string text = marked.Remove(caret, 1);
    return LuaEditing.Complete(new(text, caret, caret), force, out _, out _);
}

Indent("", 0, 0, false, "    ", 4, 4, "empty Tab");
Indent("abCD", 2, 2, false, "ab    CD", 6, 6, "Tab at caret, not end");
Indent("one\ntwo\nthree", 4, 0, false, "    one\ntwo\nthree", 8, 4, "selection ending at next line excludes it");
Indent("one\ntwo\nthree", 0, 5, false, "    one\n    two\nthree", 4, 13, "reverse partial multiline selection");
Indent("a\n", 2, 0, false, "    a\n", 6, 4, "final empty line excluded");
Indent("a\n", 2, 2, false, "a\n    ", 6, 6, "caret on final empty line");
Indent("a\r\nb\r\nc", 3, 0, false, "    a\r\nb\r\nc", 7, 4, "CRLF boundary");
Indent("a\rb\rc", 2, 0, false, "    a\rb\rc", 6, 4, "CR boundary");
Indent("    one\n\ttwo\n  three", 10, 2, true, "one\ntwo\n  three", 5, 0, "unindent clamps endpoint inside removed prefix");
Indent("  ab", 4, 4, true, "ab", 2, 2, "partial indentation");
Indent("\tab", 2, 2, true, "ab", 1, 1, "literal tab unindent");
Indent("one", 2, 1, true, "one", 2, 1, "no indentation unchanged");
Indent("\u03bb\ud83d\ude80\nx", 4, 0, false, "    \u03bb\ud83d\ude80\nx", 8, 4, "UTF-16 offsets through astral character");
Indent("\ud83d\ude80x", 2, 2, false, "\ud83d\ude80    x", 6, 6, "caret after surrogate pair");
Check(!LuaEditing.Indent(new("\ud83d\ude80", 1, 1), false, out _), "reject split surrogate caret");
Check(!LuaEditing.Indent(new("x", -1, 0), false, out _), "reject negative offset");
Check(!LuaEditing.Indent(new("x", 2, 0), true, out _), "reject out-of-range offset");
for (int c = 0; c <= 9; c++)
    for (int a = 0; a <= 9; a++)
    {
        var edit = new LuaEdit("ab\r\ncd\nef", c, a);
        if (c == a) continue;
        Check(LuaEditing.Indent(edit, false, out var indented) &&
            LuaEditing.Indent(indented, true, out var restored) && restored == edit, "indent/unindent selection roundtrip " + c + "/" + a);
    }

Newline("    output()", 12, 12, "    output()\n    ", 17, "copy current indentation");
Newline("if x then", 9, 9, "if x then\n    ", 14, "then block");
Newline("for i=1,8 do", 12, 12, "for i=1,8 do\n    ", 17, "do block");
Newline("else", 4, 4, "else\n    ", 9, "else block");
Newline("repeat", 6, 6, "repeat\n    ", 11, "repeat block");
Newline("function tick()", 15, 15, "function tick()\n    ", 20, "function block");
Newline("local f = function(x)", 21, 21, "local f = function(x)\n    ", 26, "anonymous function block");
Newline("if x then -- note", 17, 17, "if x then -- note\n    ", 22, "block followed by comment");
Newline("-- if x then", 12, 12, "-- if x then\n", 13, "comment does not open block");
Newline("x = 'then'", 10, 10, "x = 'then'\n", 11, "string does not open block");
Newline("if x then end", 13, 13, "if x then end\n", 14, "inline closed block");
Newline("  abcXYZ", 5, 8, "  abc\n  ", 8, "newline replaces selection");
Newline("a\r\n  b", 6, 6, "a\r\n  b\r\n  ", 10, "preserve CRLF");
Newline("a\r  b", 5, 5, "a\r  b\r  ", 8, "preserve CR");
Newline("    abc", 2, 2, "  \n    abc", 5, "split indentation copies only prefix before caret");

Check(Complete("inp|").SequenceEqual(new[] { "input", "input_bool", "input_vec3" }), "API prefix");
Check(Complete("out|").Contains("output_vec3"), "vector output API");
Check(Complete("tick_|").Contains("tick_id"), "tick ID API");
Check(Complete("sta|").Contains("state"), "state API");
Check(Complete("fun|").Contains("function"), "Lua keyword");
Check(Complete("math.s|").SequenceEqual(new[] { "sin", "sqrt" }), "dot math members only");
Check(Complete("math.|").Length == 8, "math dot automatically triggers bounded list");
Check(Complete("math . at|").Contains("atan2"), "whitespace around math dot");
Check(Complete("math.ra|").SequenceEqual(new[] { "rad" }), "no unsupported random");
Check(Complete("math.random|", true).Length == 0, "unavailable math functions absent");
Check(Complete("io.|", true).Length == 0, "no forbidden library members");
Check(Complete("thing.math.s|", true).Length == 0, "nested math is not global math");
Check(Complete("math..s|", true).Length == 0, "concat not member completion");
Check(Complete("thing:inp|", true).Length == 0, "no global proposals for colon member");
Check(Complete("1inp|", true).Length == 0, "numeric prefix excluded");
Check(Complete("|", true).Length == 8, "explicit blank context, max eight");
Check(Complete("|").Length == 0, "automatic blank context hidden");
Check(Complete("local throttle = 1\nthr|").Contains("throttle"), "lexical local identifiers");
Check(Complete("local function f(throttle)\n thr|\nend").Contains("throttle"), "parameter identifiers");
Check(Complete("math.sin(1)\nsi|").Length == 0, "member names are not advertised as globals");
Check(Complete("-- hiddenName\nlocal s='hiddenOther'\nhidd|").Length == 0, "comments and strings not harvested");
foreach (string marked in new[] { "-- inp|", "-- inp|\noutput()", "'inp|ut'", "\"inp|ut\"", "'a\\' inp|'",
    "'abc\\\ninp|'", "[[inp|ut]]", "[=[inp|ut]=]", "[==[inp|ut]==]", "--[=[inp|ut]=]", "'inp|", "--[[inp|", "[[inp|" })
    Check(Complete(marked, true).Length == 0, "exclude literal/comment context " + marked);
Check(Complete("--[=[word]] still comment\n inp|\n]=]", true).Length == 0, "long bracket must close matching equals");
Check(Complete("[[word]] inp|").Contains("input"), "after closed long string");
Check(Complete("-- word\ninp|").Contains("input"), "after line comment");
Check(Complete("'bad\ninp|").Contains("input"), "recover unterminated short string at newline");
Check(Complete("'text\\z \n  inp|'", true).Length == 0, "Lua whitespace-eating escape keeps string context");
Check(Complete("local \u03bbrate = 2\n\u03bbr|").Contains("\u03bbrate"), "Unicode lexical identifier");
Check(Complete("x='\ud83d\ude80'\ninp|").Contains("input"), "completion after astral string");

var middle = new LuaEdit("a = inpBROKEN + 3", 7, 7);
var proposals = LuaEditing.Complete(middle, false, out int start, out int end);
Check(proposals.Contains("input") && start == 4 && end == 13, "completion replacement is entire token at caret");
Check(LuaEditing.Replace(middle, start, end, "input", out var accepted) && accepted == new LuaEdit("a = input + 3", 9, 9), "replace token suffix, retain surrounding source");
var dot = new LuaEdit("math.sBROKEN(1)", 6, 6);
LuaEditing.Complete(dot, false, out start, out end);
Check(LuaEditing.Replace(dot, start, end, "sin", out accepted) && accepted == new LuaEdit("math.sin(1)", 8, 8), "math insertion preserves qualifier and arguments");
Check(LuaEditing.Complete(new("inp", 3, 0), true, out _, out _).Length == 0, "no autocomplete over selection");
Check(!LuaEditing.Replace(new("\ud83d\ude80", 2, 2), 1, 2, "x", out _), "replacement cannot split surrogate");
var maximum = new LuaEdit(new string('x', LuaEditing.MaxLength), 1, 1);
Check(!LuaEditing.Indent(maximum, false, out var rejected) && rejected == maximum, "Tab length limit leaves original intact");
var selectedLimit = new LuaEdit("a\n" + new string('x', LuaEditing.MaxLength - 6), 4, 0);
Check(!LuaEditing.Indent(selectedLimit, false, out rejected) && rejected == selectedLimit, "multiline indentation length check is atomic");
Check(!LuaEditing.Newline(maximum, out rejected) && rejected == maximum, "newline length limit leaves original intact");
Check(!LuaEditing.Replace(maximum, 0, 1, "longer", out rejected) && rejected == maximum, "completion length limit");
Check(LuaEditing.Replace(maximum, 0, 5, "input", out accepted) && accepted.Text.Length == LuaEditing.MaxLength, "replacement at exact limit");
var nearly = new LuaEdit(new string('x', LuaEditing.MaxLength - 4), 0, 0);
Check(LuaEditing.Indent(nearly, false, out accepted) && accepted.Text.Length == LuaEditing.MaxLength, "indent to exact limit");
var oversized = new LuaEdit(new string('x', LuaEditing.MaxLength + 1), 0, 0);
Check(!LuaEditing.Indent(oversized, true, out rejected) && rejected == oversized, "oversized source retained read-only");
Check(LuaEditing.Complete(oversized, true, out _, out _).Length == 0, "oversized completion scan refused");
var boundedNames = "local " + string.Join(" ", Enumerable.Range(0, 500).Select(i => "name" + i)) + "\nnam|";
Check(Complete(boundedNames).Length == 8, "bounded lexical list");

var history = new LuaEditHistory();
var original = new LuaEdit("abc", 1, 2);
var changed = new LuaEdit("aXc", 2, 2);
history.Record(original, changed);
Check(history.Move(changed, false) == original, "undo source and selection");
Check(history.Move(original, true) == changed, "redo source and selection");
history.Move(changed, false);
var fork = new LuaEdit("abY", 3, 3);
history.Record(original, fork);
Check(history.Move(fork, true) == fork, "new edit discards redo");
history.Clear();
Check(history.Move(fork, false) == fork && history.Move(fork, true) == fork, "clear history on document change");
var step = new LuaEdit("", 0, 0);
for (int i = 0; i < 50; i++)
{
    var next = new LuaEdit(step.Text + "x", i + 1, i + 1);
    history.Record(step, next);
    step = next;
}
for (int i = 0; i < 50; i++) step = history.Move(step, false);
Check(step.Text.Length == 50 - LuaEditHistory.Capacity, "bounded undo history");

Console.WriteLine($"PASS: {checks} Lua editing checks.");
#endif
