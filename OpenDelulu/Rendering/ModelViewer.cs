using System;
using System.Numerics;
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
/// orbit camera and input: drag = rotate, wheel / pinch = zoom. With a <see cref="TouchHandler"/>, the primary
/// button or a finger touches the scene instead (the right mouse button still rotates).
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

    public static readonly StyledProperty<double> DefaultZoomProperty =
        AvaloniaProperty.Register<ModelViewer, double>(nameof(DefaultZoom), 1.0);

    /// <summary>Camera distance factor on (re)set: 1 = whole model, ~0.4 = upper body, ~0.2 = face.</summary>
    public double DefaultZoom
    {
        get => GetValue(DefaultZoomProperty);
        set => SetValue(DefaultZoomProperty, value);
    }

    public static readonly StyledProperty<ISceneTouchHandler?> TouchHandlerProperty =
        AvaloniaProperty.Register<ModelViewer, ISceneTouchHandler?>(nameof(TouchHandler));

    /// <summary>Receives presses on the scene as rays; null = every drag rotates the camera.</summary>
    public ISceneTouchHandler? TouchHandler
    {
        get => GetValue(TouchHandlerProperty);
        set => SetValue(TouchHandlerProperty, value);
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
    bool _touching;

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
        StartPreferred();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        RenderBackendSettings.Changed -= OnPreferenceChanged;
        RemoveBackend();
    }

    void OnPreferenceChanged() => StartPreferred();

    void StartPreferred()
    {
        _fallbackReason = null;
        _failedKinds.Clear();
        CreateBackend(RenderBackendSettings.Preferred == RenderBackendKind.OpenGl ? RenderBackendKind.OpenGl : RenderBackendKind.Vulkan);
    }

    /// <summary>
    /// A backend that neither reports Ready nor Failed in this time is treated as failed: OpenGlControlBase
    /// stays silent when the compositor has no OpenGL (e.g. Avalonia running on Vulkan).
    /// </summary>
    static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(3);

    readonly System.Collections.Generic.HashSet<RenderBackendKind> _failedKinds = [];
    Avalonia.Threading.DispatcherTimer? _startupTimer;

    void CreateBackend(RenderBackendKind kind)
    {
        RemoveBackend();
        IModelViewBackend backend = kind == RenderBackendKind.Vulkan ? new VulkanModelView() : new GlModelView();
        backend.Camera = _camera;
        backend.Scene = Scene;
        backend.Ready += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(backend, _backend))
            {
                _startupTimer?.Stop();
                UpdateBadge();
            }
        });
        backend.Failed += reason => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnBackendFailed(backend, kind, reason));
        _backend = backend;
        _layers.Children.Insert(0, (Control)backend);
        UpdateBadge();

        _startupTimer?.Stop();
        _startupTimer = new Avalonia.Threading.DispatcherTimer { Interval = StartupTimeout };
        _startupTimer.Tick += (_, _) =>
        {
            _startupTimer?.Stop();
            OnBackendFailed(backend, kind, Localization.Translator.Get("gfx.startTimeout"));
        };
        _startupTimer.Start();
    }

    void RemoveBackend()
    {
        _startupTimer?.Stop();
        if (_backend is Control control)
            _layers.Children.Remove(control);
        _backend = null;
    }

    void OnBackendFailed(IModelViewBackend backend, RenderBackendKind kind, string reason)
    {
        if (!ReferenceEquals(backend, _backend))
            return;
        _failedKinds.Add(kind);
        var other = kind == RenderBackendKind.Vulkan ? RenderBackendKind.OpenGl : RenderBackendKind.Vulkan;
        if (_failedKinds.Contains(other))
        {
            RemoveBackend();
            _badge.Text = Localization.Translator.Format("gfx.error", BackendName(kind), reason, _fallbackReason);
            return;
        }
        // Never leave the viewer blank: try the other backend once.
        _fallbackReason = Localization.Translator.Format("gfx.fallback", BackendName(kind), reason, BackendName(other))
            + (RenderBackendSettings.VulkanCompositor && kind == RenderBackendKind.OpenGl
                ? Localization.Translator.Get("gfx.vulkanUi")
                : "");
        CreateBackend(other);
    }

    static string BackendName(RenderBackendKind kind) => kind == RenderBackendKind.Vulkan ? "Vulkan" : "OpenGL";

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
        if (change.Property == TouchHandlerProperty && change.NewValue is not null)
            ReplayStartupTouch();
        if (change.Property == DefaultZoomProperty)
        {
            _camera.DefaultZoom = (float)DefaultZoom;
            _camera.Reset();
            RequestFrame();
            return;
        }
        if (change.Property != SceneProperty)
            return;
        if (change.OldValue is ModelScene oldScene)
            oldScene.Changed -= RequestFrame;
        if (change.NewValue is ModelScene newScene)
            newScene.Changed += RequestFrame;
        if (!KeepCamera || change.OldValue is null)
        {
            _camera.Reset();
            if (change.NewValue is ModelScene { CameraPitch: { } pitch })
                _camera.Pitch = pitch;
            if (change.NewValue is ModelScene { CameraZoom: { } zoom } && Services.AppHost.StartupZoom is null)
                _camera.Zoom = zoom;
        }
        if (_backend is not null)
            _backend.Scene = change.NewValue as ModelScene;
        RequestFrame();
    }

    void RequestFrame() => _backend?.RequestFrame();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var position = e.GetPosition(this);
        e.Pointer.Capture(this);
        bool primary = e.Pointer.Type != PointerType.Mouse || e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        if (primary && TouchHandler is { } handler && RayAt(position) is var (ray, up) && handler.Begin(ray, up))
        {
            _touching = true;
            RequestFrame();
            return;
        }
        _dragFrom = position;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_touching)
        {
            if (TouchHandler is { } handler && RayAt(e.GetPosition(this)) is var (ray, up))
                handler.Move(ray, up);
            RequestFrame();
            return;
        }
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
        EndTouch();
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndTouch();
        _dragFrom = null;
    }

    void EndTouch()
    {
        if (!_touching)
            return;
        _touching = false;
        TouchHandler?.End();
        RequestFrame();
    }

    /// <summary>The camera ray through a point of the viewer (same camera as the backends draw with).</summary>
    (Engine.Assets.Ray Ray, Vector3 Up)? RayAt(Point point)
    {
        if (Scene is not { } scene || Bounds.Width < 1 || Bounds.Height < 1)
            return null;
        float width = (float)Bounds.Width, height = (float)Bounds.Height;
        return _camera.RayThrough(scene.Subject, width / height, 2 * (float)point.X / width - 1, 1 - 2 * (float)point.Y / height);
    }

    /// <summary>
    /// Test hook (desktop <c>--touch</c>): once a touch handler is set, drags back and forth along
    /// <see cref="Services.AppHost.StartupTouch"/> for a few seconds through the same code as real input, then lets go.
    /// </summary>
    void ReplayStartupTouch()
    {
        if (Services.AppHost.StartupTouch is not { Length: >= 1 } path)
            return;
        Services.AppHost.StartupTouch = null;
        var started = DateTime.UtcNow;
        bool down = false;
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            double seconds = (DateTime.UtcNow - started).TotalSeconds - 1;
            if (seconds < 0)
                return;
            // Back and forth along the path, one leg per second.
            double t = seconds % 2 > 1 ? 2 - seconds % 2 : seconds % 2;
            double f = t * (path.Length - 1);
            int i = Math.Min((int)f, Math.Max(0, path.Length - 2));
            var a = path[i];
            var b = path[Math.Min(i + 1, path.Length - 1)];
            double u = f - i;
            var point = new Point((a.X + (b.X - a.X) * u) * Bounds.Width, (a.Y + (b.Y - a.Y) * u) * Bounds.Height);
            if (TouchHandler is not { } handler || RayAt(point) is not var (ray, up))
                return;
            if (!down)
            {
                down = handler.Begin(ray, up);
                _touching = down;
            }
            else if (seconds < Services.AppHost.StartupTouchSeconds)
                handler.Move(ray, up);
            else
            {
                timer.Stop();
                EndTouch();
            }
            RequestFrame();
        };
        timer.Start();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _camera.ZoomBy(MathF.Pow(0.88f, (float)e.Delta.Y));
        RequestFrame();
        e.Handled = true;
    }
}
