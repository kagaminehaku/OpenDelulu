using System.Numerics;
using System.Runtime.InteropServices;

namespace OpenDelulu.Formats.Pp;

/// <summary>
/// The SB3 .pp "encryption": every byte is negated (<c>b' = -b mod 256</c>).
/// The operation is its own inverse, so the same call encodes and decodes.
/// Used for both the file name table and the subfile payloads.
/// </summary>
public static class Sb3Cipher
{
    public static void Negate(Span<byte> data)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && data.Length >= Vector<byte>.Count)
        {
            var vectors = MemoryMarshal.Cast<byte, Vector<byte>>(data);
            for (int v = 0; v < vectors.Length; v++)
                vectors[v] = Vector<byte>.Zero - vectors[v];
            i = vectors.Length * Vector<byte>.Count;
        }
        for (; i < data.Length; i++)
            data[i] = (byte)-data[i];
    }
}
