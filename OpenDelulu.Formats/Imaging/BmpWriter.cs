namespace OpenDelulu.Formats.Imaging;

/// <summary>Writes 24-bit Windows bitmaps (bottom-up rows, as the exe's screenshots in data/cap).</summary>
public static class BmpWriter
{
    /// <param name="rgba">RGBA rows from the top.</param>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        int stride = (width * 3 + 3) & ~3, size = 54 + stride * height;
        var bmp = new byte[size];
        void Int(int at, int value) => BitConverter.TryWriteBytes(bmp.AsSpan(at), value);
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        Int(2, size);
        Int(10, 54);
        Int(14, 40);
        Int(18, width);
        Int(22, height);
        bmp[26] = 1;
        bmp[28] = 24;
        Int(34, stride * height);
        for (int y = 0; y < height; y++)
        {
            int row = 54 + (height - 1 - y) * stride;
            for (int x = 0; x < width; x++)
            {
                int s = (y * width + x) * 4, d = row + x * 3;
                bmp[d] = rgba[s + 2];
                bmp[d + 1] = rgba[s + 1];
                bmp[d + 2] = rgba[s];
            }
        }
        return bmp;
    }
}
