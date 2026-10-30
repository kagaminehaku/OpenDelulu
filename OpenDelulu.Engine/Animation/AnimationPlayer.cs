using System.Numerics;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Formats.Xa;

namespace OpenDelulu.Engine.Animation;

/// <summary>A named range of the shared .xa timeline.</summary>
public sealed record AnimationClipInfo(int Index, string Name, float Start, float End, float Speed, int Next)
{
    public bool IsStatic => End <= Start;
    public string DisplayName => Name.Length > 0 ? $"{Index}. {Name}" : $"{Index}.";
}

/// <summary>
/// Plays the skeletal part of an .xa on a <see cref="Rig"/>: samples every track at the current
/// timeline position (lerp/slerp between keys) and follows each clip's "next" link when it ends.
/// </summary>
public sealed class AnimationPlayer
{
    /// <summary>
    /// Timeline frames per second at clip speed 1. Not stored in the data; 10 makes the idle loops
    /// (10-20 frames at speed 1.2-1.5) last about one breath. Adjust with <see cref="SpeedScale"/>.
    /// </summary>
    public const double BaseFramesPerSecond = 10;

    readonly XaAnimationTrack[] _tracks;
    readonly int[] _trackFrame;
    readonly Rig _rig;

    public AnimationPlayer(Rig rig, XaAnimationSection animation, string name)
    {
        _rig = rig;
        Name = name;
        _tracks = animation.Tracks.Where(t => t.Keyframes.Length > 0).ToArray();
        _trackFrame = _tracks.Select(t => rig.Find(t.Name)).ToArray();
        Clips = animation.UsedClips()
            .Select(c => new AnimationClipInfo(c.Index, c.Clip.Name, c.Clip.Start, c.Clip.End, c.Clip.Speed, c.Clip.Next))
            .ToList();
        BoundTracks = _trackFrame.Count(i => i >= 0);
        var first = Clips.FirstOrDefault(c => !c.IsStatic) ?? Clips.FirstOrDefault();
        if (first is not null)
            Play(first.Index);
    }

    public string Name { get; }
    public IReadOnlyList<AnimationClipInfo> Clips { get; }
    /// <summary>Rig frames driven by a track (-1 entries for tracks whose frame is missing).</summary>
    public IReadOnlyList<int> TrackFrames => _trackFrame;

    /// <summary>Tracks whose bone exists in the rig (diagnostics).</summary>
    public int BoundTracks { get; }
    public AnimationClipInfo? Current { get; private set; }
    /// <summary>Position on the shared timeline, in frames.</summary>
    public double Time { get; private set; }
    public double SpeedScale { get; set; } = 1;
    public bool IsPlaying { get; set; } = true;
    /// <summary>When false, clips loop on themselves instead of following "next".</summary>
    public bool FollowNext { get; set; } = true;

    /// <summary>When true (and not following "next"), a clip stops on its last frame instead of looping.</summary>
    public bool HoldAtEnd { get; set; }

    public event Action<AnimationClipInfo>? ClipChanged;

    public void Play(int clipIndex)
    {
        Current = Clips.FirstOrDefault(c => c.Index == clipIndex) ?? Current;
        Time = Current?.Start ?? 0;
        if (Current is not null)
            ClipChanged?.Invoke(Current);
    }

    public void Advance(double seconds)
    {
        if (!IsPlaying || Current is not { IsStatic: false } clip)
            return;
        Time += seconds * BaseFramesPerSecond * Math.Max(0.01, clip.Speed) * SpeedScale;
        if (HoldAtEnd && !FollowNext && Time >= clip.End)
        {
            Time = clip.End;
            return;
        }
        int guard = 0;
        while (Current is { IsStatic: false } c && Time >= c.End && guard++ < 16)
        {
            double overflow = Time - c.End;
            // "Next" pointing at a static clip (index 0 is usually the T-pose / initial state) means "loop":
            // loops such as 平泳ぎループ (breaststroke loop) end with next = 0.
            var next = FollowNext ? Clips.FirstOrDefault(x => x.Index == c.Next && !x.IsStatic) ?? c : c;
            if (!ReferenceEquals(next, c))
            {
                Current = next;
                ClipChanged?.Invoke(next);
            }
            Time = Current.Start + (Current.IsStatic ? 0 : overflow % Math.Max(1e-3, Current.End - Current.Start));
        }
    }

    /// <summary>Writes the local matrices of the current pose (bind pose for untracked frames).</summary>
    public void Evaluate(Span<Matrix4x4> local)
    {
        _rig.BindLocal.CopyTo(local);
        float time = (float)Time;
        for (int t = 0; t < _tracks.Length; t++)
        {
            int frame = _trackFrame[t];
            if (frame >= 0)
                local[frame] = Sample(_tracks[t].Keyframes, time);
        }
    }

    static Matrix4x4 Sample(XaKeyframe[] keys, float time)
    {
        if (time <= keys[0].Index)
            return keys[0].ToMatrix();
        if (time >= keys[^1].Index)
            return keys[^1].ToMatrix();

        // Keys are sorted by frame index; find the pair around "time".
        int lo = 0, hi = keys.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (keys[mid].Index <= time) lo = mid; else hi = mid;
        }
        ref readonly var a = ref keys[lo];
        ref readonly var b = ref keys[hi];
        float f = (time - a.Index) / Math.Max(1, b.Index - a.Index);
        var rotation = Quaternion.Slerp(a.Rotation, b.Rotation, f);
        return Matrix4x4.CreateScale(Vector3.Lerp(a.Scaling, b.Scaling, f))
             * Matrix4x4.CreateFromQuaternion(rotation)
             * Matrix4x4.CreateTranslation(Vector3.Lerp(a.Translation, b.Translation, f));
    }
}
