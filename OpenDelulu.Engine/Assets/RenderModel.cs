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
/// The frame hierarchy of a <see cref="RenderModel"/> flattened into arrays (parents before children),
/// so a pose is just an array of local matrices.
/// </summary>
public sealed class Rig
{
    readonly Dictionary<string, int> _byName;

    internal Rig(XxFrame[] frames)
    {
        Frames = frames;
        var index = new Dictionary<XxFrame, int>(frames.Length);
        for (int i = 0; i < frames.Length; i++)
            index[frames[i]] = i;
        Parents = frames.Select(f => f.Parent is { } p && index.TryGetValue(p, out int pi) ? pi : -1).ToArray();
        BindLocal = frames.Select(f => f.Matrix).ToArray();
        _byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < frames.Length; i++)
            _byName.TryAdd(frames[i].Name, i);
        FrameIndex = index;
    }

    public IReadOnlyList<XxFrame> Frames { get; }
    public int[] Parents { get; }
    public Matrix4x4[] BindLocal { get; }
    internal Dictionary<XxFrame, int> FrameIndex { get; }

    public int Count => Parents.Length;

    /// <summary>First frame with that name (the body's, when parts share names).</summary>
    public int Find(string name) => _byName.GetValueOrDefault(name, -1);

    public void ComputeWorld(ReadOnlySpan<Matrix4x4> local, Span<Matrix4x4> world)
    {
        for (int i = 0; i < Parents.Length; i++)
            world[i] = Parents[i] < 0 ? local[i] : local[i] * world[Parents[i]];
    }
}

/// <summary>
/// GPU-ready geometry built from one or more .xx models: interleaved vertices in model space,
/// 16-bit indices and decoded textures. Renderer agnostic. Vertices start in the bind pose and can be
/// re-skinned for any pose with <see cref="Skin"/> (CPU skinning: works on every GPU API, GLES2 included).
/// </summary>
public sealed class RenderModel
{
    /// <summary>Floats per vertex: position (3), normal (3), uv (2).</summary>
    public const int VertexStride = 8;

    public required string Name { get; init; }
    public required IReadOnlyList<RenderMesh> Meshes { get; init; }
    /// <summary>Decoded textures, indexed by <see cref="RenderMesh.TextureIndex"/>. Null = failed to decode.</summary>
    public required IReadOnlyList<RgbaImage?> Textures { get; init; }
    public required Rig Rig { get; init; }
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }

    public Vector3 Center => (BoundsMin + BoundsMax) * 0.5f;

    /// <summary>Point close-ups should aim at: between the eyes for characters, null otherwise.</summary>
    public Vector3? FocusPoint { get; init; }
    public float Radius => MathF.Max(0.001f, Vector3.Distance(BoundsMin, BoundsMax) * 0.5f);

    public static RenderModel FromXx(XxModel model, string name) => Build(name, [new RenderPart(name, model, model.Root)]);

    /// <summary>Collision ("Atari") and glare helper meshes are never drawn as-is by the game.</summary>
    public static bool IsHelperMaterial(string material) =>
        material.Contains("Atari", StringComparison.OrdinalIgnoreCase) ||
        material.Contains("Glare", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Re-computes <see cref="RenderMesh.Vertices"/> of one mesh for a pose.
    /// </summary>
    /// <param name="world">World matrices for every frame of <see cref="Rig"/>.</param>
    /// <param name="source">Vertices to skin; defaults to the mesh's own (pass morphed ones for expressions).</param>
    public static void Skin(RenderMesh mesh, ReadOnlySpan<Matrix4x4> world, ReadOnlySpan<XxVertex> source = default)
    {
        if (source.IsEmpty)
            source = mesh.SourceVertices;
        var output = mesh.Vertices;
        var frameWorld = world[mesh.FrameIndex];

        Span<Matrix4x4> skin = mesh.BoneFrames is { } bones ? new Matrix4x4[bones.Length] : default;
        if (mesh.BoneFrames is { } boneFrames)
            for (int b = 0; b < boneFrames.Length; b++)
                skin[b] = boneFrames[b] >= 0 ? mesh.BoneOffsets![b] * world[boneFrames[b]] : frameWorld;

        for (int v = 0; v < source.Length; v++)
        {
            ref readonly var src = ref source[v];
            Vector3 position, normal;
            if (skin.IsEmpty)
            {
                position = Vector3.Transform(src.Position, frameWorld);
                normal = Vector3.TransformNormal(src.Normal, frameWorld);
            }
            else
            {
                position = Vector3.Zero;
                normal = Vector3.Zero;
                float total = 0;
                for (int slot = 0; slot < 4; slot++)
                {
                    int bone = src.Bone(slot);
                    float weight = src.Weight(slot);
                    if (bone == 0xFF || bone >= skin.Length || weight <= 0)
                        continue;
                    position += Vector3.Transform(src.Position, skin[bone]) * weight;
                    normal += Vector3.TransformNormal(src.Normal, skin[bone]) * weight;
                    total += weight;
                }
                if (total > 1e-6f)
                {
                    position /= total;
                }
                else
                {
                    position = Vector3.Transform(src.Position, frameWorld);
                    normal = Vector3.TransformNormal(src.Normal, frameWorld);
                }
            }
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;

            int o = v * VertexStride;
            output[o] = position.X; output[o + 1] = position.Y; output[o + 2] = position.Z;
            output[o + 3] = normal.X; output[o + 4] = normal.Y; output[o + 5] = normal.Z;
            output[o + 6] = src.UV.X; output[o + 7] = src.UV.Y;
        }
    }

    /// <param name="visibility">Initial visibility per mesh; default hides helper meshes only.</param>
    public static RenderModel Build(string name, IReadOnlyList<RenderPart> parts, Func<RenderMesh, bool>? visibility = null)
    {
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

        // Rig: every frame reachable from the top-most roots, parents first.
        var topRoots = parts.Select(p => p.Root).Select(r => { while (r.Parent is not null) r = r.Parent; return r; }).Distinct();
        var rig = new Rig(topRoots.SelectMany(r => r.EnumerateSelfAndDescendants()).ToArray());

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
        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            foreach (var frame in FramesOf(part))
            {
                if (frame.Mesh is not { } mesh)
                    continue;

                int[]? boneFrames = null;
                Matrix4x4[]? boneOffsets = null;
                if (mesh.IsSkinned)
                {
                    boneFrames = mesh.Bones
                        .Select(b => partFrames[p].GetValueOrDefault(b.Name) ?? globalFrames.GetValueOrDefault(b.Name))
                        .Select(f => f is not null && rig.FrameIndex.TryGetValue(f, out int i) ? i : -1)
                        .ToArray();
                    boneOffsets = mesh.Bones.Select(b => b.Matrix).ToArray();
                }

                for (int s = 0; s < mesh.Submeshes.Count; s++)
                {
                    var submesh = mesh.Submeshes[s];
                    var material = submesh.MaterialIndex >= 0 && submesh.MaterialIndex < part.Model.Materials.Count
                        ? part.Model.Materials[submesh.MaterialIndex]
                        : null;

                    var renderMesh = new RenderMesh
                    {
                        PartName = part.Name,
                        FrameName = frame.Name,
                        SubmeshIndex = s,
                        Vertices = new float[submesh.Vertices.Length * VertexStride],
                        Indices = submesh.Indices,
                        TextureIndex = material is null ? -1 : ResolveTexture(p, material.Textures.FirstOrDefault()?.Name ?? ""),
                        // Material colors are stored negated in SB3 data (4778 of 4796 materials; the rest are 0).
                        Diffuse = material is null ? Vector4.One : Vector4.Abs(material.Diffuse),
                        Ambient = material is null ? Vector4.Zero : Vector4.Abs(material.Ambient),
                        Emissive = material is null ? Vector4.Zero : Vector4.Abs(material.Emissive),
                        MaterialName = material?.Name ?? "",
                        SourceVertices = submesh.Vertices,
                        FrameIndex = rig.FrameIndex[frame],
                        BoneFrames = boneFrames,
                        BoneOffsets = boneOffsets,
                    };
                    renderMesh.Visible = visibility?.Invoke(renderMesh) ?? !IsHelperMaterial(renderMesh.MaterialName);
                    meshes.Add(renderMesh);
                }
            }
        }

        // Bind pose.
        var world = new Matrix4x4[rig.Count];
        rig.ComputeWorld(rig.BindLocal, world);
        foreach (var mesh in meshes)
            Skin(mesh, world);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var mesh in meshes)
        {
            // Material alpha below 1 marks overlays/effects: water, glints, blush, the cornea layer (alpha 0 = hidden
            // until an expression or effect raises it).
            mesh.HasAlpha = mesh.Diffuse.W < 0.999f
                || (mesh.TextureIndex >= 0 && textures[mesh.TextureIndex]?.HasAlpha() == true);
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

        // Characters: between the eye frames (bind pose), falling back to the head joint.
        Vector3? focus = null;
        int left = rig.Find("o01_N_Me_L"), right = rig.Find("o01_N_Me_R"), head = rig.Find("o01_J_Head");
        if (left >= 0 && right >= 0)
            focus = (world[left].Translation + world[right].Translation) * 0.5f;
        else if (head >= 0)
            focus = world[head].Translation;

        return new RenderModel
        {
            Name = name, Meshes = meshes, Textures = textures, Rig = rig, BoundsMin = min, BoundsMax = max, FocusPoint = focus,
        };
    }
}

public sealed class RenderMesh
{
    public required string PartName { get; init; }
    public required string FrameName { get; init; }
    public required int SubmeshIndex { get; init; }
    /// <summary>Current interleaved vertices (rewritten by <see cref="RenderModel.Skin"/>).</summary>
    public required float[] Vertices { get; init; }
    /// <summary>Triangle list, Direct3D winding (clockwise front faces).</summary>
    public required ushort[] Indices { get; init; }
    public required int TextureIndex { get; init; }
    /// <summary>Material diffuse color (already un-negated); W is the material opacity.</summary>
    public required Vector4 Diffuse { get; init; }
    public required Vector4 Ambient { get; init; }
    public Vector4 Emissive { get; init; }
    public required string MaterialName { get; init; }
    /// <summary>Texture contains translucent pixels; draw after opaque meshes.</summary>
    public bool HasAlpha { get; internal set; }
    /// <summary>Initial visibility chosen by whoever built the model.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>Mesh-space vertices from the .xx (bind pose input to skinning and morphs).</summary>
    public required Formats.Xx.XxVertex[] SourceVertices { get; init; }
    /// <summary>Rig index of the frame owning the mesh (rigid transform).</summary>
    public required int FrameIndex { get; init; }
    /// <summary>Rig index per bone (-1 = bone frame not found); null for rigid meshes.</summary>
    public int[]? BoneFrames { get; init; }
    public Matrix4x4[]? BoneOffsets { get; init; }

    public string DisplayName => $"{FrameName} [{SubmeshIndex}]";
}
