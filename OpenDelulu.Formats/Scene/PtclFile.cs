using OpenDelulu.Formats.IO;
using OpenDelulu.Formats.Xx;

namespace OpenDelulu.Formats.Scene;

/// <summary>A key of a particle emitter curve: a frame number and float values.</summary>
public sealed record ParticleKey(int Frame, float[] Values);

/// <summary>
/// One emitter: a small .xx model (billboard quad + material + texture), animation curves and the frame
/// of the stage/character model it is attached to (e.g. o01_N_sun).
/// </summary>
public sealed class ParticleEmitter
{
    public float Value { get; init; }
    public int Index { get; init; }
    public required XxModel Model { get; init; }
    public byte[] Flags { get; init; } = [];
    public ushort Unknown1 { get; init; }
    /// <summary>Keys of 17 floats (72 bytes with the frame).</summary>
    public IReadOnlyList<ParticleKey> Curve17 { get; init; } = [];
    /// <summary>Keys of 7 floats (32 bytes with the frame): three zeros, an angle, a size vector.</summary>
    public IReadOnlyList<ParticleKey> Curve7 { get; init; } = [];
    public string AttachFrame { get; init; } = "";
    public byte[] Trailer { get; init; } = [];
}

/// <summary>
/// .ptcl: particle effects of a stage (mpNN_*.ptcl) or an event (ekNN.ptcl). Verified on all 9 files:
/// <code>
/// int32 version (1), byte 0, int32 emitter count
/// per emitter:
///   float, byte 0, int32 index
///   .xx body in format 1 (frames, materials, textures; no .xx header)
///   8 flag bytes, uint16, int32 n, n x (int32 frame + 17 floats), int32 m, m x (int32 frame + 7 floats)
///   attach frame name (int32 length + NOT-encoded), 9 bytes
/// optional footer (2 of 9 files), not decoded
/// </code>
/// </summary>
public sealed class PtclFile
{
    public int Version { get; init; }
    public IReadOnlyList<ParticleEmitter> Emitters { get; init; } = [];
    /// <summary>Bytes after the last emitter: only in mp09_03 (66) and ek06 (3251); meaning unknown, kept raw.</summary>
    public byte[] Footer { get; init; } = [];

    public static PtclFile Read(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data, writable: false), ShiftJis.Encoding);
        int version = r.ReadInt32();
        r.ReadByte();
        int count = r.ReadInt32();
        var emitters = new List<ParticleEmitter>(count);
        for (int i = 0; i < count; i++)
        {
            float value = r.ReadSingle();
            r.ReadByte();
            int index = r.ReadInt32();
            var model = XxReader.ReadBody(r, 1, []);
            var flags = r.ReadExactBytes(8);
            ushort unknown = r.ReadUInt16();
            var curve17 = ReadKeys(r, 17);
            var curve7 = ReadKeys(r, 7);
            emitters.Add(new ParticleEmitter
            {
                Value = value,
                Index = index,
                Model = model,
                Flags = flags,
                Unknown1 = unknown,
                Curve17 = curve17,
                Curve7 = curve7,
                AttachFrame = r.ReadName(),
                Trailer = r.ReadExactBytes(9),
            });
        }
        var footer = r.ReadBytes((int)(data.Length - r.BaseStream.Position));
        return new PtclFile { Version = version, Emitters = emitters, Footer = footer };
    }

    static List<ParticleKey> ReadKeys(BinaryReader r, int floats)
    {
        int count = r.ReadInt32();
        if (count is < 0 or > 100_000)
            throw new InvalidDataException($".ptcl: implausible key count {count}.");
        var keys = new List<ParticleKey>(count);
        for (int k = 0; k < count; k++)
        {
            int frame = r.ReadInt32();
            var values = new float[floats];
            for (int v = 0; v < floats; v++)
                values[v] = r.ReadSingle();
            keys.Add(new ParticleKey(frame, values));
        }
        return keys;
    }
}

/// <summary>
/// .glare: bloom/glare settings of a stage, 32 bytes (verified on all 9 files):
/// int32 version, byte, float intensity (1.3), byte, float threshold (0.25), float, float (0.3, 0.3),
/// byte, float (3.0), float (2.0), byte.
/// </summary>
public sealed class GlareFile
{
    public int Version { get; init; }
    public byte[] Flags { get; init; } = [];
    public float[] Values { get; init; } = [];

    public static GlareFile Read(byte[] data)
    {
        if (data.Length != 32)
            throw new InvalidDataException($".glare: expected 32 bytes, got {data.Length}.");
        using var r = new BinaryReader(new MemoryStream(data, writable: false));
        int version = r.ReadInt32();
        byte f0 = r.ReadByte();
        float v0 = r.ReadSingle();
        byte f1 = r.ReadByte();
        float v1 = r.ReadSingle(), v2 = r.ReadSingle(), v3 = r.ReadSingle();
        byte f2 = r.ReadByte();
        float v4 = r.ReadSingle(), v5 = r.ReadSingle();
        byte f3 = r.ReadByte();
        return new GlareFile { Version = version, Flags = [f0, f1, f2, f3], Values = [v0, v1, v2, v3, v4, v5] };
    }
}
