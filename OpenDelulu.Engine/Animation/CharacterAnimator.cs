using System.Numerics;
using OpenDelulu.Engine.Assets;

namespace OpenDelulu.Engine.Animation;

/// <summary>
/// Drives a <see cref="RenderModel"/>: skeletal animation for the body, morphs for the face, then
/// CPU skinning into <see cref="RenderMesh.Vertices"/>. Call <see cref="Update"/> once per rendered frame
/// on the thread that uploads the vertices.
/// </summary>
public sealed class CharacterAnimator
{
    readonly Matrix4x4[] _local;
    readonly Matrix4x4[] _world;
    bool _dirty = true;

    public CharacterAnimator(RenderModel model)
    {
        Model = model;
        _local = (Matrix4x4[])model.Rig.BindLocal.Clone();
        _world = new Matrix4x4[model.Rig.Count];
        Gaze = EyeGaze.TryCreate(model);
        _focusFrames = new[] { "o01_N_Me_L", "o01_N_Me_R" }.Select(model.Rig.Find).Where(i => i >= 0).ToArray();
        if (_focusFrames.Length == 0 && model.Rig.Find("o01_J_Head") is var head and >= 0)
            _focusFrames = [head];
    }

    readonly int[] _focusFrames;

    /// <summary>Between the eyes in the current pose (camera close-ups aim here); null without eye/head frames.</summary>
    public Vector3? Focus { get; private set; }

    public RenderModel Model { get; }

    Matrix4x4 _placement = Matrix4x4.Identity;
    /// <summary>Where the character stands (e.g. a stage point); applied on top of the animated pose.</summary>
    public Matrix4x4 Placement
    {
        get => _placement;
        set { _placement = value; _dirty = true; }
    }

    AnimationPlayer? _body;
    public AnimationPlayer? Body
    {
        get => _body;
        set { _body = value; _dirty = true; _animatedMeshes = null; }
    }

    bool _animatedOnly;
    bool[]? _animatedMeshes;

    /// <summary>
    /// Re-skin only meshes under frames the body animation moves (stages: a few animated waves among hundreds of
    /// static meshes). Characters leave it off: the gaze, morphs and physics touch frames the clips do not.
    /// </summary>
    public bool SkinAnimatedOnly
    {
        get => _animatedOnly;
        set { _animatedOnly = value; _animatedMeshes = null; _dirty = true; }
    }

    /// <summary>True when the mesh's vertices may change from frame to frame (renderers re-upload only these).</summary>
    public bool IsMeshAnimated(int mesh)
    {
        if (!_animatedOnly || !_placement.IsIdentity)
            return true;
        _animatedMeshes ??= FindAnimatedMeshes();
        return _animatedMeshes[mesh];
    }

    bool[] FindAnimatedMeshes()
    {
        var rig = Model.Rig;
        var moved = new bool[rig.Count];
        foreach (int frame in _body?.TrackFrames ?? [])
            if (frame >= 0)
                moved[frame] = true;
        for (int i = 0; i < rig.Count; i++)
            if (rig.Parents[i] >= 0 && moved[rig.Parents[i]])
                moved[i] = true;
        return Model.Meshes.Select(m => moved[m.FrameIndex] || (m.BoneFrames?.Any(b => b >= 0 && moved[b]) ?? false)).ToArray();
    }

    public MorphPlayer? Face { get; set; }

    /// <summary>Morphs that follow the body animation's timeline (the player's hand); null for none.</summary>
    public TimelineMorph? Morph { get; set; }

    /// <summary>Timeline position for <see cref="Morph"/> when it follows another animation (the player's hand for the
    /// oil scene's .xm deformations); null = this body's own animation.</summary>
    public Func<double>? MorphClock { get; set; }

    /// <summary>Hair and breast/buttock secondary motion; null to disable.</summary>
    public SpringBones? Physics { get; set; }

    /// <summary>The voice being spoken; drives <see cref="MorphPlayer.MouthOpen"/> until it ends.</summary>
    public LipSync? Speech { get; set; }

    /// <summary>Eyes following <see cref="LookTarget"/>; null when the model has no eye frames.</summary>
    public EyeGaze? Gaze { get; }

    bool _eyesFollow = true;
    public bool EyesFollowTarget
    {
        get => _eyesFollow;
        set { _eyesFollow = value; _dirty = true; }
    }

    Vector3? _lookTarget;
    /// <summary>Point to look at, in model space (the viewer sets its camera position every frame).</summary>
    public Vector3? LookTarget
    {
        get => _lookTarget;
        set
        {
            if (_lookTarget is { } old && value is { } now && Vector3.DistanceSquared(old, now) < 1e-6f)
                return;
            _lookTarget = value;
            if (Gaze is not null && _eyesFollow)
                _dirty = true;
        }
    }

    /// <summary>True while something moves on its own (a playing clip or blinking).</summary>
    public bool IsAnimating => Body is { IsPlaying: true, Current.IsStatic: false } || Face?.AutoBlink == true || Speech is not null
        || Physics?.IsMoving == true;

    /// <summary>World matrix of a rig frame in the last evaluated pose, placement included.</summary>
    public Matrix4x4 WorldOf(int frame) => _world[frame];

    /// <summary>Forces a re-skin on the next <see cref="Update"/> (pose, expression or visibility changed).</summary>
    public void Invalidate() => _dirty = true;

    void ComputeWorld()
    {
        Model.Rig.ComputeWorld(_local, _world);
        if (!_placement.IsIdentity)
            for (int i = 0; i < _world.Length; i++)
                _world[i] *= _placement;
    }

    /// <summary>Advances time and re-skins visible meshes. Returns false when nothing changed.</summary>
    public bool Update(double seconds, Func<int, bool> isVisible)
    {
        if (!IsAnimating && !_dirty)
            return false;
        _dirty = false;

        Body?.Advance(seconds);
        Face?.Advance(seconds);
        if (Speech is { } speech && Face is not null)
        {
            Face.MouthOpen = speech.Update(seconds);
            if (!speech.IsActive)
            {
                Face.MouthOpen = 0;
                Speech = null;
            }
        }

        if (Body is not null)
            Body.Evaluate(_local);
        else
            Model.Rig.BindLocal.CopyTo(_local, 0);
        ComputeWorld();
        if (Gaze is not null && _eyesFollow && _lookTarget is { } target)
        {
            Gaze.Apply(_local, _world, target);
            ComputeWorld();
        }
        Physics?.Apply(_local, _world, seconds);
        if (Morph is not null)
            Morph.Time = MorphClock?.Invoke() ?? Body?.Time ?? 0;
        if (_focusFrames.Length > 0)
            Focus = _focusFrames.Aggregate(Vector3.Zero, (sum, i) => sum + _world[i].Translation) / _focusFrames.Length;

        var meshes = Model.Meshes;
        Parallel.For(0, meshes.Count, i =>
        {
            if (!isVisible(i) || !IsMeshAnimated(i))
                return;
            var mesh = meshes[i];
            RenderModel.Skin(mesh, _world, Face?.Apply(mesh) ?? Morph?.Apply(mesh));
        });
        return true;
    }
}
