// Device selection and interop requirements adapted from the Avalonia GpuInterop sample
// (MIT License, Copyright (c) AvaloniaUI OÜ).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace OpenDelulu.Rendering.Vulkan;

/// <summary>
/// A Vulkan device on the same GPU as Avalonia's compositor, with the extensions needed to share
/// rendered images with it (D3D11 keyed-mutex textures or opaque Win32 handles on Windows,
/// opaque file descriptors elsewhere).
/// </summary>
internal sealed unsafe class VulkanContext : IDisposable
{
    public required Vk Api { get; init; }
    public required Instance Instance { get; init; }
    public required PhysicalDevice PhysicalDevice { get; init; }
    public required Device Device { get; init; }
    public required Queue Queue { get; init; }
    public required uint QueueFamilyIndex { get; init; }
    public required VulkanCommandBufferPool Pool { get; init; }
    /// <summary>Set on Windows when the compositor can only import D3D11 textures.</summary>
    public required ComPtr<ID3D11Device> D3DDevice { get; init; }
    public required string DeviceName { get; init; }
    public required string ShareMode { get; init; }

    public static (VulkanContext? Context, string Info) TryCreate(ICompositionGpuInterop interop)
    {
        Vk api;
        try
        {
            api = Vk.GetApi();
        }
        catch (Exception ex)
        {
            return (null, "Không tải được Vulkan: " + ex.Message);
        }

        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var images = interop.SupportedImageHandleTypes;
        var semaphores = interop.SupportedSemaphoreTypes;
        var deviceExtensions = new List<string> { "VK_KHR_external_memory", "VK_KHR_external_semaphore" };
        string shareMode;
        if (windows)
        {
            bool d3d = images.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle);
            bool opaque = images.Contains(KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle);
            if (!d3d && !opaque)
                return (null, $"Compositor không nhận ảnh ngoài ({string.Join(", ", images)})");
            deviceExtensions.AddRange([KhrExternalMemoryWin32.ExtensionName, KhrExternalSemaphoreWin32.ExtensionName,
                "VK_KHR_dedicated_allocation", "VK_KHR_get_memory_requirements2"]);
            shareMode = opaque ? "Vulkan NT handle" : "D3D11 shared texture";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return (null, "Vulkan trên macOS/iOS cần MoltenVK, chưa hỗ trợ");
        }
        else
        {
            if (!images.Contains(KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor)
                || !semaphores.Contains(KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaquePosixFileDescriptor))
                return (null, $"Compositor không nhận ảnh Vulkan ({string.Join(", ", images)})");
            deviceExtensions.AddRange([KhrExternalMemoryFd.ExtensionName, KhrExternalSemaphoreFd.ExtensionName]);
            shareMode = "Vulkan file descriptor";
        }

        using var appName = new ByteString("Open Delulu");
        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = appName,
            PEngineName = appName,
            ApiVersion = new Version32(1, 1, 0),
            ApplicationVersion = new Version32(0, 1, 0),
            EngineVersion = new Version32(0, 1, 0),
        };
        using var instanceExtensions = new ByteStringList([
            "VK_KHR_get_physical_device_properties2",
            "VK_KHR_external_memory_capabilities",
            "VK_KHR_external_semaphore_capabilities",
        ]);
        var instanceInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = instanceExtensions.Count,
            PpEnabledExtensionNames = instanceExtensions,
        };

        Instance instance = default;
        Device device = default;
        VulkanCommandBufferPool? pool = null;
        try
        {
            api.CreateInstance(in instanceInfo, null, out instance).ThrowOnError();

            uint count = 0;
            api.EnumeratePhysicalDevices(instance, ref count, null).ThrowOnError();
            var physicalDevices = new PhysicalDevice[count];
            fixed (PhysicalDevice* p = physicalDevices)
                api.EnumeratePhysicalDevices(instance, ref count, p).ThrowOnError();

            foreach (var physicalDevice in physicalDevices)
            {
                if (deviceExtensions.Any(ext => !api.IsDeviceExtensionPresent(physicalDevice, ext)))
                    continue;

                // Must be the GPU Avalonia composes on, or the shared image cannot be imported.
                var ids = new PhysicalDeviceIDProperties { SType = StructureType.PhysicalDeviceIDProperties };
                var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &ids };
                api.GetPhysicalDeviceProperties2(physicalDevice, &properties);
                if (interop.DeviceLuid is { } luid && ids.DeviceLuidvalid)
                {
                    if (!new Span<byte>(ids.DeviceLuid, 8).SequenceEqual(luid))
                        continue;
                }
                else if (interop.DeviceUuid is { } uuid && !new Span<byte>(ids.DeviceUuid, 16).SequenceEqual(uuid))
                {
                    continue;
                }

                uint familyCount = 0;
                api.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, ref familyCount, null);
                var families = new QueueFamilyProperties[familyCount];
                fixed (QueueFamilyProperties* f = families)
                    api.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, ref familyCount, f);
                int family = Array.FindIndex(families, f => f.QueueFlags.HasFlag(QueueFlags.GraphicsBit));
                if (family < 0)
                    continue;

                float priority = 1f;
                var queueInfo = new DeviceQueueCreateInfo
                {
                    SType = StructureType.DeviceQueueCreateInfo,
                    QueueFamilyIndex = (uint)family,
                    QueueCount = 1,
                    PQueuePriorities = &priority,
                };
                var features = new PhysicalDeviceFeatures();
                using var extensions = new ByteStringList(deviceExtensions);
                var deviceInfo = new DeviceCreateInfo
                {
                    SType = StructureType.DeviceCreateInfo,
                    QueueCreateInfoCount = 1,
                    PQueueCreateInfos = &queueInfo,
                    EnabledExtensionCount = extensions.Count,
                    PpEnabledExtensionNames = extensions,
                    PEnabledFeatures = &features,
                };
                api.CreateDevice(physicalDevice, in deviceInfo, null, out device).ThrowOnError();
                api.GetDeviceQueue(device, (uint)family, 0, out var queue);
                pool = new VulkanCommandBufferPool(api, device, queue, (uint)family);

                ComPtr<ID3D11Device> d3d = default;
                if (windows && ids.DeviceLuidvalid
                    && !images.Contains(KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle))
                    d3d = D3DMemoryHelper.CreateDeviceByLuid(MemoryMarshal.Read<Silk.NET.Core.Native.Luid>(new Span<byte>(ids.DeviceLuid, 8)));

                string name = Marshal.PtrToStringAnsi((IntPtr)properties.Properties.DeviceName) ?? "GPU";
                var context = new VulkanContext
                {
                    Api = api,
                    Instance = instance,
                    PhysicalDevice = physicalDevice,
                    Device = device,
                    Queue = queue,
                    QueueFamilyIndex = (uint)family,
                    Pool = pool,
                    D3DDevice = d3d,
                    DeviceName = name,
                    ShareMode = shareMode,
                };
                return (context, $"{name}, {shareMode}");
            }

            api.DestroyInstance(instance, null);
            return (null, "Không tìm thấy GPU Vulkan phù hợp với compositor");
        }
        catch (Exception ex)
        {
            pool?.Dispose();
            if (device.Handle != 0)
                api.DestroyDevice(device, null);
            if (instance.Handle != 0)
                api.DestroyInstance(instance, null);
            return (null, ex.Message);
        }
    }

    public void Dispose()
    {
        Api.DeviceWaitIdle(Device);
        Pool.Dispose();
        D3DDevice.Dispose();
        Api.DestroyDevice(Device, null);
        Api.DestroyInstance(Instance, null);
    }
}
