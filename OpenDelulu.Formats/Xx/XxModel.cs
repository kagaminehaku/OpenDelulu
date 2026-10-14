using System.Numerics;

namespace OpenDelulu.Formats.Xx;

/// <summary>
/// An Illusion .xx model: a frame (node) hierarchy with optional meshes, plus materials and
/// embedded textures. Coordinates follow Direct3D 9 (left-handed, row vectors).
/// Unknown byte blocks are kept verbatim so a writer can round-trip files later.
/// </summary>
public sealed class XxModel
{
    /// <summary>File format version. Every model shipped with SB3 is version 1.</summary>
    public int Format { get; init; }
    public byte[] Header { get; init; } = [];
    public XxFrame Root { get; init; } = null!;
    public byte[] MaterialSectionUnknown { get; init; } = [];
    public List<XxMaterial> Materials { get; init; } = [];
    public List<XxTexture> Textures { get; init; } = [];
    public byte[]? Footer { get; init; }

    public IEnumerable<XxFrame> EnumerateFrames() => Root.EnumerateSelfAndDescendants();

    public XxFrame? FindFrame(string name) =>
        EnumerateFrames().FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));

    public XxTexture? FindTexture(string name) =>
        Textures.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class XxFrame
{
    public string Name { get; set; } = "";
    /// <summary>Local transform relative to <see cref="Parent"/>.</summary>
    public Matrix4x4 Matrix { get; set; } = Matrix4x4.Identity;
    public byte[] Unknown1 { get; set; } = [];
    public Vector3 BoundsMin { get; set; }
    public Vector3 BoundsMax { get; set; }
    public byte[] Unknown2 { get; set; } = [];
    public string? Name2 { get; set; }
    public XxMesh? Mesh { get; set; }
    public XxFrame? Parent { get; internal set; }
    public List<XxFrame> Children { get; } = [];

    /// <summary>Transform from this frame's space to model space (<c>local * parent * ... * root</c>).</summary>
    public Matrix4x4 WorldMatrix => Parent is null ? Matrix : Matrix * Parent.WorldMatrix;

    public IEnumerable<XxFrame> EnumerateSelfAndDescendants()
    {
        var stack = new Stack<XxFrame>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var frame = stack.Pop();
            yield return frame;
            for (int i = frame.Children.Count - 1; i >= 0; i--)
                stack.Push(frame.Children[i]);
        }
    }

    public override string ToString() => Name;
}

public sealed class XxMesh
{
    /// <summary>Number of extra UV sets stored after each submesh's vertices.</summary>
    public byte NumVector2PerVertex { get; set; }
    public List<XxSubmesh> Submeshes { get; } = [];
    /// <summary>Vertices shared between submeshes (used by the game for seam normals).</summary>
    public List<XxVertex> DuplicateVertices { get; } = [];
    public byte[] DuplicateVerticesUnknown { get; set; } = [];
    /// <summary>Skinning bones. Empty for rigid meshes.</summary>
    public List<XxBone> Bones { get; } = [];

    public bool IsSkinned => Bones.Count > 0;
}

public sealed class XxSubmesh
{
    public byte[] Unknown1 { get; set; } = [];
    public int MaterialIndex { get; set; }
    /// <summary>Triangle list, 3 indices per face, Direct3D (clockwise) winding.</summary>
    public ushort[] Indices { get; set; } = [];
    public XxVertex[] Vertices { get; set; } = [];
    /// <summary>Per-vertex trailing bytes (20 each) present from format 4 onwards; null for SB3.</summary>
    public byte[]? VertexExtra { get; set; }
    public byte[]? Unknown2 { get; set; }
    /// <summary>Extra UV sets: <c>ExtraUVs[vertex][set]</c>.</summary>
    public Vector2[][]? ExtraUVs { get; set; }
    public byte[]? Unknown3 { get; set; }
    public byte[]? Unknown4 { get; set; }
    public byte[]? Unknown5 { get; set; }
    public byte[]? Unknown6 { get; set; }

    public int FaceCount => Indices.Length / 3;
}

public struct XxVertex
{
    public int Index;
    public Vector3 Position;
    /// <summary>First three blend weights; the fourth is <c>1 - sum</c>.</summary>
    public Vector3 Weights3;
    /// <summary>Four bone indices into <see cref="XxMesh.Bones"/>; 0xFF = unused.</summary>
    public uint BoneIndices;
    public Vector3 Normal;
    public Vector2 UV;

    public readonly byte Bone(int slot) => (byte)(BoneIndices >> (slot * 8));

    public readonly float Weight(int slot) => slot switch
    {
        0 => Weights3.X,
        1 => Weights3.Y,
        2 => Weights3.Z,
        _ => MathF.Max(0f, 1f - (Weights3.X + Weights3.Y + Weights3.Z)),
    };
}

public sealed class XxBone
{
    public string Name { get; set; } = "";
    public int Index { get; set; }
    /// <summary>Offset (inverse bind) matrix: mesh space to bone space.</summary>
    public Matrix4x4 Matrix { get; set; }

    public override string ToString() => Name;
}

public sealed class XxMaterial
{
    public string Name { get; set; } = "";
    public Vector4 Diffuse { get; set; }
    public Vector4 Ambient { get; set; }
    public Vector4 Specular { get; set; }
    public Vector4 Emissive { get; set; }
    public float Power { get; set; }
    /// <summary>Four texture slots; slot 0 is the diffuse map. Empty name = unused.</summary>
    public XxMaterialTexture[] Textures { get; set; } = [];
    public byte[] Unknown1 { get; set; } = [];

    public override string ToString() => Name;
}

public sealed class XxMaterialTexture
{
    public string Name { get; set; } = "";
    public byte[] Unknown1 { get; set; } = [];
}

public sealed class XxTexture
{
    public string Name { get; set; } = "";
    public byte[] Unknown1 { get; set; } = [];
    public int Width { get; set; }
    public int Height { get; set; }
    public int Depth { get; set; }
    public int MipLevels { get; set; }
    /// <summary>D3DFORMAT of the GPU texture.</summary>
    public int D3DFormat { get; set; }
    public int ResourceType { get; set; }
    /// <summary>D3DXIMAGE_FILEFORMAT of <see cref="ImageData"/> (0 = BMP, 2 = TGA...).</summary>
    public int ImageFileFormat { get; set; }
    public byte Checksum { get; set; }
    /// <summary>Image file bytes. BMPs have their "BM" signature zeroed; use <see cref="GetImageFile"/>.</summary>
    public byte[] ImageData { get; set; } = [];

    /// <summary>A standalone image file (signature restored).</summary>
    public byte[] GetImageFile()
    {
        var data = (byte[])ImageData.Clone();
        if (data.Length >= 2 && Path.GetExtension(Name).Equals(".bmp", StringComparison.OrdinalIgnoreCase))
        {
            data[0] = (byte)'B';
            data[1] = (byte)'M';
        }
        return data;
    }

    public override string ToString() => $"{Name} {Width}x{Height}";
}
