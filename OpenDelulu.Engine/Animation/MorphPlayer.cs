using System.Text.RegularExpressions;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Formats.Xa;
using OpenDelulu.Formats.Xx;

namespace OpenDelulu.Engine.Animation;

/// <summary>
/// Facial expressions from the morph section of a face .xa. Keyframes are named
/// <c>{Expression}{Variant}_{Target}</c> (e.g. <c>YoroA00_Me</c>, <c>YoroA09_Me</c>, <c>YoroA_Kuti</c>):
/// for the eyes, variant 00 is open and 09 closed, which also gives blinking.
/// </summary>
public sealed partial class MorphPlayer
{
    /// <summary>An index set (named like its clip and keyframe suffix: "Me", "Kuti"...) on one mesh.</summary>
    sealed record Target(RenderMesh Mesh, XaMorphIndexSet Set);

    readonly XaMorphSection _morphs;
    readonly List<Target> _targets = [];
    readonly Dictionary<RenderMesh, XxVertex[]> _morphed = new();

    public MorphPlayer(RenderModel model, XaMorphSection morphs, string partName)
    {
        _morphs = morphs;
        foreach (var set in morphs.IndexSets)
        {
            // The clip of the same name says which mesh frame the set deforms; several sets can
            // share a mesh (eyes "Me" and mouth "Kuti" both deform o01_P_Kao).
            string? meshFrame = morphs.Clips.FirstOrDefault(c => c.Name == set.Name)?.MeshName;
            var mesh = model.Meshes
                .Where(m => m.PartName == partName && m.FrameName == meshFrame)
                .FirstOrDefault(m => set.MeshIndices.Length > 0 && set.MeshIndices.Max() < m.SourceVertices.Length);
            if (mesh is null)
                continue;
            _targets.Add(new Target(mesh, set));
            // One private vertex copy per mesh, created up front so Apply can run in parallel per mesh.
            _morphed.TryAdd(mesh, (XxVertex[])mesh.SourceVertices.Clone());
        }

        Expressions = morphs.Keyframes
            .Select(k => ExpressionName(k.Name))
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();
        Expression = Expressions.Contains("Defo") ? "Defo" : Expressions.FirstOrDefault() ?? "";
    }

    public IReadOnlyList<string> Expressions { get; }
    public string Expression { get; set; }
    /// <summary>0 = eyes open, 1 = closed (blends variant 00 towards 09).</summary>
    public float EyeClosed { get; set; }
    public bool AutoBlink { get; set; } = true;
    public int TargetCount => _targets.Count;

    double _blinkClock, _nextBlink = 3;

    public void Advance(double seconds)
    {
        if (!AutoBlink)
            return;
        _blinkClock += seconds;
        if (_blinkClock >= _nextBlink)
        {
            double t = _blinkClock - _nextBlink; // 0..0.15 s close, then open
            EyeClosed = (float)(t < 0.08 ? t / 0.08 : Math.Max(0, 1 - (t - 0.08) / 0.1));
            if (t > 0.2)
            {
                _blinkClock = 0;
                _nextBlink = 2.5 + Random.Shared.NextDouble() * 3;
                EyeClosed = 0;
            }
        }
    }

    /// <summary>
    /// Morphed source vertices for a mesh, or null if no morph applies to it.
    /// Safe to call in parallel for different meshes.
    /// </summary>
    public XxVertex[]? Apply(RenderMesh mesh)
    {
        XxVertex[]? vertices = null;
        foreach (var target in _targets)
        {
            if (!ReferenceEquals(target.Mesh, mesh))
                continue;
            string suffix = "_" + target.Set.Name;
            var open = FindKeyframe(Expression, suffix, closed: false) ?? FindKeyframe("Defo", suffix, closed: false);
            if (open is null)
                continue;
            var closed = EyeClosed > 0 ? FindKeyframe(Expression, suffix, closed: true) ?? FindKeyframe("Defo", suffix, closed: true) : null;

            vertices ??= _morphed[mesh];

            var set = target.Set;
            for (int i = 0; i < set.MeshIndices.Length; i++)
            {
                int dst = set.MeshIndices[i], src = set.MorphIndices[i];
                if (dst >= vertices.Length || src >= open.Positions.Length)
                    continue;
                var position = open.Positions[src];
                var normal = open.Normals[src];
                if (closed is not null && src < closed.Positions.Length)
                {
                    position = System.Numerics.Vector3.Lerp(position, closed.Positions[src], EyeClosed);
                    normal = System.Numerics.Vector3.Lerp(normal, closed.Normals[src], EyeClosed);
                }
                vertices[dst].Position = position;
                vertices[dst].Normal = normal;
            }
        }
        return vertices;
    }

    XaMorphKeyframe? FindKeyframe(string expression, string suffix, bool closed)
    {
        XaMorphKeyframe? any = null;
        foreach (var keyframe in _morphs.Keyframes)
        {
            if (!keyframe.Name.EndsWith(suffix, StringComparison.Ordinal) || ExpressionName(keyframe.Name) != expression)
                continue;
            string variant = Variant(keyframe.Name);
            if (closed ? variant == "09" : variant is "00" or "")
                return keyframe;
            any ??= keyframe;
        }
        return closed ? null : any;
    }

    // "YoroA00_Me" -> expression "YoroA", variant "00", target "Me". "Sp_Kuti_00" -> "Sp".
    [GeneratedRegex(@"^(?<expr>[A-Za-z]+?)(?<variant>\d*)_")]
    private static partial Regex NamePattern();

    static string ExpressionName(string keyframe) => NamePattern().Match(keyframe) is { Success: true } m ? m.Groups["expr"].Value : "";

    static string Variant(string keyframe) => NamePattern().Match(keyframe) is { Success: true } m ? m.Groups["variant"].Value : "";
}
