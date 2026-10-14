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

    AnimationPlayer? _body;
    public AnimationPlayer? Body
    {
        get => _body;
        set { _body = value; _dirty = true; }
    }

    public MorphPlayer? Face { get; set; }

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
    public bool IsAnimating => Body is { IsPlaying: true, Current.IsStatic: false } || Face?.AutoBlink == true;

    /// <summary>Forces a re-skin on the next <see cref="Update"/> (pose, expression or visibility changed).</summary>
    public void Invalidate() => _dirty = true;

    /// <summary>Advances time and re-skins visible meshes. Returns false when nothing changed.</summary>
    public bool Update(double seconds, Func<int, bool> isVisible)
    {
        if (!IsAnimating && !_dirty)
            return false;
        _dirty = false;

        Body?.Advance(seconds);
        Face?.Advance(seconds);

        if (Body is not null)
            Body.Evaluate(_local);
        else
            Model.Rig.BindLocal.CopyTo(_local, 0);
        Model.Rig.ComputeWorld(_local, _world);
        if (Gaze is not null && _eyesFollow && _lookTarget is { } target)
        {
            Gaze.Apply(_local, _world, target);
            Model.Rig.ComputeWorld(_local, _world);
        }
        if (_focusFrames.Length > 0)
            Focus = _focusFrames.Aggregate(Vector3.Zero, (sum, i) => sum + _world[i].Translation) / _focusFrames.Length;

        var meshes = Model.Meshes;
        Parallel.For(0, meshes.Count, i =>
        {
            if (!isVisible(i))
                return;
            var mesh = meshes[i];
            RenderModel.Skin(mesh, _world, Face?.Apply(mesh));
        });
        return true;
    }
}
