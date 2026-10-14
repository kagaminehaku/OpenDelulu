using System;
using System.Linq;
using OpenDelulu.Engine.Assets;

namespace OpenDelulu.Rendering;

/// <summary>What the <see cref="ModelViewer"/> draws: a model plus per-mesh visibility.</summary>
public sealed class ModelScene(RenderModel model)
{
    readonly bool[] _visible = model.Meshes.Select(m => m.Visible).ToArray();

    public RenderModel Model { get; } = model;

    /// <summary>Raised when visibility changes; the viewer redraws.</summary>
    public event Action? Changed;

    public bool IsVisible(int meshIndex) => _visible[meshIndex];

    public void SetVisible(int meshIndex, bool visible)
    {
        if (_visible[meshIndex] == visible)
            return;
        _visible[meshIndex] = visible;
        Changed?.Invoke();
    }
}
