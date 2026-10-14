using System.Numerics;
using OpenDelulu.Engine.Assets;

namespace OpenDelulu.Engine.Oil;

/// <summary>
/// Sun oil on the skin: a 0..1 amount per vertex of the body's skin meshes, painted around the rubbing hand and
/// drawn as shine (see <see cref="RenderMesh.Gloss"/>). The exe paints into a 512x512 buffer instead
/// (sb_OilScene, sb_Coating "手はわし処理"); per-vertex is enough for the body's ~9000 vertices.
/// </summary>
public sealed class OilCoating
{
    readonly List<RenderMesh> _skin;
    readonly int _vertexCount;

    public OilCoating(RenderModel model)
    {
        _skin = model.Meshes.Where(IsSkin).ToList();
        foreach (var mesh in _skin)
            mesh.Gloss ??= new float[mesh.SourceVertices.Length];
        _vertexCount = _skin.Sum(m => m.SourceVertices.Length);
    }

    /// <summary>The body's skin: every submesh of o01_P_Body (arms, chest, belly, back, hips, legs, hands).</summary>
    static bool IsSkin(RenderMesh mesh) => mesh.PartName == "body" && mesh.FrameName == "o01_P_Body";

    /// <summary>Share of the skin covered (0..1).</summary>
    public float Coverage { get; private set; }

    /// <summary>Adds oil around <paramref name="point"/> (world space, current pose); returns true if anything changed.</summary>
    public bool Paint(Vector3 point, float radius, float amount)
    {
        float radiusSquared = radius * radius;
        bool changed = false;
        foreach (var mesh in _skin)
        {
            var gloss = mesh.Gloss!;
            var vertices = mesh.Vertices;
            for (int v = 0; v < gloss.Length; v++)
            {
                int o = v * RenderModel.VertexStride;
                float dx = vertices[o] - point.X, dy = vertices[o + 1] - point.Y, dz = vertices[o + 2] - point.Z;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 >= radiusSquared || gloss[v] >= 1)
                    continue;
                float falloff = 1 - MathF.Sqrt(d2) / radius;
                gloss[v] = MathF.Min(1, gloss[v] + amount * falloff * falloff);
                changed = true;
            }
        }
        if (changed)
            Coverage = _skin.Sum(m => m.Gloss!.Count(g => g > 0.5f)) / (float)Math.Max(1, _vertexCount);
        return changed;
    }

    public void Clear()
    {
        foreach (var mesh in _skin)
            Array.Clear(mesh.Gloss!);
        Coverage = 0;
    }
}
