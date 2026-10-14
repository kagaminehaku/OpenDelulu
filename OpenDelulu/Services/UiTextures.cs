using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenDelulu.Engine;
using OpenDelulu.Formats.Imaging;

namespace OpenDelulu.Services;

/// <summary>
/// The original game's 2D interface textures (stNN_*.tga/.bmp in sb3_0200.pp, Bonus Disc additions in sb3x_1200.pp)
/// as Avalonia bitmaps, cached per game folder. The exe asks for ".tga"/".bmp" names; the archives store them as ".ema".
/// Missing or undecodable textures give null, so views fall back to their own look.
/// </summary>
public sealed class UiTextures(GameFileSystem fs)
{
    static readonly string[] Archives = ["sb3x_1200.pp", "sb3_0200.pp"];

    readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Bitmap? Get(string file)
    {
        if (_cache.TryGetValue(file, out var cached))
            return cached;
        Bitmap? bitmap = null;
        string ema = Path.ChangeExtension(file, ".ema");
        foreach (string archive in Archives)
        {
            if (!fs.HasArchive(archive))
                continue;
            var pp = fs.GetArchive(archive);
            string? name = pp.Find(file) is not null ? file : pp.Find(ema) is not null ? ema : null;
            if (name is null)
                continue;
            try
            {
                bitmap = ToBitmap(ImageDecoder.Decode(fs.ReadFile(archive, name), name));
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or InvalidOperationException)
            {
                // Headless runs have no bitmap support; a broken texture just keeps the fallback look.
            }
            break;
        }
        _cache[file] = bitmap;
        return bitmap;
    }

    public static unsafe Bitmap ToBitmap(RgbaImage image)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var locked = bitmap.Lock();
        var span = new Span<byte>((void*)locked.Address, locked.RowBytes * image.Height);
        image.CopyToBgra(span, locked.RowBytes);
        return bitmap;
    }
}
