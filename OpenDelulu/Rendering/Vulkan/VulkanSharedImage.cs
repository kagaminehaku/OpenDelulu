// Adapted from the Avalonia GpuInterop sample (MIT License, Copyright (c) AvaloniaUI OÜ).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace OpenDelulu.Rendering.Vulkan;

/// <summary>
/// An RGBA8 image whose memory can be handed to Avalonia's compositor: backed by a D3D11 shared texture,
/// or exported as an opaque Win32 handle / POSIX file descriptor.
/// </summary>
internal sealed unsafe class VulkanSharedImage : IDisposable
{
    readonly VulkanContext _vk;
    readonly ComPtr<ID3D11Texture2D> _d3dTexture;
    ImageLayout _layout = ImageLayout.Undefined;
    AccessFlags _access = AccessFlags.None;

    public VulkanSharedImage(VulkanContext vk, PixelSize size, IReadOnlyList<string> supportedHandleTypes)
    {
        _vk = vk;
        Size = size;
        var api = vk.Api;
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var handleType = windows
            ? (vk.D3DDevice.Handle != null ? ExternalMemoryHandleTypeFlags.D3D11TextureBit : ExternalMemoryHandleTypeFlags.OpaqueWin32Bit)
            : ExternalMemoryHandleTypeFlags.OpaqueFDBit;

        var external = new ExternalMemoryImageCreateInfo { SType = StructureType.ExternalMemoryImageCreateInfo, HandleTypes = handleType };
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            PNext = &external,
            ImageType = ImageType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            Extent = new Extent3D((uint)size.Width, (uint)size.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            Flags = ImageCreateFlags.CreateMutableFormatBit,
        };
        api.CreateImage(vk.Device, in imageInfo, null, out var image).ThrowOnError();
        Image = image;

        api.GetImageMemoryRequirements(vk.Device, image, out var requirements);
        var dedicated = new MemoryDedicatedAllocateInfoKHR { SType = StructureType.MemoryDedicatedAllocateInfoKhr, Image = image };
        var export = new ExportMemoryAllocateInfo { SType = StructureType.ExportMemoryAllocateInfo, HandleTypes = handleType, PNext = &dedicated };
        ImportMemoryWin32HandleInfoKHR import = default;
        if (handleType == ExternalMemoryHandleTypeFlags.D3D11TextureBit)
        {
            _d3dTexture = D3DMemoryHelper.CreateSharedTexture(vk.D3DDevice, size);
            import = new ImportMemoryWin32HandleInfoKHR
            {
                SType = StructureType.ImportMemoryWin32HandleInfoKhr,
                PNext = &dedicated,
                HandleType = ExternalMemoryHandleTypeFlags.D3D11TextureBit,
                Handle = D3DMemoryHelper.CreateSharedHandle(_d3dTexture),
            };
        }
        var allocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = import.Handle != IntPtr.Zero ? &import : &export,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = VulkanMemory.FindMemoryType(api, vk.PhysicalDevice, requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        api.AllocateMemory(vk.Device, in allocate, null, out var memory).ThrowOnError();
        Memory = memory;
        MemorySize = requirements.Size;
        api.BindImageMemory(vk.Device, image, memory, 0).ThrowOnError();
    }

    public PixelSize Size { get; }
    public Image Image { get; }
    public DeviceMemory Memory { get; }
    public ulong MemorySize { get; }
    public bool IsDirectXBacked => _d3dTexture.Handle != null;

    public void TransitionLayout(CommandBuffer commandBuffer, ImageLayout layout, AccessFlags access)
    {
        VulkanMemory.TransitionLayout(_vk.Api, commandBuffer, Image, _layout, _access, layout, access);
        _layout = layout;
        _access = access;
    }

    public IPlatformHandle Export()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (IsDirectXBacked)
                return new PlatformHandle(D3DMemoryHelper.CreateSharedHandle(_d3dTexture),
                    KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle);
            if (!_vk.Api.TryGetDeviceExtension<KhrExternalMemoryWin32>(_vk.Instance, _vk.Device, out var win32))
                throw new NotSupportedException(KhrExternalMemoryWin32.ExtensionName);
            var info = new MemoryGetWin32HandleInfoKHR
            {
                SType = StructureType.MemoryGetWin32HandleInfoKhr,
                Memory = Memory,
                HandleType = ExternalMemoryHandleTypeFlags.OpaqueWin32Bit,
            };
            win32.GetMemoryWin32Handle(_vk.Device, in info, out var handle).ThrowOnError();
            return new PlatformHandle(handle, KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle);
        }

        if (!_vk.Api.TryGetDeviceExtension<KhrExternalMemoryFd>(_vk.Instance, _vk.Device, out var fdExt))
            throw new NotSupportedException(KhrExternalMemoryFd.ExtensionName);
        var fdInfo = new MemoryGetFdInfoKHR
        {
            SType = StructureType.MemoryGetFDInfoKhr,
            Memory = Memory,
            HandleType = ExternalMemoryHandleTypeFlags.OpaqueFDBit,
        };
        fdExt.GetMemoryF(_vk.Device, in fdInfo, out int fd).ThrowOnError();
        return new PlatformHandle(fd, KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor);
    }

    public void Dispose()
    {
        _vk.Api.DestroyImage(_vk.Device, Image, null);
        _vk.Api.FreeMemory(_vk.Device, Memory, null);
        _d3dTexture.Dispose();
    }
}

/// <summary>Exportable semaphores used to hand images back and forth with the compositor.</summary>
internal sealed unsafe class VulkanSemaphorePair : IDisposable
{
    readonly VulkanContext _vk;

    public VulkanSemaphorePair(VulkanContext vk)
    {
        _vk = vk;
        var export = new ExportSemaphoreCreateInfo
        {
            SType = StructureType.ExportSemaphoreCreateInfo,
            HandleTypes = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ExternalSemaphoreHandleTypeFlags.OpaqueWin32Bit
                : ExternalSemaphoreHandleTypeFlags.OpaqueFDBit,
        };
        var info = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &export };
        vk.Api.CreateSemaphore(vk.Device, in info, null, out var available).ThrowOnError();
        vk.Api.CreateSemaphore(vk.Device, in info, null, out var finished).ThrowOnError();
        ImageAvailable = available;
        RenderFinished = finished;
    }

    public Semaphore ImageAvailable { get; }
    public Semaphore RenderFinished { get; }

    public IPlatformHandle Export(bool renderFinished)
    {
        var semaphore = renderFinished ? RenderFinished : ImageAvailable;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!_vk.Api.TryGetDeviceExtension<KhrExternalSemaphoreWin32>(_vk.Instance, _vk.Device, out var ext))
                throw new NotSupportedException(KhrExternalSemaphoreWin32.ExtensionName);
            var info = new SemaphoreGetWin32HandleInfoKHR
            {
                SType = StructureType.SemaphoreGetWin32HandleInfoKhr,
                Semaphore = semaphore,
                HandleType = ExternalSemaphoreHandleTypeFlags.OpaqueWin32Bit,
            };
            ext.GetSemaphoreWin32Handle(_vk.Device, in info, out var handle).ThrowOnError();
            return new PlatformHandle(handle, KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaqueNtHandle);
        }
        if (!_vk.Api.TryGetDeviceExtension<KhrExternalSemaphoreFd>(_vk.Instance, _vk.Device, out var fdExt))
            throw new NotSupportedException(KhrExternalSemaphoreFd.ExtensionName);
        var fdInfo = new SemaphoreGetFdInfoKHR
        {
            SType = StructureType.SemaphoreGetFDInfoKhr,
            Semaphore = semaphore,
            HandleType = ExternalSemaphoreHandleTypeFlags.OpaqueFDBit,
        };
        fdExt.GetSemaphoreF(_vk.Device, in fdInfo, out int fd).ThrowOnError();
        return new PlatformHandle(fd, KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaquePosixFileDescriptor);
    }

    public void Dispose()
    {
        _vk.Api.DestroySemaphore(_vk.Device, ImageAvailable, null);
        _vk.Api.DestroySemaphore(_vk.Device, RenderFinished, null);
    }
}
