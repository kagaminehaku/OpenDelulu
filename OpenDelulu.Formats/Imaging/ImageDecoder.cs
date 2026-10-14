using System.Buffers.Binary;
using System.Text;

namespace OpenDelulu.Formats.Imaging;

/// <summary>Decodes the image containers found in SB3 archives: .bmp, .tga and .ema.</summary>
public static class ImageDecoder
{
    public static readonly string[] Extensions = [".bmp", ".tga", ".ema"];

    public static bool IsImage(string fileName) =>
        Extensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>Decodes a standalone image file, choosing the decoder from the name and signature.</summary>
    public static RgbaImage Decode(ReadOnlySpan<byte> data, string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext == ".ema")
            return DecodeEma(data);
        if (data.Length >= 2 && data[0] == 'B' && data[1] == 'M')
            return BmpDecoder.Decode(data);
        if (ext == ".bmp")
        {
            // Signature zeroed (as inside .xx/.ema); restore it on a copy.
            var copy = data.ToArray();
            copy[0] = (byte)'B';
            copy[1] = (byte)'M';
            return BmpDecoder.Decode(copy);
        }
        return TgaDecoder.Decode(data);
    }

    /// <summary>
    /// .ema = int32 width, int32 height, 5-byte extension (".bmp\0" / ".tga\0"), then the image file
    /// (BMP signature zeroed).
    /// </summary>
    public static RgbaImage DecodeEma(ReadOnlySpan<byte> data)
    {
        if (data.Length < 13)
            throw new InvalidDataException("EMA header truncated.");
        string ext = Encoding.ASCII.GetString(data.Slice(8, 4)).ToLowerInvariant();
        return Decode(data[13..], "image" + ext);
    }

    /// <summary>Reads the declared size of an .ema without decoding it.</summary>
    public static (int Width, int Height) EmaSize(ReadOnlySpan<byte> data) =>
        (BinaryPrimitives.ReadInt32LittleEndian(data), BinaryPrimitives.ReadInt32LittleEndian(data[4..]));
}
