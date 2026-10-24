namespace OpenDelulu.Engine.Assets;

/// <summary>
/// The water shader of a mesh whose frame asks for shader 3 (the exe's gi_dx9water, after the DirectX SDK BumpWaves
/// sample): the texture is looked up through a turning 2×2 bump matrix applied to a procedural V8U8 wave map, so the
/// sea, pools and falls shimmer. Everything comes from byte 5 of the submesh's first unknown block.
/// </summary>
/// <remarks>
/// The wave map (0x539B40) is 128×128: for texel (x, y) with fx = x / 128 − 0.5, fy = y / 128 − 0.5,
/// a = 150·<see cref="Wave"/>·(fx + fy), b = 140·<see cref="Wave"/>·(0.85·fx − fy), du = 32·cos a + 16·cos b and
/// dv = 32·sin a + 16·sin b (signed bytes). Each frame the angle grows by 0.004 × <see cref="Turn"/> per tick (0x538E70);
/// the bump matrix is <see cref="Scale"/> × (sin, −cos; cos, sin) of that angle.
/// </remarks>
/// <param name="Scale">Size of the distortion in texture units: 0.01 × (bits 1–4 + 1).</param>
/// <param name="Turn">+1 or −1 (bit 0): which way the bump matrix turns.</param>
/// <param name="Wave">Frequency of the wave map (bits 5–7: 1.0, 0.9, 0.8, 0.7, 0.5, 0.4, 0.3, 0.2).</param>
public readonly record struct WaterBump(float Scale, float Turn, float Wave)
{
    static readonly float[] Waves = [1.0f, 0.9f, 0.8f, 0.7f, 0.5f, 0.4f, 0.3f, 0.2f];

    /// <summary>
    /// Radians the bump matrix turns per second. The exe adds 0.004 per tick of its frame timer, assumed to be
    /// milliseconds (the BumpWaves sample turns at 9 rad/s).
    /// </summary>
    public const float RadiansPerSecond = 4;

    public static WaterBump FromByte(byte value) => new(
        Scale: 0.01f * (((value >> 1) & 0xF) + 1),
        Turn: (value & 1) == 0 ? 1 : -1,
        Wave: Waves[(value >> 5) & 7]);

    /// <summary>The bump matrix angle after <paramref name="seconds"/> of play.</summary>
    public float AngleAt(double seconds) => (float)(seconds * RadiansPerSecond % (2 * Math.PI)) * Turn;
}
