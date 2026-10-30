// Composition hosting adapted from the Avalonia GpuInterop sample (MIT License, Copyright (c) AvaloniaUI OÜ).
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;

namespace OpenDelulu.Rendering.Vulkan;

/// <summary>
/// Vulkan backend: renders with our own Vulkan device and hands each frame to Avalonia's compositor
/// through <see cref="ICompositionGpuInterop"/> (no CPU copies).
/// </summary>
internal sealed class VulkanModelView : Control, IModelViewBackend
{
    CompositionSurfaceVisual? _visual;
    Compositor? _compositor;
    CompositionDrawingSurface? _surface;
    VulkanContext? _context;
    VulkanSwapchain? _swapchain;
    VulkanSceneRenderer? _renderer;
    readonly FrameClock _clock = new();
    readonly Action _update;
    bool _updateQueued;
    bool _ready;

    public VulkanModelView() => _update = UpdateFrame;

    public ModelScene? Scene { get; set; }
    public OrbitCamera Camera { get; set; } = new();
    public string Description { get; private set; } = "Vulkan";

    public event Action<string>? Failed;
    public event Action? Ready;

    public void RequestFrame()
    {
        if (!_ready || _updateQueued || _compositor is null)
            return;
        _updateQueued = true;
        _compositor.RequestCompositionUpdate(_update);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ = InitializeAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _ready = false;
        _ = ReleaseAsync();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
            RequestFrame();
    }

    async Task InitializeAsync()
    {
        try
        {
            var self = ElementComposition.GetElementVisual(this);
            if (self is null)
            {
                Failed?.Invoke(Localization.Translator.Get("gfx.noVisual"));
                return;
            }
            _compositor = self.Compositor;
            var interop = await _compositor.TryGetCompositionGpuInterop();
            if (interop is null)
            {
                Failed?.Invoke(Localization.Translator.Get("gfx.noInterop"));
                return;
            }

            var (context, info) = VulkanContext.TryCreate(interop);
            if (context is null)
            {
                Failed?.Invoke(info);
                return;
            }
            _context = context;
            _renderer = new VulkanSceneRenderer(context);

            _surface = _compositor.CreateDrawingSurface();
            _visual = _compositor.CreateSurfaceVisual();
            _visual.Size = new Vector(Bounds.Width, Bounds.Height);
            _visual.Surface = _surface;
            ElementComposition.SetElementChildVisual(this, _visual);
            _swapchain = new VulkanSwapchain(context, interop, _surface);

            Description = $"Vulkan · {info}";
            _ready = true;
            Ready?.Invoke();
            RequestFrame();
        }
        catch (Exception ex)
        {
            await ReleaseAsync();
            Failed?.Invoke(ex.Message);
        }
    }

    void UpdateFrame()
    {
        _updateQueued = false;
        if (!_ready || _swapchain is null || _renderer is null || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;
        _visual!.Size = new Vector(Bounds.Width, Bounds.Height);
        var size = PixelSize.FromSize(Bounds.Size, topLevel.RenderScaling);
        if (size.Width < 1 || size.Height < 1)
            return;

        try
        {
            bool animating;
            using (_swapchain.BeginDraw(size, out var image))
                animating = _renderer.Render(image, Scene, Camera, _clock.Tick());
            if (Scene?.CaptureRequest is { } capture)
            {
                Scene.CaptureRequest = null;
                capture(_renderer.ReadPixels(), size.Width, size.Height);
            }
            if (animating)
                RequestFrame();
            else
                _clock.Pause();
        }
        catch (Exception ex)
        {
            _ready = false;
            Failed?.Invoke(ex.Message);
        }
    }

    async Task ReleaseAsync()
    {
        _renderer?.Dispose();
        _renderer = null;
        if (_swapchain is not null)
            await _swapchain.DisposeAsync();
        _swapchain = null;
        _surface?.Dispose();
        _surface = null;
        _context?.Dispose();
        _context = null;
    }
}
