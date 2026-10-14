// Adapted from the Avalonia GpuInterop sample (MIT License, Copyright (c) AvaloniaUI OÜ).
using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace OpenDelulu.Rendering.Vulkan;

/// <summary>
/// One-shot command buffers with a fence each. Submitted buffers are kept until
/// <see cref="FreeUsedCommandBuffers"/>, which waits for their fences: calling it at the start of a frame
/// guarantees the GPU finished the previous frame, so host-visible buffers can be rewritten safely.
/// </summary>
internal sealed unsafe class VulkanCommandBufferPool : IDisposable
{
    readonly Vk _api;
    readonly Device _device;
    readonly Queue _queue;
    readonly CommandPool _commandPool;
    readonly List<VulkanCommandBuffer> _used = [];
    readonly object _lock = new();

    public VulkanCommandBufferPool(Vk api, Device device, Queue queue, uint queueFamilyIndex)
    {
        _api = api;
        _device = device;
        _queue = queue;
        var info = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = queueFamilyIndex,
        };
        _api.CreateCommandPool(_device, in info, null, out _commandPool).ThrowOnError();
    }

    public VulkanCommandBuffer CreateCommandBuffer() => new(this);

    public void FreeUsedCommandBuffers()
    {
        lock (_lock)
        {
            foreach (var buffer in _used)
                buffer.Release();
            _used.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            FreeUsedCommandBuffers();
            _api.DestroyCommandPool(_device, _commandPool, null);
        }
    }

    public sealed class VulkanCommandBuffer
    {
        readonly VulkanCommandBufferPool _pool;
        readonly Fence _fence;
        bool _started, _ended;

        internal VulkanCommandBuffer(VulkanCommandBufferPool pool)
        {
            _pool = pool;
            var allocate = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = pool._commandPool,
                CommandBufferCount = 1,
                Level = CommandBufferLevel.Primary,
            };
            lock (pool._lock)
            {
                pool._api.AllocateCommandBuffers(pool._device, in allocate, out var handle).ThrowOnError();
                Handle = handle;
            }
            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
            pool._api.CreateFence(pool._device, in fenceInfo, null, out _fence).ThrowOnError();
        }

        public CommandBuffer Handle { get; }

        public void BeginRecording()
        {
            if (_started)
                return;
            _started = true;
            var begin = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            _pool._api.BeginCommandBuffer(Handle, in begin).ThrowOnError();
        }

        public void EndRecording()
        {
            if (_started && !_ended)
            {
                _ended = true;
                _pool._api.EndCommandBuffer(Handle).ThrowOnError();
            }
        }

        public sealed class KeyedMutexSubmitInfo
        {
            public ulong? AcquireKey { get; init; }
            public ulong? ReleaseKey { get; init; }
            public DeviceMemory DeviceMemory { get; init; }
        }

        public void Submit(
            ReadOnlySpan<Semaphore> waitSemaphores = default,
            ReadOnlySpan<PipelineStageFlags> waitStages = default,
            ReadOnlySpan<Semaphore> signalSemaphores = default,
            KeyedMutexSubmitInfo? keyedMutex = null)
        {
            EndRecording();
            var api = _pool._api;

            ulong acquireKey = keyedMutex?.AcquireKey ?? 0, releaseKey = keyedMutex?.ReleaseKey ?? 0;
            var memory = keyedMutex?.DeviceMemory ?? default;
            uint timeout = uint.MaxValue;
            Win32KeyedMutexAcquireReleaseInfoKHR mutex = default;
            if (keyedMutex is not null)
            {
                mutex = new Win32KeyedMutexAcquireReleaseInfoKHR
                {
                    SType = StructureType.Win32KeyedMutexAcquireReleaseInfoKhr,
                    AcquireCount = keyedMutex.AcquireKey.HasValue ? 1u : 0u,
                    ReleaseCount = keyedMutex.ReleaseKey.HasValue ? 1u : 0u,
                    PAcquireKeys = &acquireKey,
                    PReleaseKeys = &releaseKey,
                    PAcquireSyncs = &memory,
                    PReleaseSyncs = &memory,
                    PAcquireTimeouts = &timeout,
                };
            }

            fixed (Semaphore* pWait = waitSemaphores, pSignal = signalSemaphores)
            fixed (PipelineStageFlags* pStages = waitStages)
            {
                var handle = Handle;
                var submit = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    PNext = keyedMutex is not null ? &mutex : null,
                    WaitSemaphoreCount = (uint)waitSemaphores.Length,
                    PWaitSemaphores = pWait,
                    PWaitDstStageMask = pStages,
                    CommandBufferCount = 1,
                    PCommandBuffers = &handle,
                    SignalSemaphoreCount = (uint)signalSemaphores.Length,
                    PSignalSemaphores = pSignal,
                };
                var fence = _fence;
                api.ResetFences(_pool._device, 1, in fence).ThrowOnError();
                api.QueueSubmit(_pool._queue, 1, in submit, fence).ThrowOnError();
            }

            lock (_pool._lock)
                _pool._used.Add(this);
        }

        /// <summary>Waits for completion and frees the buffer.</summary>
        internal void Release()
        {
            var api = _pool._api;
            var fence = _fence;
            api.WaitForFences(_pool._device, 1, in fence, true, ulong.MaxValue);
            var handle = Handle;
            api.FreeCommandBuffers(_pool._device, _pool._commandPool, 1, in handle);
            api.DestroyFence(_pool._device, _fence, null);
        }
    }
}
