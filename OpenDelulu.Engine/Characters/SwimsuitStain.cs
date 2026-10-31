using System.Globalization;
using OpenDelulu.Formats.Imaging;

namespace OpenDelulu.Engine.Characters;

/// <summary>
/// The wet stain of the swimsuit (exe: sb_SukeBase, "SimiShader": colour = suit − g_fLerp × stain, or for the areas the
/// suit marks see-through, "SukeShader": colour and opacity = suit − g_fLerp × stain; the effect's ps_1_1 code is in
/// the exe's resources at file offset 0x2101D4 / 0x210324). Eleven areas per
/// suit, rows of lz00 starting at lbNN column 10 (21 = rows 21..31: breasts, belly, sides, crotch front and back, hips…),
/// each a rectangle of the 512×512 suit texture (columns 1..4: width, height, x, y) where the stain sk01 (lbNN column 6,
/// 128×128 grey with alpha) is stamped, scaled to it. The oil and H scenes raise area 6 (0-based: the crotch panel, lz00
/// row first + 6; exe 0x4A5989 / 0x4B1DC5) with the girl's arousal; other areas are not driven by the exe either.
/// </summary>
public sealed class SwimsuitStain
{
    /// <summary>The crotch area (0-based 6 = the seventh row: the panel between her legs on the suit texture).</summary>
    public const int Crotch = 6;

    readonly RgbaImage _stain;
    readonly (int W, int H, int X, int Y)[] _areas;
    readonly int[] _modes;
    readonly float[] _levels;

    SwimsuitStain(RgbaImage stain, (int, int, int, int)[] areas, int[] modes)
    {
        _stain = stain;
        _areas = areas;
        _modes = modes;
        _levels = new float[areas.Length];
    }

    /// <summary>Some wet area turns see-through (mode 2): the suit then has to be drawn blended over her skin.</summary>
    public bool IsSeeThrough => Enumerable.Range(0, _areas.Length).Any(a => _levels[a] > 0.001f && Mode(a) == 2);

    int Mode(int area) => area < _modes.Length ? _modes[area] : 1;

    /// <summary>The stain of a character's swimsuit, or null when the suit has none.</summary>
    public static SwimsuitStain? Load(GameFileSystem fs, string tableArchive, Character girl)
    {
        if (girl.Swimsuit is not { Stain: { } stainRef, StainRows: > 0 } suit)
            return null;
        try
        {
            var stain = ImageDecoder.Decode(fs.ReadReferenced(stainRef.Archive, stainRef.File), stainRef.File);
            var table = fs.ReadTable(tableArchive, "lz00_00_00_00_00.lst");
            var areas = new (int, int, int, int)[11];
            for (int i = 0; i < areas.Length; i++)
            {
                string row = (suit.StainRows + i).ToString(CultureInfo.InvariantCulture);
                if (table.Rows.FirstOrDefault(r => r.Length > 4 && r[0] == row) is { } cells)
                {
                    int Int(int c) => int.TryParse(cells[c], out int v) ? v : 0;
                    areas[i] = (Int(1), Int(2), Int(3), Int(4));
                }
            }
            return new SwimsuitStain(stain, areas, [.. suit.StainModes]);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>0..1 wetness of an area.</summary>
    public float this[int area]
    {
        get => _levels[area];
        set => _levels[area] = Math.Clamp(value, 0f, 1f);
    }

    public bool IsDry => _levels.All(l => l <= 0.001f);

    /// <summary>A copy of <paramref name="suit"/> with the stains of the current levels.</summary>
    public RgbaImage Apply(RgbaImage suit)
    {
        var result = new RgbaImage(suit.Width, suit.Height, (byte[])suit.Pixels.Clone());
        float scaleX = suit.Width / 512f, scaleY = suit.Height / 512f;
        for (int a = 0; a < _areas.Length; a++)
        {
            var (w, h, x, y) = _areas[a];
            float level = _levels[a];
            int mode = Mode(a);
            if (level <= 0.001f || w <= 0 || h <= 0 || mode == 0)
                continue;
            int x0 = (int)(x * scaleX), y0 = (int)(y * scaleY), pw = Math.Max(1, (int)(w * scaleX)), ph = Math.Max(1, (int)(h * scaleY));
            for (int py = 0; py < ph; py++)
            {
                int ty = y0 + py;
                if (ty < 0 || ty >= suit.Height)
                    continue;
                int sy = Math.Min(_stain.Height - 1, py * _stain.Height / ph);
                for (int px = 0; px < pw; px++)
                {
                    int tx = x0 + px;
                    if (tx < 0 || tx >= suit.Width)
                        continue;
                    int sx = Math.Min(_stain.Width - 1, px * _stain.Width / pw);
                    int s = (sy * _stain.Width + sx) * 4, t = (ty * suit.Width + tx) * 4;
                    // The exe's ps_1_1 shaders: SimiShader rgb = suit − g_fLerp × stain (alpha kept); SukeShader does the
                    // same to all four channels, so the wet cloth also loses opacity (sk01's alpha is 133 at most).
                    int channels = mode == 2 ? 4 : 3;
                    for (int c = 0; c < channels; c++)
                        result.Pixels[t + c] = (byte)Math.Max(0, result.Pixels[t + c] - level * _stain.Pixels[s + c]);
                }
            }
        }
        return result;
    }
}
