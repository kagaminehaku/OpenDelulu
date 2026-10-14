// Parts of the Vulkan interop layer are adapted from the Avalonia GpuInterop sample
// (https://github.com/AvaloniaUI/Avalonia/tree/master/samples/GpuInterop), MIT License, Copyright (c) AvaloniaUI OÜ.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace OpenDelulu.Rendering.Vulkan;

internal static class VulkanExtensions
{
    public static void ThrowOnError(this Result result)
    {
        if (result != Result.Success)
            throw new VulkanException(result);
    }
}

internal sealed class VulkanException(Result result) : Exception($"Vulkan error {result}.")
{
    public Result Result { get; } = result;
}

/// <summary>A NUL-terminated ANSI string in unmanaged memory.</summary>
internal sealed unsafe class ByteString(string value) : IDisposable
{
    public IntPtr Pointer { get; } = Marshal.StringToHGlobalAnsi(value);

    public void Dispose() => Marshal.FreeHGlobal(Pointer);

    public static implicit operator byte*(ByteString s) => (byte*)s.Pointer;
}

internal sealed unsafe class ByteStringList : IDisposable
{
    readonly List<ByteString> _items;
    readonly byte** _pointers;

    public ByteStringList(IEnumerable<string> items)
    {
        _items = items.Select(x => new ByteString(x)).ToList();
        _pointers = (byte**)Marshal.AllocHGlobal(IntPtr.Size * Math.Max(1, _items.Count));
        for (int i = 0; i < _items.Count; i++)
            _pointers[i] = (byte*)_items[i].Pointer;
    }

    public uint Count => (uint)_items.Count;

    public void Dispose()
    {
        foreach (var item in _items)
            item.Dispose();
        Marshal.FreeHGlobal((IntPtr)_pointers);
    }

    public static implicit operator byte**(ByteStringList list) => list._pointers;
}

internal static unsafe class VulkanMemory
{
    public static uint FindMemoryType(Vk api, PhysicalDevice physicalDevice, uint typeBits, MemoryPropertyFlags flags)
    {
        api.GetPhysicalDeviceMemoryProperties(physicalDevice, out var properties);
        for (int i = 0; i < properties.MemoryTypeCount; i++)
            if ((typeBits & (1u << i)) != 0 && (properties.MemoryTypes[i].PropertyFlags & flags) == flags)
                return (uint)i;
        throw new NotSupportedException($"No Vulkan memory type with {flags}.");
    }

    public static void TransitionLayout(Vk api, CommandBuffer commandBuffer, Image image,
        ImageLayout from, AccessFlags fromAccess, ImageLayout to, AccessFlags toAccess,
        ImageAspectFlags aspect = ImageAspectFlags.ColorBit, uint mipLevels = 1)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = fromAccess,
            DstAccessMask = toAccess,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, mipLevels, 0, 1),
        };
        api.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, in barrier);
    }
}

/// <summary>A buffer in host-visible, coherent memory, mapped for its whole lifetime.</summary>
internal sealed unsafe class VulkanBuffer : IDisposable
{
    readonly Vk _api;
    readonly Device _device;

    public VulkanBuffer(VulkanContext vk, ulong size, BufferUsageFlags usage)
    {
        _api = vk.Api;
        _device = vk.Device;
        Size = Math.Max(4, size);
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = Size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        _api.CreateBuffer(_device, in info, null, out var buffer).ThrowOnError();
        Handle = buffer;

        _api.GetBufferMemoryRequirements(_device, buffer, out var requirements);
        var allocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = VulkanMemory.FindMemoryType(_api, vk.PhysicalDevice, requirements.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
        };
        _api.AllocateMemory(_device, in allocate, null, out var memory).ThrowOnError();
        Memory = memory;
        _api.BindBufferMemory(_device, buffer, memory, 0).ThrowOnError();

        void* mapped;
        _api.MapMemory(_device, memory, 0, Size, 0, &mapped).ThrowOnError();
        Mapped = mapped;
    }

    public Silk.NET.Vulkan.Buffer Handle { get; }
    public DeviceMemory Memory { get; }
    public ulong Size { get; }
    public void* Mapped { get; }

    public void Write<T>(ReadOnlySpan<T> data) where T : unmanaged =>
        data.CopyTo(new Span<T>(Mapped, (int)(Size / (ulong)sizeof(T))));

    public void Dispose()
    {
        _api.UnmapMemory(_device, Memory);
        _api.DestroyBuffer(_device, Handle, null);
        _api.FreeMemory(_device, Memory, null);
    }
}
