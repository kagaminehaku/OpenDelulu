using System.Buffers.Binary;

namespace OpenDelulu.Formats.Imaging;

/// <summary>Windows BMP: uncompressed 1/4/8/16/24/32-bit with BITMAPINFOHEADER or later.</summary>
public static class BmpDecoder
{
    public static RgbaImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 54)
            throw new InvalidDataException("BMP header truncated.");

        int pixelOffset = BinaryPrimitives.ReadInt32LittleEndian(data[10..]);
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(data[14..]);
        int width = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(data[22..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(data[28..]);
        int compression = BinaryPrimitives.ReadInt32LittleEndian(data[30..]);
        int paletteCount = BinaryPrimitives.ReadInt32LittleEndian(data[46..]);

        bool topDown = rawHeight < 0;
        int height = Math.Abs(rawHeight);
        if (width <= 0 || height == 0)
            throw new InvalidDataException("BMP has invalid size.");
        if (compression is not (0 or 3))
            throw new NotSupportedException($"BMP compression {compression} not supported.");

        uint maskR = 0x00FF0000, maskG = 0x0000FF00, maskB = 0x000000FF, maskA = 0;
        if (bits == 16)
        {
            maskR = 0x7C00; maskG = 0x03E0; maskB = 0x001F;
        }
        if (compression == 3)
        {
            int masks = 14 + headerSize;
            if (headerSize >= 52 || data.Length >= masks + 12)
            {
                int at = headerSize >= 52 ? 14 + 40 : masks;
                maskR = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
                maskG = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
                maskB = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 8)..]);
                if (headerSize >= 56)
                    maskA = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 12)..]);
            }
        }
        else if (bits == 32 && headerSize >= 56)
        {
            maskA = BinaryPrimitives.ReadUInt32LittleEndian(data[(14 + 52)..]);
        }

        ReadOnlySpan<byte> palette = default;
        if (bits <= 8)
        {
            int count = paletteCount > 0 ? paletteCount : 1 << bits;
            palette = data.Slice(14 + headerSize, count * 4);
        }

        int stride = ((width * bits + 31) / 32) * 4;
        var image = new RgbaImage(width, height);
        var dst = image.Pixels;

        for (int row = 0; row < height; row++)
        {
            var line = data.Slice(pixelOffset + row * stride, stride);
            int y = topDown ? row : height - 1 - row;
            int o = y * width * 4;
            for (int x = 0; x < width; x++, o += 4)
            {
                switch (bits)
                {
                    case 24:
                        dst[o] = line[x * 3 + 2]; dst[o + 1] = line[x * 3 + 1]; dst[o + 2] = line[x * 3]; dst[o + 3] = 255;
                        break;
                    case 32:
                    case 16:
                        uint v = bits == 32
                            ? BinaryPrimitives.ReadUInt32LittleEndian(line[(x * 4)..])
                            : BinaryPrimitives.ReadUInt16LittleEndian(line[(x * 2)..]);
                        dst[o] = Channel(v, maskR);
                        dst[o + 1] = Channel(v, maskG);
                        dst[o + 2] = Channel(v, maskB);
                        dst[o + 3] = maskA == 0 ? (byte)255 : Channel(v, maskA);
                        break;
                    default:
                        int perByte = 8 / bits;
                        int shift = 8 - bits - (x % perByte) * bits;
                        int index = (line[x / perByte] >> shift) & ((1 << bits) - 1);
                        var entry = palette.Slice(index * 4, 4);
                        dst[o] = entry[2]; dst[o + 1] = entry[1]; dst[o + 2] = entry[0]; dst[o + 3] = 255;
                        break;
                }
            }
        }
        return image;
    }

    static byte Channel(uint value, uint mask)
    {
        if (mask == 0)
            return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        uint v = (value & mask) >> shift;
        return (byte)(v * 255 / max);
    }
}
