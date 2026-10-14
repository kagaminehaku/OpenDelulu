using System;
using System.Linq;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Assets;

namespace OpenDelulu.Rendering;

/// <summary>What the <see cref="ModelViewer"/> draws: a model, per-mesh visibility and an optional animator.</summary>
public sealed class ModelScene(RenderModel model, CharacterAnimator? animator = null)
{
    readonly bool[] _visible = model.Meshes.Select(m => m.Visible).ToArray();

    public RenderModel Model { get; } = model;

    /// <summary>Re-skins the meshes each frame; null for static models.</summary>
    public CharacterAnimator? Animator { get; } = animator;

    /// <summary>Raised when visibility or animation settings change; the viewer redraws.</summary>
    public event Action? Changed;

    public bool IsVisible(int meshIndex) => _visible[meshIndex];

    public void SetVisible(int meshIndex, bool visible)
    {
        if (_visible[meshIndex] == visible)
            return;
        _visible[meshIndex] = visible;
        Animator?.Invalidate();
        Changed?.Invoke();
    }

    /// <summary>Call after changing the animator (clip, expression, play state).</summary>
    public void NotifyChanged()
    {
        Animator?.Invalidate();
        Changed?.Invoke();
    }
}
