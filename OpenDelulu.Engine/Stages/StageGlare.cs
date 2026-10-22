using OpenDelulu.Formats.Scene;

namespace OpenDelulu.Engine.Stages;

/// <summary>How the glare meshes are coloured in the glare source (the exe's giTechGlareDif/Spe/Emi/Lit).</summary>
public enum GlareColor
{
    /// <summary>Lit diffuse × texture, clamped.</summary>
    Diffuse = 0,
    /// <summary>The specular colour only.</summary>
    Specular = 1,
    /// <summary>Material emissive × texture.</summary>
    Emissive = 2,
    /// <summary>Lit colour × texture.</summary>
    Lit = 3,
}

/// <summary>
/// A stage's .glare (the exe's gi_dx9glare, after the DirectX SDK HDRLighting sample). The meshes whose frame asks for
/// the glare shader (<see cref="Assets.RenderMesh.IsGlareSource"/>) are drawn again, hidden by the rest of the scene,
/// into a source image; it is shrunk to a quarter, spread into a four-line star or a Gaussian bloom, and added onto the
/// frame.
/// </summary>
/// <param name="Star">True: four star lines (two passes of 8 samples each, 0.95 attenuation); false: bloom.</param>
/// <param name="CrossRate">Weight of each star line when the four are summed (g_fCrossRate; 0.25 = their mean).</param>
/// <param name="StarLength">Sample step of the star lines in source texels (x, y).</param>
/// <param name="BloomPasses">Times the horizontal + vertical Gaussian blur is applied.</param>
/// <param name="BloomDeviation">Standard deviation of the 15-tap Gaussian, in texels.</param>
/// <param name="BloomMultiplier">Scale of the Gaussian weights (above 1 brightens with every pass).</param>
public sealed record StageGlare(
    bool Star,
    GlareColor Color,
    float CrossRate,
    float StarLengthX,
    float StarLengthY,
    int BloomPasses,
    float BloomDeviation,
    float BloomMultiplier)
{
    /// <summary>
    /// Reads the fields the exe uses: flag 1 picks star (0) or bloom (1), flag 2 the bloom passes, flag 3 the source
    /// colour; values 1..5 are the cross rate, star lengths and bloom deviation and multiplier. Value 0 (always 1.3) and
    /// flag 0 (always 0) are not understood.
    /// </summary>
    public static StageGlare FromFile(GlareFile file)
    {
        var f = file.Flags;
        var v = file.Values;
        return new StageGlare(
            Star: f[1] == 0,
            Color: (GlareColor)Math.Min((int)f[3], 3),
            CrossRate: v[1],
            StarLengthX: v[2],
            StarLengthY: v[3],
            BloomPasses: Math.Max(1, (int)f[2]),
            BloomDeviation: v[4],
            BloomMultiplier: v[5]);
    }
}
