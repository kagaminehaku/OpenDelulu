// Adapted from Avalonia's SwapchainBase and the GpuInterop sample (MIT License, Copyright (c) AvaloniaUI OÜ).
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Silk.NET.Vulkan;

namespace OpenDelulu.Rendering.Vulkan;

/// <summary>
/// Rotates shared images between Vulkan and the compositor: an image is reused only after the compositor
/// finished presenting it, and only when at least two images of the current size are in flight.
/// </summary>
internal sealed class VulkanSwapchain(VulkanContext vk, ICompositionGpuInterop interop, CompositionDrawingSurface target) : IAsyncDisposable
{
    readonly List<SwapchainImage> _pending = [];

    /// <summary>Acquires an image to draw into; disposing the result presents it.</summary>
    public IDisposable BeginDraw(PixelSize size, out VulkanSharedImage image)
    {
        // Waits for all previously submitted GPU work, so mapped buffers can be updated afterwards.
        vk.Pool.FreeUsedCommandBuffers();

        var swapchainImage = FindReusable(size) ?? new SwapchainImage(vk, size, interop, target);
        swapchainImage.BeginDraw();
        _pending.Remove(swapchainImage);
        image = swapchainImage.Image;
        return new Presenter(this, swapchainImage);
    }

    SwapchainImage? FindReusable(PixelSize size)
    {
        SwapchainImage? found = null;
        bool multiple = false;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var image = _pending[i];
            bool ready = image.LastPresent is null || image.LastPresent.Status == TaskStatus.RanToCompletion;
            bool matches = image.Size == size;
            if (image.LastPresent?.IsFaulted == true || (!matches && ready))
            {
                _ = image.DisposeAsync();
                _pending.RemoveAt(i);
                continue;
            }
            if (matches && ready)
            {
                if (found is null) found = image;
                else multiple = true;
            }
        }
        // Keep at least one image of this size in flight to avoid stalling the UI thread.
        return multiple ? found : null;
    }

    sealed class Presenter(VulkanSwapchain owner, SwapchainImage image) : IDisposable
    {
        public void Dispose()
        {
            image.Present();
            owner._pending.Add(image);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var image in _pending)
            await image.DisposeAsync();
        _pending.Clear();
    }

    sealed class SwapchainImage : IAsyncDisposable
    {
        readonly VulkanContext _vk;
        readonly ICompositionGpuInterop _interop;
        readonly CompositionDrawingSurface _target;
        readonly VulkanSemaphorePair? _semaphores;
        ICompositionImportedGpuImage? _imported;
        ICompositionImportedGpuSemaphore? _available, _finished;
        bool _initial = true;

        public SwapchainImage(VulkanContext vk, PixelSize size, ICompositionGpuInterop interop, CompositionDrawingSurface target)
        {
            _vk = vk;
            _interop = interop;
            _target = target;
            Size = size;
            Image = new VulkanSharedImage(vk, size, interop.SupportedImageHandleTypes);
            if (!Image.IsDirectXBacked)
                _semaphores = new VulkanSemaphorePair(vk);
        }

        public PixelSize Size { get; }
        public VulkanSharedImage Image { get; }
        public Task? LastPresent { get; private set; }

        public void BeginDraw()
        {
            var buffer = _vk.Pool.CreateCommandBuffer();
            buffer.BeginRecording();
            Image.TransitionLayout(buffer.Handle, ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentReadBit);
            if (Image.IsDirectXBacked)
                buffer.Submit(keyedMutex: new() { AcquireKey = 0, DeviceMemory = Image.Memory });
            else if (_initial)
                buffer.Submit();
            else
                buffer.Submit([_semaphores!.ImageAvailable], [PipelineStageFlags.AllGraphicsBit]);
            _initial = false;
        }

        public void Present()
        {
            var buffer = _vk.Pool.CreateCommandBuffer();
            buffer.BeginRecording();
            Image.TransitionLayout(buffer.Handle, ImageLayout.TransferSrcOptimal, AccessFlags.TransferWriteBit);
            if (Image.IsDirectXBacked)
            {
                buffer.Submit(keyedMutex: new() { ReleaseKey = 1, DeviceMemory = Image.Memory });
            }
            else
            {
                buffer.Submit(signalSemaphores: [_semaphores!.RenderFinished]);
                _available ??= _interop.ImportSemaphore(_semaphores.Export(false));
                _finished ??= _interop.ImportSemaphore(_semaphores.Export(true));
            }

            _imported ??= _interop.ImportImage(Image.Export(), new PlatformGraphicsExternalImageProperties
            {
                Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,
                Width = Size.Width,
                Height = Size.Height,
                MemorySize = Image.MemorySize,
                VulkanProperties = new PlatformGraphicsExternalImageVulkanProperties { Layout = (int)ImageLayout.TransferSrcOptimal },
            });

            LastPresent = Image.IsDirectXBacked
                ? _target.UpdateWithKeyedMutexAsync(_imported, 1, 0)
                : _target.UpdateWithSemaphoresAsync(_imported, _finished!, _available!);
        }

        public async ValueTask DisposeAsync()
        {
            if (LastPresent is not null)
            {
                try { await LastPresent; }
                catch (Exception) { /* presentation failures are reported by the next frame */ }
            }
            if (_imported is not null) await _imported.DisposeAsync();
            if (_available is not null) await _available.DisposeAsync();
            if (_finished is not null) await _finished.DisposeAsync();
            _semaphores?.Dispose();
            Image.Dispose();
        }
    }
}
