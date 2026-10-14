using System.Buffers.Binary;
using OpenDelulu.Formats.IO;

namespace OpenDelulu.Formats.Voice;

/// <summary>Mouth shape of one mora, by vowel. <see cref="Hold"/> keeps the previous shape (long vowel "ー").</summary>
public enum MouthShape : byte
{
    Hold = 0,
    A = 1,
    I = 2,
    U = 3,
    E = 4,
    O = 5,
}

/// <summary>
/// Lip-sync data for one voice line (.vss next to the .wav of the same name). Verified on all 6663 files:
/// <code>
/// float  1.0
/// int32  duration in milliseconds (equals the .wav length)
/// int32  sample count at 44.1 kHz
/// int32  N
/// N x (uint16 Shift-JIS kana, uint16 0)   the last entry is empty (silence after the line)
/// int32  N
/// N x byte  mouth shape (MouthShape)
/// </code>
/// No per-mora timing is stored: morae are spread evenly over the duration.
/// </summary>
public sealed class VssFile
{
    public float Scale { get; init; }
    public int DurationMs { get; init; }
    public int SampleCount { get; init; }
    /// <summary>One entry per mora; the last one is the closing silence (empty text).</summary>
    public IReadOnlyList<(string Kana, MouthShape Shape)> Morae { get; init; } = [];

    public string Text => string.Concat(Morae.Select(m => m.Kana));

    public static VssFile Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20)
            throw new InvalidDataException(".vss too short.");
        float scale = BinaryPrimitives.ReadSingleLittleEndian(data);
        int duration = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        int samples = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        int shapesAt = 16 + count * 4;
        if (count < 0 || shapesAt + 4 + count != data.Length || BinaryPrimitives.ReadInt32LittleEndian(data[shapesAt..]) != count)
            throw new InvalidDataException(".vss layout mismatch.");

        var morae = new (string, MouthShape)[count];
        Span<byte> kana = stackalloc byte[2];
        for (int i = 0; i < count; i++)
        {
            ushort code = BinaryPrimitives.ReadUInt16LittleEndian(data[(16 + i * 4)..]);
            BinaryPrimitives.WriteUInt16LittleEndian(kana, code);
            string text = code == 0 ? "" : ShiftJis.GetString(kana);
            morae[i] = (text, (MouthShape)data[shapesAt + 4 + i]);
        }
        return new VssFile { Scale = scale, DurationMs = duration, SampleCount = samples, Morae = morae };
    }

    /// <summary>Mouth shape at <paramref name="milliseconds"/> into the line (morae evenly spaced).</summary>
    public MouthShape ShapeAt(double milliseconds)
    {
        if (Morae.Count == 0 || milliseconds < 0 || milliseconds >= DurationMs)
            return MouthShape.Hold;
        int index = (int)(milliseconds / DurationMs * Morae.Count);
        for (int i = Math.Min(index, Morae.Count - 1); i >= 0; i--)
            if (Morae[i].Shape != MouthShape.Hold)
                return Morae[i].Shape;
        return MouthShape.A;
    }

    /// <summary>How open the mouth is for a shape, 0..1 (drives the "speaking" morph).</summary>
    public static float Openness(MouthShape shape) => shape switch
    {
        MouthShape.A => 1f,
        MouthShape.O => 0.75f,
        MouthShape.E => 0.6f,
        MouthShape.U => 0.35f,
        MouthShape.I => 0.3f,
        _ => 0f,
    };
}
