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
    }

    public RenderModel Model { get; }

    AnimationPlayer? _body;
    public AnimationPlayer? Body
    {
        get => _body;
        set { _body = value; _dirty = true; }
    }

    public MorphPlayer? Face { get; set; }

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
