using System.Numerics;

namespace OpenDelulu.Engine.Assets;

/// <summary>How a <see cref="TextureLayer"/> combines with the lit texture (Direct3D 9 texture stage 1 colour op).</summary>
public enum LayerBlend
{
    /// <summary>D3DTOP_MODULATE: lit colour × layer.</summary>
    Modulate = 1,
    /// <summary>D3DTOP_ADD: lit colour + layer (the shine of skin, swimsuits, nails, rocks, oil drops).</summary>
    Add = 2,
    /// <summary>D3DTOP_ADDSIGNED: lit colour + layer − 0.5 (chrome: shower, swing).</summary>
    AddSigned = 3,
}

/// <summary>Where a <see cref="TextureLayer"/> takes its texture coordinates (D3DTSS_TEXCOORDINDEX).</summary>
public enum LayerCoordinates
{
    /// <summary>A UV set of the mesh.</summary>
    Uv = 0,
    /// <summary>D3DTSS_TCI_CAMERASPACENORMAL: x, y of the camera-space normal.</summary>
    CameraNormal = 1,
    /// <summary>D3DTSS_TCI_CAMERASPACEPOSITION: x, y of the camera-space position.</summary>
    CameraPosition = 2,
    /// <summary>D3DTSS_TCI_CAMERASPACEREFLECTIONVECTOR: x, y of the reflected view vector (fake environment shine).</summary>
    Reflection = 3,
}

/// <summary>
/// The second texture of a material (slot 1 of the .xx material), drawn by the exe as texture stage 1 (0x54F0D0). The
/// 16 bytes after the texture name hold: byte 0 the stage's colour op (0/6 modulate, 7 add, 2 add signed; 1 is the
/// alpha-blend setup of stage 0), byte 2 the coordinate count (0 = two), byte 3 the coordinate source and byte 4 the UV
/// set. No texture transform is set, so reflection coordinates are used as they are, with wrapping: the 16×16
/// highlight maps <c>sp01</c> (skin, nails) and <c>sp11</c> (swimsuits) give the characters' gloss.
/// </summary>
public sealed record TextureLayer(int TextureIndex, LayerBlend Blend, LayerCoordinates Coordinates, int UvSet)
{
    /// <summary>The layer described by a texture slot's bytes, or null when it is not a second stage the exe draws.</summary>
    public static (LayerBlend Blend, LayerCoordinates Coordinates, int UvSet)? Describe(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 5)
            return null;
        LayerBlend? blend = bytes[0] switch
        {
            0 or 6 => LayerBlend.Modulate,
            7 => LayerBlend.Add,
            2 => LayerBlend.AddSigned,
            _ => null,
        };
        if (blend is null || bytes[3] > 3)
            return null;
        return (blend.Value, (LayerCoordinates)bytes[3], bytes[4]);
    }

    /// <summary>
    /// Fills the per-vertex layer data (u, v, blend, 1) for camera-dependent coordinates: Direct3D's camera space
    /// (left-handed, x right, y up), reflection = 2(N·E)N − E with E pointing from the vertex to the eye.
    /// </summary>
    public void Evaluate(RenderMesh mesh, Matrix4x4 cameraToWorld, Span<float> data)
    {
        var vertices = mesh.Vertices;
        int count = vertices.Length / RenderModel.VertexStride;
        if (!Matrix4x4.Invert(cameraToWorld, out var view))
            view = Matrix4x4.Identity;
        float blend = (float)Blend;
        for (int v = 0; v < count; v++)
        {
            int o = v * RenderModel.VertexStride;
            Vector2 uv;
            if (Coordinates == LayerCoordinates.Uv)
            {
                uv = mesh.LayerUV is { } layerUV ? layerUV[v] : new Vector2(vertices[o + 6], vertices[o + 7]);
            }
            else
            {
                var position = Vector3.Transform(new Vector3(vertices[o], vertices[o + 1], vertices[o + 2]), view);
                var normal = Vector3.TransformNormal(new Vector3(vertices[o + 3], vertices[o + 4], vertices[o + 5]), view);
                if (normal.LengthSquared() > 1e-12f)
                    normal = Vector3.Normalize(normal);
                switch (Coordinates)
                {
                    case LayerCoordinates.CameraNormal:
                        uv = new Vector2(normal.X, normal.Y);
                        break;
                    case LayerCoordinates.CameraPosition:
                        uv = new Vector2(position.X, position.Y);
                        break;
                    default:
                        var toEye = position.LengthSquared() > 1e-12f ? -Vector3.Normalize(position) : Vector3.UnitZ;
                        var reflection = 2 * Vector3.Dot(normal, toEye) * normal - toEye;
                        uv = new Vector2(reflection.X, reflection.Y);
                        break;
                }
            }
            data[v * 4] = uv.X;
            data[v * 4 + 1] = uv.Y;
            data[v * 4 + 2] = blend;
            data[v * 4 + 3] = 1;
        }
    }
}
