using OpenDelulu.Formats.IO;

namespace OpenDelulu.Formats.Character;

/// <summary>Parameters of one look behaviour (48 bytes in the file).</summary>
/// <param name="MaxYaw">Horizontal limit in radians (e.g. 0.349 = 20°).</param>
/// <param name="MaxPitch">Vertical limit in radians (e.g. 0.1745 = 10°).</param>
/// <param name="Timings">Four durations in milliseconds (e.g. 300, 200, 1000, 200).</param>
public sealed record LookBehavior(
    int Id,
    string Name,
    int Kind,
    float MaxYaw,
    float MaxPitch,
    int DurationMs,
    int Unknown1,
    float Speed,
    float Value1,
    float Value2,
    int[] Timings)
{
    public override string ToString() => $"{Id} {Name}";
}

/// <summary>
/// .eyes / .neck: how the eyes (o01_N_Me_R/L) or the head (o01_J_Head) turn to look at something.
/// Behaviour names are Shift-JIS, NOT-encoded: 目で見る (look with the eyes), 目でそらす (look away),
/// 首と目で見る (look with head and eyes)... Layout (verified on all .eyes and .neck files):
/// <code>
/// int32  version (1), 2 bytes 0
/// controlled frames: byte length + NOT-encoded name, repeated (2 for .eyes, 1 for .neck)
/// int32  entry count
/// entry 0 (default): byte length + name, 48-byte block
/// entries 1..: uint16 id, byte length + name, 48-byte block
/// 48-byte block: int32 kind, float maxYaw, float maxPitch, int32 ms, int32, float speed, float, float, 4 x int32 ms
/// </code>
/// </summary>
public sealed class LookFile
{
    public int Version { get; init; }
    public IReadOnlyList<string> Frames { get; init; } = [];
    public IReadOnlyList<LookBehavior> Behaviors { get; init; } = [];

    public LookBehavior? Default => Behaviors.Count > 0 ? Behaviors[0] : null;

    public static LookFile Read(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data, writable: false), ShiftJis.Encoding);
        int version = r.ReadInt32();
        r.ReadUInt16();

        // Frame names start with "o01_"; the entry count (a small int32) follows them.
        var frames = new List<string>();
        while (r.BaseStream.Position + 1 < data.Length)
        {
            long at = r.BaseStream.Position;
            int length = r.ReadByte();
            if (length is < 4 or > 64 || at + 1 + length > data.Length)
            {
                r.BaseStream.Position = at;
                break;
            }
            string name = r.ReadFixedName(length);
            if (!name.StartsWith("o01_", StringComparison.Ordinal))
            {
                r.BaseStream.Position = at;
                break;
            }
            frames.Add(name);
        }

        int count = r.ReadInt32();
        var behaviors = new List<LookBehavior>(count);
        for (int i = 0; i < count; i++)
        {
            int id = i == 0 ? 0 : r.ReadUInt16();
            string name = r.ReadFixedName(r.ReadByte());
            behaviors.Add(new LookBehavior(id, name,
                Kind: r.ReadInt32(),
                MaxYaw: r.ReadSingle(),
                MaxPitch: r.ReadSingle(),
                DurationMs: r.ReadInt32(),
                Unknown1: r.ReadInt32(),
                Speed: r.ReadSingle(),
                Value1: r.ReadSingle(),
                Value2: r.ReadSingle(),
                Timings: [r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32()]));
        }
        if (r.BaseStream.Position != data.Length)
            throw new InvalidDataException($"look file: {data.Length - r.BaseStream.Position} unparsed bytes.");
        return new LookFile { Version = version, Frames = frames, Behaviors = behaviors };
    }
}
