using System.Numerics;
using System.Text.RegularExpressions;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Formats.Xa;
using OpenDelulu.Formats.Xx;

namespace OpenDelulu.Engine.Animation;

/// <summary>
/// Facial expressions from the morph section of a face .xa.
/// <para>
/// Each morph clip is a timeline: its keyframe refs carry a frame number, and every expression owns a
/// block of 10 frames (expression <c>k</c> = frames <c>10k .. 10k+9</c>). Clips flagged 3 are eye-type:
/// frame <c>10k</c> is closed, <c>10k+9</c> open (the modelled face equals frame 9 of expression 0), so
/// blinking runs the block backwards. Clips flagged 5 are mouth-type: <c>10k</c> is the expression's mouth,
/// <c>10k+9</c> the open "a" mouth used while speaking. This holds for both naming schemes found in the data
/// (<c>Defo00_Me</c>/<c>Defo09_Me</c> and <c>def_cl</c>/<c>def_op</c>).
/// </para>
/// </summary>
public sealed partial class MorphPlayer
{
    const int FramesPerExpression = 10;
    const int EyeClipFlag = 3;

    sealed record Channel(RenderMesh Mesh, XaMorphIndexSet Set, (int Frame, XaMorphKeyframe Key)[] Keys, bool IsEyes);

    readonly List<Channel> _channels = [];
    readonly Dictionary<RenderMesh, XxVertex[]> _morphed = new();
    int _block, _mouthBlock;

    public MorphPlayer(RenderModel model, XaMorphSection morphs, string partName)
    {
        foreach (var set in morphs.IndexSets)
        {
            // The clip named like the set says which mesh it deforms; several sets can share one mesh
            // (eyes and mouth both deform o01_P_Kao).
            var clip = morphs.Clips.FirstOrDefault(c => c.Name == set.Name);
            if (clip is null)
                continue;
            var mesh = model.Meshes
                .Where(m => m.PartName == partName && m.FrameName == clip.MeshName)
                .FirstOrDefault(m => set.MeshIndices.Length > 0 && set.MeshIndices.Max() < m.SourceVertices.Length);
            if (mesh is null)
                continue;

            var keys = clip.KeyframeRefs
                .Select(r => (r.Index, Key: morphs.FindKeyframe(r.Name)))
                .Where(k => k.Key is not null)
                .Select(k => (k.Index, k.Key!))
                .OrderBy(k => k.Index)
                .ToArray();
            if (keys.Length == 0)
                continue;
            bool isEyes = clip.Unknown1.Length > 0 && clip.Unknown1[0] == EyeClipFlag;
            _channels.Add(new Channel(mesh, set, keys, isEyes));
            // One private vertex copy per mesh, created up front so Apply can run in parallel per mesh.
            _morphed.TryAdd(mesh, (XxVertex[])mesh.SourceVertices.Clone());
        }

        Expressions = BuildExpressionNames();
        Expression = Expressions.FirstOrDefault() ?? "";
    }

    /// <summary>Display names, index = expression block.</summary>
    public IReadOnlyList<string> Expressions { get; }

    public string Expression
    {
        get => _block < Expressions.Count ? Expressions[_block] : "";
        set => _block = _mouthBlock = Math.Max(0, Expressions.ToList().IndexOf(value));
    }

    /// <summary>Eyes and mouth from different expression blocks (the scene tables set them separately).</summary>
    public void SetExpression(int eyes, int mouth)
    {
        _block = Math.Clamp(eyes, 0, Math.Max(0, Expressions.Count - 1));
        _mouthBlock = Math.Clamp(mouth, 0, Math.Max(0, Expressions.Count - 1));
    }

    /// <summary>0 = eyes open, 1 = closed.</summary>
    public float EyeClosed { get; set; }
    /// <summary>0 = expression mouth, 1 = open "a" mouth (for lip sync).</summary>
    public float MouthOpen { get; set; }
    public bool AutoBlink { get; set; } = true;
    public int TargetCount => _channels.Count;

    double _blinkClock, _nextBlink = 3;

    public void Advance(double seconds)
    {
        if (!AutoBlink)
            return;
        _blinkClock += seconds;
        if (_blinkClock < _nextBlink)
            return;
        double t = _blinkClock - _nextBlink; // close in 0.08 s, reopen in 0.1 s
        EyeClosed = (float)(t < 0.08 ? t / 0.08 : Math.Max(0, 1 - (t - 0.08) / 0.1));
        if (t > 0.2)
        {
            _blinkClock = 0;
            _nextBlink = 2.5 + Random.Shared.NextDouble() * 3;
            EyeClosed = 0;
        }
    }

    /// <summary>
    /// Morphed source vertices for a mesh, or null if no morph applies to it.
    /// Safe to call in parallel for different meshes.
    /// </summary>
    public XxVertex[]? Apply(RenderMesh mesh)
    {
        XxVertex[]? vertices = null;
        foreach (var channel in _channels)
        {
            if (!ReferenceEquals(channel.Mesh, mesh))
                continue;
            float position = channel.IsEyes ? 9f * (1f - EyeClosed) : 9f * MouthOpen;
            if (!TrySample(channel, channel.IsEyes ? _block : _mouthBlock, position, out var a, out var b, out float f)
                && !TrySample(channel, 0, position, out a, out b, out f))
                continue;

            vertices ??= _morphed[mesh];
            var set = channel.Set;
            for (int i = 0; i < set.MeshIndices.Length; i++)
            {
                int dst = set.MeshIndices[i], src = set.MorphIndices[i];
                if (dst >= vertices.Length || src >= a.Positions.Length || src >= b.Positions.Length)
                    continue;
                vertices[dst].Position = Vector3.Lerp(a.Positions[src], b.Positions[src], f);
                vertices[dst].Normal = Vector3.Lerp(a.Normals[src], b.Normals[src], f);
            }
        }
        return vertices;
    }

    /// <summary>Finds the keys around <c>10 * block + position</c> inside the expression's block.</summary>
    static bool TrySample(Channel channel, int block, float position, out XaMorphKeyframe a, out XaMorphKeyframe b, out float f)
    {
        int start = block * FramesPerExpression, end = start + FramesPerExpression - 1;
        float time = start + position;
        (int Frame, XaMorphKeyframe Key)? before = null, after = null;
        foreach (var key in channel.Keys)
        {
            if (key.Frame < start || key.Frame > end)
                continue;
            if (key.Frame <= time)
                before = key;
            if (key.Frame >= time && after is null)
                after = key;
        }
        before ??= after;
        after ??= before;
        if (before is null)
        {
            a = b = null!;
            f = 0;
            return false;
        }
        a = before.Value.Key;
        b = after!.Value.Key;
        int span = after.Value.Frame - before.Value.Frame;
        f = span > 0 ? (time - before.Value.Frame) / span : 0;
        return true;
    }

    List<string> BuildExpressionNames()
    {
        // Name each block after the eye clip's first key in it ("YoroA00_Me" -> "YoroA", "yoro_cl" -> "yoro").
        var source = _channels.FirstOrDefault(c => c.IsEyes) ?? _channels.FirstOrDefault();
        if (source is null)
            return [];
        int blocks = _channels.Max(c => c.Keys[^1].Frame) / FramesPerExpression + 1;
        var names = new List<string>(blocks);
        for (int block = 0; block < blocks; block++)
        {
            var key = source.Keys.FirstOrDefault(k => k.Frame / FramesPerExpression == block).Key;
            string name = key is null ? "" : CleanName(key.Name, source.Set.Name);
            names.Add($"{block}. {(name.Length > 0 ? name : "?")}");
        }
        return names;
    }

    [GeneratedRegex(@"(_?(cl|op)(_m)?|_?\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex VariantSuffix();

    static string CleanName(string keyframe, string setName)
    {
        string name = keyframe.EndsWith("_" + setName, StringComparison.Ordinal) ? keyframe[..^(setName.Length + 1)] : keyframe;
        return VariantSuffix().Replace(name, "");
    }
}
