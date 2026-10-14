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

    public TimelineMorph(RenderModel model, XaMorphSection morphs)
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
            _channels.Add(new Channel(mesh, set, keys));
            _morphed.TryAdd(mesh, (XxVertex[])mesh.SourceVertices.Clone());
        }
    }

    public int ChannelCount => _channels.Count;

    /// <summary>Timeline position in frames (set from the animation player before skinning).</summary>
    public double Time { get; set; }

    /// <summary>Morphed source vertices for a mesh at <see cref="Time"/>, or null if no morph applies to it.</summary>
    public XxVertex[]? Apply(RenderMesh mesh)
    {
        XxVertex[]? vertices = null;
        float time = (float)Time;
        foreach (var channel in _channels)
        {
            if (!ReferenceEquals(channel.Mesh, mesh))
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
