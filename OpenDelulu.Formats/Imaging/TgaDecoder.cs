using System.Buffers.Binary;

namespace OpenDelulu.Formats.Imaging;

/// <summary>Truevision TGA: uncompressed/RLE true-color, grayscale and color-mapped images.</summary>
public static class TgaDecoder
{
    public static RgbaImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18)
            throw new InvalidDataException("TGA header truncated.");

        int idLength = data[0];
        int colorMapType = data[1];
        int imageType = data[2];
        int mapFirst = BinaryPrimitives.ReadUInt16LittleEndian(data[3..]);
        int mapLength = BinaryPrimitives.ReadUInt16LittleEndian(data[5..]);
        int mapEntryBits = data[7];
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        int bits = data[16];
        int descriptor = data[17];
        bool topDown = (descriptor & 0x20) != 0;
        bool rightToLeft = (descriptor & 0x10) != 0;

        if (width == 0 || height == 0)
            throw new InvalidDataException("TGA has zero size.");

        int pos = 18 + idLength;
        byte[]? palette = null;
        if (colorMapType == 1)
        {
            int entryBytes = (mapEntryBits + 7) / 8;
            palette = new byte[(mapFirst + mapLength) * 4];
            for (int i = 0; i < mapLength; i++)
                ReadColor(data.Slice(pos + i * entryBytes), mapEntryBits, palette.AsSpan((mapFirst + i) * 4, 4));
            pos += mapLength * entryBytes;
        }

        bool rle = imageType is 9 or 10 or 11;
        int baseType = rle ? imageType - 8 : imageType;
        if (baseType is not (1 or 2 or 3))
            throw new NotSupportedException($"TGA image type {imageType} not supported.");

        int pixelBytes = (bits + 7) / 8;
        int pixelCount = width * height;
        var image = new RgbaImage(width, height);
        Span<byte> color = stackalloc byte[4];
        var src = data[pos..];
        int s = 0;
        int written = 0;

        void Emit(ReadOnlySpan<byte> raw, Span<byte> rgba)
        {
            switch (baseType)
            {
                case 1:
                    int index = pixelBytes == 1 ? raw[0] : BinaryPrimitives.ReadUInt16LittleEndian(raw);
                    palette.AsSpan(index * 4, 4).CopyTo(rgba);
                    break;
                case 3:
                    rgba[0] = rgba[1] = rgba[2] = raw[0];
                    rgba[3] = pixelBytes > 1 ? raw[1] : (byte)255;
                    break;
                default:
                    ReadColor(raw, bits, rgba);
                    break;
            }
        }

        void Store(int i, ReadOnlySpan<byte> rgba, RgbaImage img)
        {
            int x = i % width, y = i / width;
            if (!topDown) y = height - 1 - y;
            if (rightToLeft) x = width - 1 - x;
            rgba.CopyTo(img.Pixels.AsSpan((y * width + x) * 4, 4));
        }

        while (written < pixelCount)
        {
            if (!rle)
            {
                Emit(src.Slice(s, pixelBytes), color);
                s += pixelBytes;
                Store(written++, color, image);
                continue;
            }

            int packet = src[s++];
            int count = (packet & 0x7F) + 1;
            if ((packet & 0x80) != 0)
            {
                Emit(src.Slice(s, pixelBytes), color);
                s += pixelBytes;
                for (int i = 0; i < count && written < pixelCount; i++)
                    Store(written++, color, image);
            }
            else
            {
                for (int i = 0; i < count && written < pixelCount; i++)
                {
                    Emit(src.Slice(s, pixelBytes), color);
                    s += pixelBytes;
                    Store(written++, color, image);
                }
            }
        }
        return image;
    }

    static void ReadColor(ReadOnlySpan<byte> raw, int bits, Span<byte> rgba)
    {
        switch (bits)
        {
            case 32:
                rgba[0] = raw[2]; rgba[1] = raw[1]; rgba[2] = raw[0]; rgba[3] = raw[3];
                break;
            case 24:
                rgba[0] = raw[2]; rgba[1] = raw[1]; rgba[2] = raw[0]; rgba[3] = 255;
                break;
            case 15:
            case 16:
                int v = BinaryPrimitives.ReadUInt16LittleEndian(raw);
                rgba[0] = Expand5((v >> 10) & 31);
                rgba[1] = Expand5((v >> 5) & 31);
                rgba[2] = Expand5(v & 31);
                rgba[3] = bits == 16 && (v & 0x8000) == 0 ? (byte)0 : (byte)255;
                break;
            default:
                throw new NotSupportedException($"TGA {bits}-bit color not supported.");
        }
    }

    internal static byte Expand5(int v) => (byte)((v << 3) | (v >> 2));
}
