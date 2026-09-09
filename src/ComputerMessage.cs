using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ApproximatelyUp.ComputerMod;

internal sealed class ComputerMessage
{
    public string Kind { get; set; } = "get";
    public long Id { get; set; }
    public string Target { get; set; } = "";
    public string? ProgramId { get; set; }
    public string? Source { get; set; }
    public bool Run { get; set; }
    public bool Running { get; set; }
    public double[] Inputs { get; set; } = Array.Empty<double>();
    public double[] Outputs { get; set; } = Array.Empty<double>();
    public string? Error { get; set; }
}

// Pure wire/session code: no Unity, Steam initialization, source files or script execution.
internal sealed class ComputerPacket
{
    internal const int MaxBytes = 65536;
    internal const string GameHash = "5DE3E4D8167C9C81986D192B5B6B80BFE98BCD84BA3C9B1B765FC24E33475B26";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 4
    };
    private static readonly string[] EnvelopeFields = { "Magic", "Protocol", "Version", "Game", "Prefab", "Policy", "Kind", "Nonce", "Session", "Challenge", "Sequence", "Message" };
    private static readonly string[] MessageFields = { "Kind", "Id", "Target", "ProgramId", "Source", "Run", "Running", "Inputs", "Outputs", "Error" };

    public string Magic { get; set; } = "AU08-LINK";
    public int Protocol { get; set; } = 1;
    public string Version { get; set; } = "0.4.4";
    public string Game { get; set; } = GameHash;
    public string Prefab { get; set; } = "CB54D87A797DFF3A";
    public string Policy { get; set; } = "all-trusted-edit-run";
    public string Kind { get; set; } = "hello";
    public string Nonce { get; set; } = "";
    public string Session { get; set; } = "";
    public string Challenge { get; set; } = "";
    public long Sequence { get; set; }
    public ComputerMessage? Message { get; set; }

    internal static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    internal static bool Hex(string? value, int length) => value is not null && value.Length == length &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool TargetId(string value)
    {
        // Native SCGuid.ToString is four uint32 hex groups, NOT a System.Guid.
        if (value.Length != 35) return false;
        for (int i = 0; i < value.Length; i++)
            if (i is 8 or 17 or 26 ? value[i] != '-' : !Uri.IsHexDigit(value[i])) return false;
        return true;
    }

    internal static byte[] Encode(ComputerPacket packet, bool fromHost)
    {
        packet.Validate(fromHost);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(packet, Json);
        if (bytes.Length > MaxBytes)
            throw new InvalidDataException("Computer message exceeds 64 KiB after JSON escaping; shorten the source.");
        return bytes;
    }

    internal static ComputerPacket Decode(byte[] bytes, bool fromHost)
    {
        if (bytes.Length is < 2 or > MaxBytes) throw new InvalidDataException("Invalid computer packet size.");
        _ = Utf8.GetCharCount(bytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        CheckFields(document.RootElement, EnvelopeFields);
        var body = document.RootElement.GetProperty("Message");
        if (body.ValueKind != JsonValueKind.Null) CheckFields(body, MessageFields);
        var packet = JsonSerializer.Deserialize<ComputerPacket>(bytes, Json) ?? throw new InvalidDataException("Empty computer packet.");
        packet.Validate(fromHost);
        return packet;
    }

    private static void CheckFields(JsonElement element, string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected computer packet object.");
        int seen = 0;
        foreach (var property in element.EnumerateObject())
        {
            int index = Array.IndexOf(fields, property.Name);
            if (index < 0 || (seen & (1 << index)) != 0) throw new InvalidDataException("Unknown or duplicate computer packet field.");
            seen |= 1 << index;
        }
        if (seen != (1 << fields.Length) - 1) throw new InvalidDataException("Missing computer packet field.");
    }

    private void Validate(bool fromHost)
    {
        if (Magic != "AU08-LINK" || Protocol != 1 || Version != "0.4.4" || Game != GameHash ||
            Prefab != "CB54D87A797DFF3A" || Policy != "all-trusted-edit-run")
            throw new InvalidDataException("Incompatible computer protocol, plugin, game, prefab or policy.");
        if (!Hex(Nonce, 32)) throw new InvalidDataException("Invalid computer connection nonce.");
        if (Kind != "message")
        {
            if (fromHost ? Kind is not ("welcome" or "ready") : Kind is not ("hello" or "ack"))
                throw new InvalidDataException("Invalid computer handshake direction.");
            if (Message is not null || Sequence != 0 || (Kind == "hello" ? Session != "" || Challenge != "" : !Hex(Session, 32) || !Hex(Challenge, 32)))
                throw new InvalidDataException("Invalid computer handshake fields.");
            return;
        }
        if (Sequence <= 0 || !Hex(Session, 32) || !Hex(Challenge, 32) || Message is not { } m)
            throw new InvalidDataException("Missing computer session or message.");
        if (fromHost ? m.Kind is not ("state" or "error") : m.Kind is not ("get" or "save" or "run" or "stop" or "stop-all"))
            throw new InvalidDataException("Invalid computer operation or direction.");
        if (m.Id < 0 || (!fromHost && (m.Id == 0 || Sequence != m.Id)))
            throw new InvalidDataException("Invalid computer request ID.");
        if (m.Target is null || (m.Target.Length != 0 && !TargetId(m.Target)) ||
            (!fromHost && m.Kind != "stop-all" && m.Target.Length == 0) ||
            (m.Kind == "stop-all" && m.Target.Length != 0))
            throw new InvalidDataException("Invalid computer target GUID.");
        if (m.ProgramId is not null && !Hex(m.ProgramId, 12)) throw new InvalidDataException("Invalid computer program ID.");
        if (m.Inputs is null || m.Outputs is null || m.Inputs.Length > 8 || m.Outputs.Length > 8 ||
            m.Inputs.Concat(m.Outputs).Any(v => !double.IsFinite(v) || v < -(double)1e20f || v > (double)1e20f))
            throw new InvalidDataException("Computer state must contain at most eight finite numeric inputs/outputs.");
        if (m.Source is not null)
        {
            if (m.Kind is not ("save" or "state") || m.Source.Length > 16384 || m.Source.Contains('\0') ||
                m.Source.StartsWith("\x1b", StringComparison.Ordinal) || m.Source.StartsWith("MoonSharp_dump_b64::", StringComparison.Ordinal) ||
                Utf8.GetByteCount(m.Source) > 49152)
                throw new InvalidDataException("Computer source must be UTF-8 Lua text of at most 16384 UTF-16 characters.");
        }
        if (m.Kind == "save" && m.Source is null) throw new InvalidDataException("Save requires source text.");
        if (m.Error is not null && (m.Kind is not ("error" or "state") || m.Error.Length > 256 || m.Error.Contains('\0')))
            throw new InvalidDataException("Invalid computer error text.");
        if (m.Error is not null) _ = Utf8.GetByteCount(m.Error);
        if (m.Kind == "error" && string.IsNullOrEmpty(m.Error)) throw new InvalidDataException("Error requires a description.");
        if ((m.Run && m.Kind != "save") || (m.Running && m.Kind != "state") ||
            (m.Kind != "state" && (m.Inputs.Length != 0 || m.Outputs.Length != 0)) ||
            (m.Kind == "stop-all" && m.ProgramId is not null))
            throw new InvalidDataException("Unexpected computer operation fields.");
    }
}

// A host challenge is rotated whenever the client nonce changes. Replaying an old
// hello therefore cannot reset deduplication and authorize old save/run packets.
internal sealed class ComputerLinkPeer
{
    internal readonly bool Client;
    internal string Nonce { get; private set; }
    internal string Session { get; private set; }
    internal string Challenge { get; private set; }
    internal bool Ready { get; private set; }
    internal double LastSeen { get; private set; }
    internal double NextHeartbeat;
    internal long LastSequence { get; private set; }
    private double tokens = 4, refilled;

    internal ComputerLinkPeer(bool client, string epoch, double now)
    {
        Client = client;
        Nonce = client ? ComputerPacket.NewNonce() : "";
        Session = client ? "" : epoch;
        Challenge = client ? "" : ComputerPacket.NewNonce();
        LastSeen = refilled = now;
    }

    internal void Invalidate()
    {
        Ready = false;
        LastSequence = 0;
        if (Client) { Nonce = ComputerPacket.NewNonce(); Session = Challenge = ""; }
        else { Nonce = ""; Challenge = ComputerPacket.NewNonce(); }
        NextHeartbeat = 0;
    }

    internal void Expire(double now)
    {
        if (now - LastSeen <= 12) return;
        Invalidate();
        LastSeen = now;
    }

    internal ComputerPacket? Heartbeat() => Client ? Control(Challenge.Length == 0 ? "hello" : "ack") :
        Nonce.Length == 0 ? null : Control(Ready ? "ready" : "welcome");

    internal ComputerPacket? AcceptControl(ComputerPacket packet, double now)
    {
        if (!Client)
        {
            if (packet.Kind is not ("hello" or "ack")) return null;
            if (packet.Nonce != Nonce)
            {
                Nonce = packet.Nonce;
                Challenge = ComputerPacket.NewNonce();
                Ready = false;
                LastSequence = 0;
            }
            if (packet.Kind == "ack" && Matches(packet))
            {
                Ready = true;
                LastSeen = now;
                return Control("ready");
            }
            return Control("welcome");
        }
        if (packet.Nonce != Nonce) return null;
        if (packet.Kind == "welcome")
        {
            if (Ready && !Matches(packet)) { Invalidate(); return Control("hello"); }
            Session = packet.Session;
            Challenge = packet.Challenge;
            return Control("ack");
        }
        if (packet.Kind == "ready" && Matches(packet)) { Ready = true; LastSeen = now; }
        return null;
    }

    internal bool AcceptMessage(ComputerPacket packet, double now)
    {
        if (!Ready || packet.Kind != "message" || !Matches(packet) || packet.Sequence <= LastSequence) return false;
        LastSequence = packet.Sequence; // Even a rate-limited or failed handler request is never replayed.
        LastSeen = now;
        // Stops must remain available after a burst of save/run requests.
        if (Client || packet.Message!.Kind is "get" or "stop" or "stop-all") return true;
        tokens = Math.Min(4, tokens + Math.Max(0, now - refilled) * 2);
        refilled = now;
        if (tokens < 1) return false;
        tokens--;
        return true;
    }

    internal ComputerPacket Wrap(ComputerMessage message, long sequence) => new()
    {
        Kind = "message", Nonce = Nonce, Session = Session, Challenge = Challenge,
        Sequence = sequence, Message = message
    };

    private bool Matches(ComputerPacket packet) => packet.Nonce == Nonce && packet.Session == Session && packet.Challenge == Challenge;
    private ComputerPacket Control(string kind) => new()
    {
        Kind = kind, Nonce = Nonce,
        Session = kind == "hello" ? "" : Session, Challenge = kind == "hello" ? "" : Challenge
    };
}
