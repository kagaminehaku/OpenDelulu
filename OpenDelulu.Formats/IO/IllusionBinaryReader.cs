using System.Numerics;

namespace OpenDelulu.Formats.IO;

/// <summary>Reading helpers for the primitive types shared by .xx/.xa files (little endian, Direct3D conventions).</summary>
public static class IllusionBinaryReader
{
    /// <summary>Length-prefixed name whose bytes are bitwise inverted (<c>~b</c>), Shift-JIS, NUL terminated.</summary>
    public static string ReadName(this BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > 0x10000)
            throw new InvalidDataException($"Implausible name length {length} at 0x{reader.BaseStream.Position - 4:X}.");
        return reader.ReadFixedName(length);
    }

    /// <summary>Fixed-width inverted name (no length prefix).</summary>
    public static string ReadFixedName(this BinaryReader reader, int length)
    {
        Span<byte> buffer = length <= 512 ? stackalloc byte[length] : new byte[length];
        reader.BaseStream.ReadExactly(buffer);
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = (byte)~buffer[i];
        return ShiftJis.GetString(buffer);
    }

    public static Vector2 ReadVector2(this BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle());

    public static Vector3 ReadVector3(this BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    /// <summary>Direct3D color: R, G, B, A as floats.</summary>
    public static Vector4 ReadColor4(this BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    /// <summary>
    /// 16 floats in row-major order. Direct3D uses row vectors (<c>v * M</c>), which is exactly the
    /// <see cref="Matrix4x4"/> convention, so no transposition is needed.
    /// </summary>
    public static Matrix4x4 ReadMatrix(this BinaryReader reader) => new(
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    public static byte[] ReadExactBytes(this BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
            throw new EndOfStreamException($"Expected {count} bytes, got {bytes.Length}.");
        return bytes;
    }
}
