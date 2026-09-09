using System.Security.Cryptography;
using System.Text;

namespace ApproximatelyUp.ComputerMod;

// Source files are immutable. Native labels and legacy bindings point at them by ID.
internal sealed class ProgramStore
{
    private readonly string root;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal const string StarterSource = @"-- AU-08 starter: runs each physics tick in game mode.
-- Inputs and outputs are numbered 1 to 8.
-- Unwritten outputs become zero each tick.

function tick()
    local value = input(1)
    output(1, value)

    -- Boolean example:
    -- output(2, input_bool(2))

    -- Vector example (uses ports 3, 4 and 5):
    -- output_vec3(3, input_vec3(3))

    -- Persistent memory until stop / game-mode exit:
    -- state.count = (state.count or 0) + 1
end
";
    internal ProgramStore(string root) => this.root = Path.GetFullPath(root);
    internal string ReadTemplate()
    {
        string path = Path.Combine(root, "computer.lua");
        if (!File.Exists(path)) return StarterSource;
        string source = Read(path);
        return string.IsNullOrWhiteSpace(source) ? StarterSource : source;
    }
    internal string Load(string id)
    {
        if (!ValidId(id)) throw new InvalidDataException("Invalid local program ID.");
        return Read(Path.Combine(root, "scripts", id + ".lua"));
    }
    internal string Create(string source)
    {
        Validate(source);
        var bytes = Utf8.GetBytes(source);
        var directory = Path.Combine(root, "scripts");
        Directory.CreateDirectory(directory);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            string path = Path.Combine(directory, id + ".lua");
            FileStream file;
            try { file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(path)) { continue; }
            using (file) { file.Write(bytes); file.Flush(true); }
            return id;
        }
        throw new IOException("Unable to allocate a new program ID.");
    }
    internal string? LegacyId(string world, string component)
    {
        string path = LegacyPath(world, component);
        if (!File.Exists(path)) return null;
        string id = Read(path);
        if (!ValidId(id)) throw new InvalidDataException("Invalid legacy program binding.");
        return id;
    }
    internal void BindLegacy(string world, string component, string? expected, string id)
    {
        if (!ValidId(id)) throw new InvalidDataException("Invalid local program ID.");
        if (LegacyId(world, component) != expected) throw new IOException("Program binding changed outside this edit.");
        string path = LegacyPath(world, component);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(Utf8.GetBytes(id)); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private string LegacyPath(string world, string component)
    {
        if (string.IsNullOrWhiteSpace(world) || string.IsNullOrWhiteSpace(component))
            throw new InvalidOperationException("Load a saved world before saving a legacy block program.");
        string key = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(world + ":" + component)));
        return Path.Combine(root, "legacy", key + ".id");
    }
    internal static bool ValidId(string? id) => id is { Length: 12 } && id.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    internal static void Validate(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 16384 || source.Contains('\0') || source.StartsWith("\x1b", StringComparison.Ordinal) ||
            source.StartsWith("MoonSharp_dump_b64::", StringComparison.Ordinal))
            throw new InvalidDataException("Programs must be Lua text of at most 16384 characters, not bytecode.");
        _ = Utf8.GetByteCount(source);
    }
    private static string Read(string path)
    {
        using var reader = new StreamReader(path, Utf8, true);
        var chars = new char[16385];
        int count = reader.ReadBlock(chars, 0, chars.Length);
        string source = new(chars, 0, count);
        Validate(source);
        return source;
    }
}
