using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Formats.Imaging;

namespace OpenDelulu.Rendering;

/// <summary>Where the camera orbits: the subject's centre and size, its close-up point and how far the scene reaches.</summary>
/// <param name="FacingYaw">Where the subject faces (radians about Y, 0 = +Z); the default view is from the front.</param>
public readonly record struct CameraSubject(Vector3 Center, float Radius, Vector3 Aim, float SceneRadius, float FacingYaw = 0);

/// <summary>
/// What the <see cref="ModelViewer"/> draws: one or more models (e.g. a stage and a character standing on it),
/// per-mesh visibility and optional animators. Meshes and textures of all objects are flattened into one list,
/// so renderers index them the same way as for a single model.
/// </summary>
public sealed class ModelScene
{
    sealed record SceneObject(RenderModel Model, CharacterAnimator? Animator, int MeshOffset, int TextureOffset)
    {
        public Vector3 Light = Vector3.One;
    }

    readonly List<SceneObject> _objects = [];
    readonly List<RenderMesh> _meshes = [];
    readonly List<RgbaImage?> _textures = [];
    readonly List<int> _owner = [];
    readonly bool[] _visible;
    readonly int _subject;

    public ModelScene(RenderModel model, CharacterAnimator? animator = null) : this([(model, animator)], 0)
    {
    }

    /// <param name="subject">Index of the object the camera frames (the character on a stage).</param>
    public ModelScene(IReadOnlyList<(RenderModel Model, CharacterAnimator? Animator)> objects, int subject)
    {
        foreach (var (model, animator) in objects)
        {
            _objects.Add(new SceneObject(model, animator, _meshes.Count, _textures.Count));
            foreach (var mesh in model.Meshes)
            {
                _meshes.Add(mesh);
                _owner.Add(_objects.Count - 1);
            }
            _textures.AddRange(model.Textures);
        }
        _visible = _meshes.Select(m => m.Visible).ToArray();
        _subject = Math.Clamp(subject, 0, _objects.Count - 1);
    }

    /// <summary>The framed object's model (the only one for single-model scenes; mesh toggles use its indices).</summary>
    public RenderModel Model => _objects[_subject].Model;

    /// <summary>The framed object's animator; null for static models.</summary>
    public CharacterAnimator? Animator => _objects[_subject].Animator;

    public IReadOnlyList<RenderMesh> Meshes => _meshes;
    public IReadOnlyList<RgbaImage?> Textures => _textures;

    /// <summary>Index into <see cref="Textures"/> for a mesh, or -1.</summary>
    public int TextureIndex(int mesh) =>
        _meshes[mesh].TextureIndex < 0 ? -1 : _meshes[mesh].TextureIndex + _objects[_owner[mesh]].TextureOffset;

    /// <summary>
    /// Multiplies the material colors of one object's meshes (1 = as lit by the viewer's light). Used to dim and
    /// tint a character to the stage's time of day.
    /// </summary>
    public void SetLight(int objectIndex, Vector3 light)
    {
        _objects[objectIndex].Light = light;
        Changed?.Invoke();
    }

    public Vector3 Light(int mesh) => _objects[_owner[mesh]].Light;

    /// <summary>True when the mesh's vertices change while animating (re-upload after <see cref="Update"/>).</summary>
    public bool IsDynamic(int mesh) =>
        _objects[_owner[mesh]] is { Animator: { } animator } o && animator.IsMeshAnimated(mesh - o.MeshOffset);

    /// <summary>Raised when visibility or animation settings change; the viewer redraws.</summary>
    public event Action? Changed;

    public bool IsVisible(int meshIndex) => _visible[meshIndex];

    public void SetVisible(int meshIndex, bool visible)
    {
        if (_visible[meshIndex] == visible)
            return;
        _visible[meshIndex] = visible;
        _objects[_owner[meshIndex]].Animator?.Invalidate();
        Changed?.Invoke();
    }

    /// <summary>Call after changing an animator (clip, expression, play state, placement).</summary>
    public void NotifyChanged()
    {
        foreach (var o in _objects)
            o.Animator?.Invalidate();
        Changed?.Invoke();
    }

    public bool IsAnimating => _objects.Any(o => o.Animator?.IsAnimating == true);

    /// <summary>Points the characters' eyes at the camera.</summary>
    public void SetLookTarget(Vector3 eye)
    {
        foreach (var o in _objects)
            if (o.Animator is { } animator)
                animator.LookTarget = eye;
    }

    /// <summary>Advances every animator; true when some vertices changed (re-upload the dynamic meshes).</summary>
    public bool Update(double seconds)
    {
        bool changed = false;
        foreach (var o in _objects)
        {
            if (o.Animator is not { } animator)
                continue;
            int offset = o.MeshOffset;
            changed |= animator.Update(seconds, i => _visible[offset + i]);
        }
        return changed;
    }

    public CameraSubject Subject
    {
        get
        {
            var o = _objects[_subject];
            var placement = o.Animator?.Placement ?? Matrix4x4.Identity;
            var model = o.Model;
            var center = Vector3.Transform(model.Center, placement);
            var aim = o.Animator?.Focus
                ?? Vector3.Transform(model.FocusPoint ?? model.Center + new Vector3(0, (model.BoundsMax.Y - model.Center.Y) * 0.85f, 0), placement);
            float sceneRadius = _objects.Max(x => Vector3.Distance(center, Vector3.Transform(x.Model.Center, x.Animator?.Placement ?? Matrix4x4.Identity)) + x.Model.Radius);
            var forward = Vector3.TransformNormal(Vector3.UnitZ, placement);
            return new CameraSubject(center, model.Radius, aim, sceneRadius, MathF.Atan2(forward.X, forward.Z));
        }
    }
}
