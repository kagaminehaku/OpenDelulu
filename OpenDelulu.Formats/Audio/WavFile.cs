using System.Buffers.Binary;

namespace OpenDelulu.Formats.Audio;

/// <summary>
/// RIFF/WAVE with uncompressed PCM. Every .wav in the game is 16-bit mono 22050 Hz (voices po/ph/pd),
/// but 8/16-bit and any channel count or rate are accepted.
/// </summary>
public sealed class WavFile
{
    public int Channels { get; init; }
    public int SampleRate { get; init; }
    /// <summary>Interleaved samples.</summary>
    public short[] Samples { get; init; } = [];

    public int Frames => Channels == 0 ? 0 : Samples.Length / Channels;
    public double Duration => SampleRate == 0 ? 0 : (double)Frames / SampleRate;

    public static WavFile Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a RIFF/WAVE file.");

        int format = 0, channels = 0, rate = 0, bits = 0;
        int at = 12;
        while (at + 8 <= data.Length)
        {
            var id = data.Slice(at, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(data[(at + 4)..]);
            int body = at + 8;
            // Some writers put a slightly wrong size on the last chunk; clamp to the file.
            int length = Math.Min(Math.Max(size, 0), data.Length - body);
            if (id.SequenceEqual("fmt "u8))
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(data[body..]);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data[(body + 2)..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(data[(body + 4)..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data[(body + 14)..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (format != 1 || channels is < 1 or > 8 || rate <= 0 || bits is not (8 or 16))
                    throw new InvalidDataException($"Unsupported WAVE format {format}, {channels} ch, {bits} bit.");
                return new WavFile { Channels = channels, SampleRate = rate, Samples = Convert(data.Slice(body, length), bits) };
            }
            at = body + length + (length & 1);
        }
        throw new InvalidDataException("WAVE file has no data chunk.");
    }

    static short[] Convert(ReadOnlySpan<byte> pcm, int bits)
    {
        if (bits == 8)
        {
            var result8 = new short[pcm.Length];
            for (int i = 0; i < pcm.Length; i++)
                result8[i] = (short)((pcm[i] - 128) << 8);
            return result8;
        }
        var result = new short[pcm.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]);
        return result;
    }
}
