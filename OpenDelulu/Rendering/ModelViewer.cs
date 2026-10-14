using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OpenDelulu.Rendering.Gl;
using OpenDelulu.Rendering.Vulkan;

namespace OpenDelulu.Rendering;

/// <summary>
/// 3D viewport for a <see cref="ModelScene"/>. Picks a render backend (Vulkan or OpenGL ES, see
/// <see cref="RenderBackendSettings"/>), falls back to OpenGL when Vulkan cannot run, and owns the
/// orbit camera and input: drag = rotate, wheel / pinch = zoom.
/// </summary>
public sealed class ModelViewer : Border
{
    public static readonly StyledProperty<ModelScene?> SceneProperty =
        AvaloniaProperty.Register<ModelViewer, ModelScene?>(nameof(Scene));

    public ModelScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public static readonly StyledProperty<bool> KeepCameraProperty =
        AvaloniaProperty.Register<ModelViewer, bool>(nameof(KeepCamera));

    /// <summary>Keep rotation/zoom when the scene changes (e.g. switching swimsuits).</summary>
    public bool KeepCamera
    {
        get => GetValue(KeepCameraProperty);
        set => SetValue(KeepCameraProperty, value);
    }

    readonly OrbitCamera _camera = new();
    readonly Grid _layers = new();
    readonly TextBlock _badge = new()
    {
        Margin = new Thickness(8),
        FontSize = 11,
        Opacity = 0.75,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false,
    };
    IModelViewBackend? _backend;
    string? _fallbackReason;
    Point? _dragFrom;

    public ModelViewer()
    {
        Background = Brushes.Transparent; // receive pointer input over the GPU surface
        Child = _layers;
        _layers.Children.Add(_badge);
        GestureRecognizers.Add(new PinchGestureRecognizer());
        AddHandler(PinchEvent, (_, e) =>
        {
            _camera.ZoomBy(1f / (float)Math.Clamp(e.Scale, 0.5, 2));
            _backend?.RequestFrame();
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RenderBackendSettings.Changed += OnPreferenceChanged;
        CreateBackend(RenderBackendSettings.Preferred == RenderBackendKind.OpenGl ? RenderBackendKind.OpenGl : RenderBackendKind.Vulkan);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        RenderBackendSettings.Changed -= OnPreferenceChanged;
        RemoveBackend();
    }

    void OnPreferenceChanged()
    {
        _fallbackReason = null;
        CreateBackend(RenderBackendSettings.Preferred == RenderBackendKind.OpenGl ? RenderBackendKind.OpenGl : RenderBackendKind.Vulkan);
    }

    void CreateBackend(RenderBackendKind kind)
    {
        RemoveBackend();
        IModelViewBackend backend = kind == RenderBackendKind.Vulkan ? new VulkanModelView() : new GlModelView();
        backend.Camera = _camera;
        backend.Scene = Scene;
        backend.Ready += () => UpdateBadge();
        backend.Failed += reason => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnBackendFailed(backend, kind, reason));
        _backend = backend;
        _layers.Children.Insert(0, (Control)backend);
        UpdateBadge();
    }

    void RemoveBackend()
    {
        if (_backend is Control control)
            _layers.Children.Remove(control);
        _backend = null;
    }

    void OnBackendFailed(IModelViewBackend backend, RenderBackendKind kind, string reason)
    {
        if (!ReferenceEquals(backend, _backend))
            return;
        if (kind == RenderBackendKind.Vulkan)
        {
            // Auto and Vulkan both fall back so the viewer never stays blank.
            _fallbackReason = $"Vulkan không dùng được ({reason}), dùng OpenGL";
            CreateBackend(RenderBackendKind.OpenGl);
        }
        else
        {
            _badge.Text = "Lỗi đồ họa: " + reason;
        }
    }

    void UpdateBadge()
    {
        string text = _backend?.Description ?? "";
        if (_fallbackReason is not null)
            text += "\n" + _fallbackReason;
        _badge.Text = text;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SceneProperty)
            return;
        if (change.OldValue is ModelScene oldScene)
            oldScene.Changed -= RequestFrame;
        if (change.NewValue is ModelScene newScene)
            newScene.Changed += RequestFrame;
        if (!KeepCamera || change.OldValue is null)
            _camera.Reset();
        if (_backend is not null)
            _backend.Scene = change.NewValue as ModelScene;
        RequestFrame();
    }

    void RequestFrame() => _backend?.RequestFrame();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragFrom = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from)
            return;
        var to = e.GetPosition(this);
        _camera.Rotate(-(float)(to.X - from.X) * 0.01f, (float)(to.Y - from.Y) * 0.01f);
        _dragFrom = to;
        RequestFrame();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _camera.ZoomBy(MathF.Pow(0.88f, (float)e.Delta.Y));
        RequestFrame();
        e.Handled = true;
    }
}
