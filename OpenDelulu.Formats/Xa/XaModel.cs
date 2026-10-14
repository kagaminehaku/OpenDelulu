using System.Numerics;

namespace OpenDelulu.Formats.Xa;

/// <summary>
/// An Illusion .xa file: skeletal animation (per-frame tracks + named clips), vertex morphs
/// (facial expressions) and material color animation. Pairs with the .xx of the same name.
/// </summary>
public sealed class XaModel
{
    /// <summary>-1 = pre-SB3 layout; SB3 files are 1 (no section 4) or 2.</summary>
    public int Format { get; init; }
    public byte[]? Header { get; init; }
    public XaMaterialSection? Materials { get; init; }
    public XaSection2? Section2 { get; init; }
    public XaMorphSection? Morphs { get; init; }
    public XaSection4? Section4 { get; init; }
    public XaAnimationSection? Animation { get; init; }
    public byte[] Footer { get; init; } = [];
}

// ------------------------------------------------------------------ materials

public sealed class XaMaterialSection
{
    public List<XaMaterial> Materials { get; } = [];
}

public sealed class XaMaterial
{
    public string Name { get; set; } = "";
    public List<XaMaterialColor> Colors { get; } = [];
}

public sealed class XaMaterialColor
{
    public Vector4 Diffuse, Ambient, Specular, Emissive;
    public float Power;
    public byte[] Unknown1 = [];
}

// ------------------------------------------------------------------ section 2 (unknown, kept raw)

public sealed class XaSection2
{
    public List<XaSection2Item> Items { get; } = [];
}

public sealed class XaSection2Item
{
    public byte[] Unknown1 = [];
    public string Name = "";
    public byte[] Unknown2 = [], Unknown3 = [], Unknown4 = [];
    public List<(byte[] Unknown1, byte[] Unknown2)> Blocks { get; } = [];
    public byte[] Unknown5 = [];
}

// ------------------------------------------------------------------ morphs

/// <summary>
/// Vertex morphs. An index set maps vertices of a mesh (<see cref="XaMorphIndexSet.MeshIndices"/>)
/// to entries of every keyframe (<see cref="XaMorphIndexSet.MorphIndices"/>); a keyframe holds absolute
/// positions/normals for those vertices; a clip lists the keyframes of one expression.
/// </summary>
public sealed class XaMorphSection
{
    public List<XaMorphIndexSet> IndexSets { get; } = [];
    public List<XaMorphKeyframe> Keyframes { get; } = [];
    public List<XaMorphClip> Clips { get; } = [];

    public XaMorphKeyframe? FindKeyframe(string name) => Keyframes.FirstOrDefault(k => k.Name == name);
}

public sealed class XaMorphIndexSet
{
    public byte[] Unknown1 = [];
    public ushort[] MeshIndices = [];
    public ushort[] MorphIndices = [];
    /// <summary>Name of the mesh frame this set applies to.</summary>
    public string Name = "";
}

public sealed class XaMorphKeyframe
{
    public Vector3[] Positions = [];
    public Vector3[] Normals = [];
    public string Name = "";

    public override string ToString() => Name;
}

public sealed class XaMorphClip
{
    public string MeshName = "";
    public string Name = "";
    public List<XaMorphKeyframeRef> KeyframeRefs { get; } = [];
    public byte[] Unknown1 = [];

    public override string ToString() => Name;
}

public sealed class XaMorphKeyframeRef
{
    public byte[] Unknown1 = [];
    public int Index;
    public byte[] Unknown2 = [];
    public string Name = "";
}

// ------------------------------------------------------------------ section 4 (unknown, kept raw)

public sealed class XaSection4
{
    /// <summary>Lists of 172-byte records.</summary>
    public List<List<byte[]>> ItemLists { get; } = [];
}

// ------------------------------------------------------------------ skeletal animation

/// <summary>
/// One long timeline per bone (<see cref="Tracks"/>), cut into named <see cref="Clips"/> by frame range.
/// </summary>
public sealed class XaAnimationSection
{
    /// <summary>Fixed-size table (512 or 1024 slots); unused slots have an empty name and Start == End.</summary>
    public List<XaAnimationClip> Clips { get; } = [];
    public List<XaAnimationTrack> Tracks { get; } = [];

    public IEnumerable<(int Index, XaAnimationClip Clip)> UsedClips() =>
        Clips.Select((clip, index) => (index, clip)).Where(c => c.clip.End > c.clip.Start || c.clip.Name.Length > 0);
}

public sealed class XaAnimationClip
{
    public string Name = "";
    public float Speed;
    public byte[] Unknown1 = [];
    public float Start;
    public float End;
    public byte[] Unknown2 = [], Unknown3 = [], Unknown4 = [];
    /// <summary>Clip to continue with when this one ends (its own index = loop).</summary>
    public int Next;
    public byte[] Unknown5 = [], Unknown6 = [], Unknown7 = [];

    public override string ToString() => $"{Name} [{Start}-{End}] x{Speed}";
}

public sealed class XaAnimationTrack
{
    /// <summary>Name of the frame (bone) this track drives.</summary>
    public string Name = "";
    public byte[] Unknown1 = [];
    public XaKeyframe[] Keyframes = [];

    public override string ToString() => $"{Name} ({Keyframes.Length} keys)";
}

public struct XaKeyframe
{
    /// <summary>Frame number on the shared timeline.</summary>
    public int Index;
    public Quaternion Rotation;
    public ulong Unknown1;
    public Vector3 Translation;
    public Vector3 Scaling;

    /// <summary>Local transform: scale, then rotate, then translate (Direct3D row-vector order).</summary>
    public readonly Matrix4x4 ToMatrix() =>
        Matrix4x4.CreateScale(Scaling) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation);
}
