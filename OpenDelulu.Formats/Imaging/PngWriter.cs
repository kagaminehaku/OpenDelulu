using System.Buffers.Binary;
using System.IO.Compression;

namespace OpenDelulu.Formats.Imaging;

/// <summary>Minimal PNG encoder (RGBA8, no filtering) for exporting textures.</summary>
public static class PngWriter
{
    public static void Write(RgbaImage image, Stream output)
    {
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], image.Height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // RGBA
        WriteChunk(output, "IHDR", header);

        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = image.Width * 4;
            for (int y = 0; y < image.Height; y++)
            {
                zlib.WriteByte(0); // filter: none
                zlib.Write(image.Pixels, y * stride, stride);
            }
        }
        WriteChunk(output, "IDAT", raw.GetBuffer().AsSpan(0, (int)raw.Length));
        WriteChunk(output, "IEND", []);
    }

    public static byte[] Encode(RgbaImage image)
    {
        using var stream = new MemoryStream();
        Write(image, stream);
        return stream.ToArray();
    }

    static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);

        Span<byte> typeBytes = [(byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]];
        output.Write(typeBytes);
        output.Write(data);

        uint crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        output.Write(buffer);
    }

    static readonly uint[] CrcTable = CreateCrcTable();

    static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
