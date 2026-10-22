using System.Numerics;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Formats.Xa;
using OpenDelulu.Formats.Xx;

namespace OpenDelulu.Engine.Animation;

/// <summary>
/// Vertex morphs that follow the .xa timeline (the player's hand te01: "体もみループ" = frames 50..60 is a rubbing
/// motion made only of morph keys). Each morph clip's keyframe refs carry the timeline frame they apply at; the
/// mesh is blended between the two keys around the current frame of the <see cref="AnimationPlayer"/>.
/// </summary>
public sealed class TimelineMorph
{
    sealed record Channel(RenderMesh Mesh, XaMorphIndexSet Set, (int Frame, XaMorphKeyframe Key)[] Keys);

    readonly List<Channel> _channels = [];
    readonly Dictionary<RenderMesh, XxVertex[]> _morphed = new();

    /// <param name="checkRest">
    /// Keep a set only when its rest key (the keyframe named like the set) matches the mesh: the .xm data belongs to the
    /// game's own bodies, and modded bodies with other vertex orders would be torn apart.
    /// </param>
    public TimelineMorph(RenderModel model, XaMorphSection morphs, bool checkRest = false)
    {
        foreach (var set in morphs.IndexSets)
        {
            var clip = morphs.Clips.FirstOrDefault(c => c.Name == set.Name);
            if (clip is null)
                continue;
            var mesh = model.Meshes
                .Where(m => m.FrameName == clip.MeshName)
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
            if (checkRest && !MatchesRest(mesh, set, morphs.FindKeyframe(set.Name)))
            {
                Skipped.Add(set.Name);
                continue;
            }
            _channels.Add(new Channel(mesh, set, keys));
            _morphed.TryAdd(mesh, (XxVertex[])mesh.SourceVertices.Clone());
        }
    }

    public int ChannelCount => _channels.Count;

    /// <summary>Sets left out because their rest shape does not match the mesh (see the constructor).</summary>
    public List<string> Skipped { get; } = [];

    static bool MatchesRest(RenderMesh mesh, XaMorphIndexSet set, XaMorphKeyframe? rest)
    {
        if (rest is null)
            return false;
        var source = mesh.SourceVertices;
        for (int i = 0; i < set.MeshIndices.Length; i++)
        {
            int dst = set.MeshIndices[i], src = set.MorphIndices[i];
            if (dst >= source.Length || src >= rest.Positions.Length
                || Vector3.DistanceSquared(source[dst].Position, rest.Positions[src]) > 0.01f * 0.01f)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Which index sets apply now (by set name, e.g. defo_mune_L); the others go back to the mesh's own shape. Null =
    /// all of them (the hand's own morphs). The .xm deformations of the oil scene pick the side the action works on.
    /// </summary>
    public Func<string, bool>? ActiveSet { get; set; }

    /// <summary>True when the set moves the mesh somewhere between <paramref name="from"/> and <paramref name="to"/>
    /// (a key other than its rest key, which carries the set's own name).</summary>
    public bool HasDeformation(string set, double from, double to) => _channels.Any(c => c.Set.Name == set
        && c.Keys.Any(k => k.Frame >= from && k.Frame <= to && k.Key.Name != set));

    /// <summary>Timeline position in frames (set from the animation player before skinning).</summary>
    public double Time { get; set; }

    /// <summary>Morphed source vertices for a mesh at <see cref="Time"/>, or null if no morph applies to it.</summary>
    public XxVertex[]? Apply(RenderMesh mesh)
    {
        XxVertex[]? vertices = null;
        float time = (float)Time;
        // Inactive sets first go back to the mesh's own shape, then the active ones move it: sets of one mesh overlap
        // (defo_mune_A covers both sides of defo_mune_L / _R), so restoring after applying would undo the movement.
        foreach (var channel in _channels)
        {
            if (!ReferenceEquals(channel.Mesh, mesh) || ActiveSet is not { } active || active(channel.Set.Name))
                continue;
            vertices ??= _morphed[mesh];
            var source = mesh.SourceVertices;
            foreach (int dst in channel.Set.MeshIndices)
            {
                if (dst >= vertices.Length || dst >= source.Length)
                    continue;
                vertices[dst].Position = source[dst].Position;
                vertices[dst].Normal = source[dst].Normal;
            }
        }
        foreach (var channel in _channels)
        {
            if (!ReferenceEquals(channel.Mesh, mesh) || ActiveSet is { } active && !active(channel.Set.Name))
                continue;
            var keys = channel.Keys;
            int hi = 0;
            while (hi < keys.Length && keys[hi].Frame < time)
                hi++;
            int lo = Math.Max(0, hi - 1);
            hi = Math.Min(hi, keys.Length - 1);
            var (frameA, a) = keys[lo];
            var (frameB, b) = keys[hi];
            float f = frameB > frameA ? Math.Clamp((time - frameA) / (frameB - frameA), 0, 1) : 0;

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
}
