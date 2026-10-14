using System.Numerics;
using OpenDelulu.Formats.Imaging;
using OpenDelulu.Formats.Xx;

namespace OpenDelulu.Engine.Assets;

/// <summary>
/// One source model contributing to a <see cref="RenderModel"/>: the frames under <see cref="Root"/>
/// are drawn with <see cref="Model"/>'s materials. <see cref="Root"/> may have been attached to a
/// frame of another part (see <see cref="XxFrame.AttachChild"/>), which places it in that part's space.
/// </summary>
/// <param name="TextureOverrides">Replaces textures by name, e.g. the 16x16 placeholders the game swaps at runtime.</param>
public sealed record RenderPart(
    string Name,
    XxModel Model,
    XxFrame Root,
    IReadOnlyDictionary<string, RgbaImage>? TextureOverrides = null);

/// <summary>
/// GPU-ready geometry built from one or more .xx models: interleaved vertices in model space
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

    public static RenderModel FromXx(XxModel model, string name) => Build(name, [new RenderPart(name, model, model.Root)]);

    /// <summary>Collision ("Atari") and glare helper meshes are never drawn as-is by the game.</summary>
    public static bool IsHelperMaterial(string material) =>
        material.Contains("Atari", StringComparison.OrdinalIgnoreCase) ||
        material.Contains("Glare", StringComparison.OrdinalIgnoreCase);

    /// <param name="visibility">Initial visibility per mesh; default hides helper meshes only.</param>
    public static RenderModel Build(string name, IReadOnlyList<RenderPart> parts, Func<RenderMesh, bool>? visibility = null)
    {
        // World matrices across the whole (possibly cross-model) hierarchy.
        var worldCache = new Dictionary<XxFrame, Matrix4x4>();
        Matrix4x4 World(XxFrame frame)
        {
            if (!worldCache.TryGetValue(frame, out var m))
                worldCache[frame] = m = frame.Parent is null ? frame.Matrix : frame.Matrix * World(frame.Parent);
            return m;
        }

        // A part attached inside another part's hierarchy belongs to itself only.
        var partRoots = parts.Select(p => p.Root).ToHashSet();
        IEnumerable<XxFrame> FramesOf(RenderPart part)
        {
            var stack = new Stack<XxFrame>();
            stack.Push(part.Root);
            while (stack.Count > 0)
            {
                var frame = stack.Pop();
                yield return frame;
                for (int i = frame.Children.Count - 1; i >= 0; i--)
                    if (!partRoots.Contains(frame.Children[i]))
                        stack.Push(frame.Children[i]);
            }
        }

        // Bone lookup: a part's own frames win, then any frame of any part (e.g. body joints).
        var globalFrames = new Dictionary<string, XxFrame>(StringComparer.Ordinal);
        var partFrames = new List<Dictionary<string, XxFrame>>();
        foreach (var part in parts)
        {
            var local = new Dictionary<string, XxFrame>(StringComparer.Ordinal);
            foreach (var frame in FramesOf(part))
            {
                local.TryAdd(frame.Name, frame);
                globalFrames.TryAdd(frame.Name, frame);
            }
            partFrames.Add(local);
        }

        var textures = new List<RgbaImage?>();
        var textureIndex = new Dictionary<(int Part, string Name), int>();
        int ResolveTexture(int partIndex, string textureName)
        {
            if (string.IsNullOrEmpty(textureName))
                return -1;
            var key = (partIndex, textureName.ToLowerInvariant());
            if (textureIndex.TryGetValue(key, out int index))
                return index;

            var part = parts[partIndex];
            RgbaImage? image = null;
            if (part.TextureOverrides?.TryGetValue(textureName, out var replacement) == true)
            {
                image = replacement;
            }
            else if (part.Model.FindTexture(textureName) is { } texture)
            {
                try { image = ImageDecoder.Decode(texture.ImageData, texture.Name); }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentOutOfRangeException) { }
            }
            textures.Add(image);
            textureIndex[key] = textures.Count - 1;
            return textures.Count - 1;
        }

        var meshes = new List<RenderMesh>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            foreach (var frame in FramesOf(part))
            {
                if (frame.Mesh is not { } mesh)
                    continue;

                var world = World(frame);
                Matrix4x4[]? skin = null;
                if (mesh.IsSkinned)
                {
                    skin = new Matrix4x4[mesh.Bones.Count];
                    for (int b = 0; b < skin.Length; b++)
                    {
                        var bone = mesh.Bones[b];
                        var boneFrame = partFrames[p].GetValueOrDefault(bone.Name) ?? globalFrames.GetValueOrDefault(bone.Name);
                        skin[b] = boneFrame is not null ? bone.Matrix * World(boneFrame) : world;
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
                    }

                    var material = submesh.MaterialIndex >= 0 && submesh.MaterialIndex < part.Model.Materials.Count
                        ? part.Model.Materials[submesh.MaterialIndex]
                        : null;
                    int texture = material is null ? -1 : ResolveTexture(p, material.Textures.FirstOrDefault()?.Name ?? "");

                    var renderMesh = new RenderMesh
                    {
                        PartName = part.Name,
                        FrameName = frame.Name,
                        SubmeshIndex = s,
                        Vertices = vertices,
                        Indices = submesh.Indices,
                        TextureIndex = texture,
                        Diffuse = material?.Diffuse ?? Vector4.One,
                        Ambient = material?.Ambient ?? Vector4.Zero,
                        MaterialName = material?.Name ?? "",
                    };
                    renderMesh.Visible = visibility?.Invoke(renderMesh) ?? !IsHelperMaterial(renderMesh.MaterialName);
                    meshes.Add(renderMesh);
                }
            }
        }

        foreach (var mesh in meshes)
        {
            mesh.HasAlpha = mesh.TextureIndex >= 0 && textures[mesh.TextureIndex]?.HasAlpha() == true;
            // Frame the camera on what is actually shown (helper meshes can be huge).
            if (!mesh.Visible)
                continue;
            for (int o = 0; o < mesh.Vertices.Length; o += VertexStride)
            {
                var position = new Vector3(mesh.Vertices[o], mesh.Vertices[o + 1], mesh.Vertices[o + 2]);
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }
        }

        if (min.X > max.X)
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
    public required string PartName { get; init; }
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
    /// <summary>Initial visibility chosen by whoever built the model.</summary>
    public bool Visible { get; set; } = true;

    public string DisplayName => $"{FrameName} [{SubmeshIndex}]";
}
