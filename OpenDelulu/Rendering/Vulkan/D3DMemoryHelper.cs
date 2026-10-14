// Adapted from the Avalonia GpuInterop sample (MIT License, Copyright (c) AvaloniaUI OÜ).
using System;
using Avalonia;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using static Silk.NET.Core.Native.SilkMarshal;

namespace OpenDelulu.Rendering.Vulkan;

/// <summary>
/// Windows/ANGLE path: Avalonia composes with Direct3D 11 there, so frames are rendered by Vulkan into
/// D3D11 textures created as shared keyed-mutex resources on the same adapter.
/// </summary>
internal static class D3DMemoryHelper
{
    const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    public static unsafe ComPtr<ID3D11Device> CreateDeviceByLuid(Luid luid)
    {
        using var dxgi = new DXGI(DXGI.CreateDefaultContext(["DXGI.dll"]));
        using var d3d11 = new D3D11(D3D11.CreateDefaultContext(["d3d11.dll"]));
        using var factory = dxgi.CreateDXGIFactory1<IDXGIFactory1>();
        using var adapter = GetAdapterByLuid(factory, luid);

        var levels = stackalloc D3DFeatureLevel[]
        {
            D3DFeatureLevel.Level111, D3DFeatureLevel.Level110, D3DFeatureLevel.Level101, D3DFeatureLevel.Level100,
        };
        ComPtr<ID3D11Device> device = default;
        ComPtr<ID3D11DeviceContext> context = default;
        D3DFeatureLevel actual;
        ThrowHResult(d3d11.CreateDevice(adapter, D3DDriverType.Unknown, IntPtr.Zero, 0u, levels, 4,
            D3D11.SdkVersion, device.GetAddressOf(), &actual, context.GetAddressOf()));
        context.Dispose();
        return device;
    }

    static unsafe ComPtr<IDXGIAdapter> GetAdapterByLuid(ComPtr<IDXGIFactory1> factory, Luid luid)
    {
        uint index = 0;
        ComPtr<IDXGIAdapter> adapter = default;
        while (factory.EnumAdapters(index, adapter.GetAddressOf()) != DxgiErrorNotFound)
        {
            AdapterDesc desc;
            if (adapter.GetDesc(&desc) == 0 && desc.AdapterLuid.Low == luid.Low && desc.AdapterLuid.High == luid.High)
                return adapter;
            adapter.Dispose();
            index++;
        }
        throw new ArgumentException("No DXGI adapter matches the Vulkan device LUID.");
    }

    public static unsafe ComPtr<ID3D11Texture2D> CreateSharedTexture(ComPtr<ID3D11Device> device, PixelSize size)
    {
        ComPtr<ID3D11Texture2D> texture = default;
        var desc = new Texture2DDesc
        {
            Format = Format.FormatR8G8B8A8Unorm,
            Width = (uint)size.Width,
            Height = (uint)size.Height,
            ArraySize = 1,
            MipLevels = 1,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource),
            MiscFlags = (uint)(ResourceMiscFlag.SharedKeyedmutex | ResourceMiscFlag.SharedNthandle),
        };
        ThrowHResult(device.CreateTexture2D(&desc, (SubresourceData*)null, texture.GetAddressOf()));
        return texture;
    }

    public static unsafe IntPtr CreateSharedHandle(ComPtr<ID3D11Texture2D> texture)
    {
        using var resource = texture.QueryInterface<IDXGIResource1>();
        void* handle;
        ThrowHResult(resource.CreateSharedHandle((SecurityAttributes*)null,
            DXGI.SharedResourceRead | DXGI.SharedResourceWrite, (char*)null, &handle));
        return (IntPtr)handle;
    }
}
