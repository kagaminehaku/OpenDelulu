namespace OpenDelulu.Formats.Imaging;

/// <summary>Decoded image: 8-bit RGBA, rows top to bottom, no padding.</summary>
public sealed class RgbaImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;

    public RgbaImage(int width, int height) : this(width, height, new byte[width * height * 4]) { }

    /// <summary>True if any pixel is not fully opaque.</summary>
    public bool HasAlpha()
    {
        for (int i = 3; i < Pixels.Length; i += 4)
            if (Pixels[i] != 255)
                return true;
        return false;
    }

    /// <summary>Copies into a BGRA buffer (the native order of most UI bitmaps).</summary>
    public void CopyToBgra(Span<byte> destination, int destinationStride)
    {
        for (int y = 0; y < Height; y++)
        {
            var src = Pixels.AsSpan(y * Width * 4, Width * 4);
            var dst = destination.Slice(y * destinationStride, Width * 4);
            for (int x = 0; x < src.Length; x += 4)
            {
                dst[x] = src[x + 2];
                dst[x + 1] = src[x + 1];
                dst[x + 2] = src[x];
                dst[x + 3] = src[x + 3];
            }
        }
    }
}
