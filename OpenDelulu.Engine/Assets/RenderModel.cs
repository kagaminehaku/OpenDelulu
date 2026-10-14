using System.Numerics;
using OpenDelulu.Formats.Imaging;
using OpenDelulu.Formats.Xx;

namespace OpenDelulu.Engine.Assets;

/// <summary>
/// GPU-ready geometry built from an <see cref="XxModel"/>: interleaved vertices in model space
/// (bind pose), 16-bit indices and decoded textures. Renderer agnostic.
/// </summary>
public sealed class RenderModel
{
    /// <summary>Floats per vertex: position (3), normal (3), uv (2).</summary>
    public const int VertexStride = 8;

    public required string Name { get; init; }
    public required IReadOnlyList<RenderMesh> Meshes { get; init; }
    /// <summary>Decoded textures, indexed by <see cref="RenderMesh.TextureIndex"/>. Null = failed to decode.</summary>
    public required IReadOnlyList<RgbaImage?> Textures { get; init; }
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }

    public Vector3 Center => (BoundsMin + BoundsMax) * 0.5f;
    public float Radius => MathF.Max(0.001f, Vector3.Distance(BoundsMin, BoundsMax) * 0.5f);

    public static RenderModel FromXx(XxModel model, string name)
    {
        var frameWorld = new Dictionary<XxFrame, Matrix4x4>();
        var byName = new Dictionary<string, XxFrame>(StringComparer.Ordinal);
        foreach (var frame in model.EnumerateFrames())
        {
            frameWorld[frame] = frame.WorldMatrix;
            byName.TryAdd(frame.Name, frame);
        }

        var textureIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var textures = new List<RgbaImage?>();
        int ResolveTexture(string textureName)
        {
            if (string.IsNullOrEmpty(textureName))
                return -1;
            if (textureIndex.TryGetValue(textureName, out int index))
                return index;
            var texture = model.FindTexture(textureName);
            RgbaImage? image = null;
            if (texture is not null)
            {
                try { image = ImageDecoder.Decode(texture.ImageData, texture.Name); }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentOutOfRangeException) { }
            }
            textures.Add(image);
            textureIndex[textureName] = textures.Count - 1;
            return textures.Count - 1;
        }

        var meshes = new List<RenderMesh>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var frame in model.EnumerateFrames())
        {
            if (frame.Mesh is not { } mesh)
                continue;

            var world = frameWorld[frame];
            // Skinning matrices for the bind pose: offset * bone frame world.
            Matrix4x4[]? skin = null;
            if (mesh.IsSkinned)
            {
                skin = new Matrix4x4[mesh.Bones.Count];
                for (int b = 0; b < skin.Length; b++)
                {
                    var bone = mesh.Bones[b];
                    skin[b] = byName.TryGetValue(bone.Name, out var boneFrame)
                        ? bone.Matrix * frameWorld[boneFrame]
                        : world;
                }
            }

            for (int s = 0; s < mesh.Submeshes.Count; s++)
            {
                var submesh = mesh.Submeshes[s];
                var vertices = new float[submesh.Vertices.Length * VertexStride];
                for (int v = 0; v < submesh.Vertices.Length; v++)
                {
                    ref readonly var src = ref submesh.Vertices[v];
                    var (position, normal) = skin is null
                        ? (Vector3.Transform(src.Position, world), Vector3.TransformNormal(src.Normal, world))
                        : Skin(src, skin, world);
                    normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;

                    int o = v * VertexStride;
                    vertices[o] = position.X; vertices[o + 1] = position.Y; vertices[o + 2] = position.Z;
                    vertices[o + 3] = normal.X; vertices[o + 4] = normal.Y; vertices[o + 5] = normal.Z;
                    vertices[o + 6] = src.UV.X; vertices[o + 7] = src.UV.Y;
                    min = Vector3.Min(min, position);
                    max = Vector3.Max(max, position);
                }

                var material = submesh.MaterialIndex >= 0 && submesh.MaterialIndex < model.Materials.Count
                    ? model.Materials[submesh.MaterialIndex]
                    : null;
                int texture = material is null ? -1 : ResolveTexture(material.Textures.FirstOrDefault()?.Name ?? "");

                meshes.Add(new RenderMesh
                {
                    FrameName = frame.Name,
                    SubmeshIndex = s,
                    Vertices = vertices,
                    Indices = submesh.Indices,
                    TextureIndex = texture,
                    Diffuse = material?.Diffuse ?? Vector4.One,
                    Ambient = material?.Ambient ?? Vector4.Zero,
                    MaterialName = material?.Name ?? "",
                });
            }
        }

        foreach (var mesh in meshes)
            mesh.HasAlpha = mesh.TextureIndex >= 0 && textures[mesh.TextureIndex]?.HasAlpha() == true;

        if (meshes.Count == 0)
            min = max = Vector3.Zero;

        return new RenderModel { Name = name, Meshes = meshes, Textures = textures, BoundsMin = min, BoundsMax = max };
    }

    static (Vector3 Position, Vector3 Normal) Skin(in XxVertex vertex, Matrix4x4[] skin, Matrix4x4 fallback)
    {
        Vector3 position = Vector3.Zero, normal = Vector3.Zero;
        float total = 0;
        for (int slot = 0; slot < 4; slot++)
        {
            int bone = vertex.Bone(slot);
            float weight = vertex.Weight(slot);
            if (bone == 0xFF || bone >= skin.Length || weight <= 0)
                continue;
            position += Vector3.Transform(vertex.Position, skin[bone]) * weight;
            normal += Vector3.TransformNormal(vertex.Normal, skin[bone]) * weight;
            total += weight;
        }
        if (total <= 1e-6f)
            return (Vector3.Transform(vertex.Position, fallback), Vector3.TransformNormal(vertex.Normal, fallback));
        return (position / total, normal);
    }
}

public sealed class RenderMesh
{
    public required string FrameName { get; init; }
    public required int SubmeshIndex { get; init; }
    public required float[] Vertices { get; init; }
    /// <summary>Triangle list, Direct3D winding (clockwise front faces).</summary>
    public required ushort[] Indices { get; init; }
    public required int TextureIndex { get; init; }
    public required Vector4 Diffuse { get; init; }
    public required Vector4 Ambient { get; init; }
    public required string MaterialName { get; init; }
    /// <summary>Texture contains translucent pixels; draw after opaque meshes.</summary>
    public bool HasAlpha { get; internal set; }

    public string DisplayName => $"{FrameName} [{SubmeshIndex}]";
}
