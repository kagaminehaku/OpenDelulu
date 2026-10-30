using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Engine.Stages;
using OpenDelulu.Formats.Imaging;
using Silk.NET.Vulkan;

namespace OpenDelulu.Rendering.Vulkan;

/// <summary>
/// Draws a <see cref="ModelScene"/> with Vulkan into an offscreen color target, then copies it into the
/// shared swapchain image. Same two-pass scheme as the OpenGL ES backend: opaque texels with depth writes,
/// then the soft edges of alpha-textured meshes blended without depth writes.
/// </summary>
internal sealed unsafe class VulkanSceneRenderer : IDisposable
{
    const Format ColorFormat = Format.R8G8B8A8Unorm;
    /// <summary>The glare images: half floats, so glare meshes adding up keep their excess (the exe's float target).</summary>
    const Format GlareFormat = Format.R16G16B16A16Sfloat;

    [StructLayout(LayoutKind.Sequential)]
    struct PushConstants
    {
        public Matrix4x4 Mvp;
        public Vector4 Diffuse;
        public Vector4 LightAndPass;
        public Vector4 Ambient;
        public Vector4 Emissive; // 128 bytes in total: the guaranteed push-constant minimum
    }

    sealed class GpuTexture
    {
        public Image Image;
        public DeviceMemory Memory;
        public ImageView View;
    }

    sealed class GpuMesh(VulkanBuffer vertices, VulkanBuffer indices, int indexCount, int texture, Vector4 diffuse, Vector4 ambient, Vector4 emissive, bool hasAlpha, BlendMode blend)
    {
        /// <summary>Lit vertex colours (binding 1, RGBA floats; A = 0 means "use the simple lighting").</summary>
        public VulkanBuffer? Colors { get; set; }
        public int ColorVersion { get; set; } = -1;
        /// <summary>Second texture stage data (binding 2: u, v, blend, 1), or null to read the shared zeros.</summary>
        public VulkanBuffer? Layers { get; set; }
        public int LayerVersion { get; set; } = -1;
        /// <summary>The texture and the layer texture (bindings 0 and 1).</summary>
        public DescriptorSet Set { get; set; }
        public Vector4 Ambient { get; } = ambient;
        public Vector4 Emissive { get; } = emissive;
        public VulkanBuffer Vertices { get; } = vertices;
        public VulkanBuffer Indices { get; } = indices;
        public int IndexCount { get; } = indexCount;
        public int Texture { get; } = texture;
        public Vector4 Diffuse { get; } = diffuse;
        public bool HasAlpha { get; } = hasAlpha;
        public BlendMode Blend { get; } = blend;
    }

    readonly VulkanContext _vk;
    readonly Format _depthFormat;
    readonly ShaderModule _vertex, _fragment;
    readonly DescriptorSetLayout _setLayout;
    readonly PipelineLayout _pipelineLayout;
    readonly RenderPass _renderPass, _glareSourcePass;
    readonly Pipeline _opaque, _translucent, _additive, _darken, _glareSource, _overlay;
    readonly Sampler _sampler;
    readonly VulkanGlare _glare;

    PixelSize _size;
    Image _color, _depth, _glareImage;
    DeviceMemory _colorMemory, _depthMemory, _glareMemory;
    ImageView _colorView, _depthView, _glareView;
    Framebuffer _framebuffer, _glareFramebuffer;

    ModelScene? _scene;
    readonly List<GpuMesh> _meshes = [];
    readonly List<GpuTexture> _textures = [];
    GpuTexture? _white;
    VulkanBuffer? _noLayers;
    DescriptorPool _descriptorPool;

    Vk Api => _vk.Api;
    Device Device => _vk.Device;

    public VulkanSceneRenderer(VulkanContext vk)
    {
        _vk = vk;
        _depthFormat = PickDepthFormat();
        _vertex = CreateShader("model.vert.spv");
        _fragment = CreateShader("model.frag.spv");

        // Binding 0: the material texture; 1: its second texture stage (white when unused).
        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        for (uint b = 0; b < 2; b++)
            bindings[b] = new DescriptorSetLayoutBinding
            {
                Binding = b,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
        var setLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        Api.CreateDescriptorSetLayout(Device, in setLayoutInfo, null, out _setLayout).ThrowOnError();

        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = (uint)sizeof(PushConstants),
        };
        var setLayout = _setLayout;
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        Api.CreatePipelineLayout(Device, in layoutInfo, null, out _pipelineLayout).ThrowOnError();

        _renderPass = CreateRenderPass();
        _glareSourcePass = CreateGlareSourcePass();
        _opaque = CreatePipeline(translucent: false);
        _translucent = CreatePipeline(translucent: true);
        _additive = CreatePipeline(translucent: true, BlendFactor.SrcAlpha, BlendFactor.One);
        _darken = CreatePipeline(translucent: true, BlendFactor.Zero, BlendFactor.OneMinusSrcColor);
        // Markers drawn over everything (the camera target).
        _overlay = CreatePipeline(translucent: true, depthTest: false);
        // Glare meshes, premultiplied by the shader, added up behind the scene's depth.
        _glareSource = CreatePipeline(translucent: true, BlendFactor.One, BlendFactor.One, _glareSourcePass);
        _glare = new VulkanGlare(vk, GlareFormat, ColorFormat, CreateShader);

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            MaxLod = Vk.LodClampNone,
        };
        Api.CreateSampler(Device, in samplerInfo, null, out _sampler).ThrowOnError();
    }

    /// <summary>Renders one frame into <paramref name="target"/>. Returns true while the scene keeps moving.</summary>
    public bool Render(VulkanSharedImage target, ModelScene? scene, OrbitCamera camera, double deltaSeconds) =>
        Render(target, target.Size, scene, camera, deltaSeconds);

    /// <summary>Renders into the renderer's own image; with a <paramref name="target"/>, copies it there.</summary>
    public bool Render(VulkanSharedImage? target, PixelSize size, ModelScene? scene, OrbitCamera camera, double deltaSeconds)
    {
        if (size != _size)
            CreateTargets(size);

        if (!ReferenceEquals(scene, _scene))
        {
            ReleaseScene();
            if (scene is not null)
                UploadScene(scene);
            _scene = scene;
        }

        // No Y flip: Vulkan's clip space points down, but the compositor samples the shared image with its
        // origin at the bottom (verified on the D3D11 and Vulkan sharing paths), and the two cancel out.
        var camera3 = scene is null ? default : camera.Compute(scene.Subject, _size.Width / (float)_size.Height, flipY: false);

        // The swapchain waited for the previous frame, so the mapped vertex buffers are free to rewrite.
        bool animating = false;
        if (scene is not null)
        {
            scene.SetLookTarget(camera3.Eye, camera.Target(scene.Subject));
            bool moved = scene.Update(deltaSeconds);
            bool effects = scene.UpdateLighting(camera.CameraToWorld(scene.Subject), moved);
            if (moved || effects)
                for (int i = 0; i < _meshes.Count; i++)
                    if (scene.IsVisible(i) && scene.IsDynamic(i))
                        _meshes[i].Vertices.Write<float>(scene.Meshes[i].Vertices);
            UploadColors(scene);
            UploadTextures(scene);
            animating = scene.IsAnimating;
        }

        var commands = _vk.Pool.CreateCommandBuffer();
        commands.BeginRecording();
        var cmd = commands.Handle;

        var clear = stackalloc ClearValue[2];
        clear[0] = new ClearValue { Color = new ClearColorValue(0.11f, 0.12f, 0.15f, 1f) };
        clear[1] = new ClearValue { DepthStencil = new ClearDepthStencilValue(1f, 0) };
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffer,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)_size.Width, (uint)_size.Height)),
            ClearValueCount = 2,
            PClearValues = clear,
        };
        Api.CmdBeginRenderPass(cmd, in begin, SubpassContents.Inline);

        var viewport = new Viewport(0, 0, _size.Width, _size.Height, 0, 1);
        Api.CmdSetViewport(cmd, 0, 1, in viewport);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)_size.Width, (uint)_size.Height));
        Api.CmdSetScissor(cmd, 0, 1, in scissor);

        if (scene is not null && _meshes.Count > 0)
        {
            DrawPass(cmd, scene, _opaque, camera3.ViewProjection, camera3.LightDirection, translucent: false);
            DrawPass(cmd, scene, _translucent, camera3.ViewProjection, camera3.LightDirection, translucent: true);
            DrawPass(cmd, scene, _overlay, camera3.ViewProjection, camera3.LightDirection, translucent: true, overlay: true);
        }
        Api.CmdEndRenderPass(cmd);

        if (scene?.Glare is { } glare && _meshes.Count > 0 && HasGlareSource(scene))
        {
            DrawGlareSource(cmd, scene, glare, camera3.ViewProjection, camera3.LightDirection);
            // Like the exe: the star turns by the camera's right vector x (_11 of the inverse view), in radians.
            _glare.StarInclination = camera.CameraToWorld(scene.Subject).M11;
            _glare.Apply(cmd, glare);
        }

        // Offscreen color is now TRANSFER_SRC (render pass final layout); copy it into the shared image.
        if (target is not null)
        {
            target.TransitionLayout(cmd, ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit);
            var layers = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1);
            var region = new ImageCopy(layers, new Offset3D(0, 0, 0), layers, new Offset3D(0, 0, 0),
                new Extent3D((uint)_size.Width, (uint)_size.Height, 1));
            Api.CmdCopyImage(cmd, _color, ImageLayout.TransferSrcOptimal, target.Image, ImageLayout.TransferDstOptimal, 1, in region);
        }
        commands.Submit();
        return animating;
    }

    /// <summary>
    /// The last rendered frame as RGBA rows from the top (waits for the GPU). The image is stored bottom-up for the
    /// compositor (see <see cref="Render(VulkanSharedImage?, PixelSize, ModelScene?, OrbitCamera, double)"/>), so rows
    /// are flipped here. For headless rendering tests.
    /// </summary>
    public byte[] ReadPixels()
    {
        int width = _size.Width, height = _size.Height;
        using var buffer = new VulkanBuffer(_vk, (ulong)(width * height * 4), BufferUsageFlags.TransferDstBit);
        var commands = _vk.Pool.CreateCommandBuffer();
        commands.BeginRecording();
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D((uint)width, (uint)height, 1),
        };
        Api.CmdCopyImageToBuffer(commands.Handle, _color, ImageLayout.TransferSrcOptimal, buffer.Handle, 1, in copy);
        commands.Submit();
        _vk.Pool.FreeUsedCommandBuffers();
        var pixels = new byte[width * height * 4];
        var source = new ReadOnlySpan<byte>(buffer.Mapped, pixels.Length);
        for (int y = 0; y < height; y++)
            source.Slice((height - 1 - y) * width * 4, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
        return pixels;
    }

    /// <param name="overlay">Draws only the meshes shown over everything (<see cref="ModelScene.IsOverlay"/>), which
    /// the other passes skip.</param>
    void DrawPass(CommandBuffer cmd, ModelScene scene, Pipeline pipeline, Matrix4x4 viewProjection, Vector3 light, bool translucent,
        bool overlay = false)
    {
        var bound = pipeline;
        Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
        var push = new PushConstants
        {
            Mvp = viewProjection,
            LightAndPass = new Vector4(light, translucent ? 1f : 0f),
        };
        for (int i = 0; i < _meshes.Count; i++)
        {
            var mesh = _meshes[i];
            if (!scene.IsVisible(i) || scene.IsOverlay(i) != overlay)
                continue;
            float opacity = scene.Opacity(i);
            if (overlay)
                push.LightAndPass.W = 2f;
            else if (translucent ? !mesh.HasAlpha && opacity >= 1 : mesh.Blend != BlendMode.Normal || opacity < 1)
                continue;
            if (translucent && !overlay)
            {
                // Pass value 1: only the edges left by pass 1; 2: every texel (effects skipped by pass 1).
                var wanted = mesh.Blend switch { BlendMode.Additive => _additive, BlendMode.Darken => _darken, _ => pipeline };
                if (wanted.Handle != bound.Handle)
                {
                    Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, wanted);
                    bound = wanted;
                }
                // Pass value 1 keeps only what pass 0 left; a faded mesh was skipped there, so all of it.
                push.LightAndPass.W = mesh.Blend == BlendMode.Normal && opacity >= 1 ? 1f : 2f;
            }

            var set = mesh.Set;
            Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &set, 0, null);
            var tint = new Vector4(scene.Light(i), opacity);
            push.Diffuse = mesh.Diffuse * tint;
            push.Ambient = mesh.Ambient * tint;
            push.Emissive = mesh.Emissive * tint;
            SetWater(ref push, scene, i);
            Api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0, (uint)sizeof(PushConstants), &push);
            var buffers = stackalloc Silk.NET.Vulkan.Buffer[3] { mesh.Vertices.Handle, mesh.Colors!.Handle, (mesh.Layers ?? _noLayers!).Handle };
            var offsets = stackalloc ulong[3];
            Api.CmdBindVertexBuffers(cmd, 0, 3, buffers, offsets);
            Api.CmdBindIndexBuffer(cmd, mesh.Indices.Handle, 0, IndexType.Uint16);
            Api.CmdDrawIndexed(cmd, (uint)mesh.IndexCount, 1, 0, 0, 0);
        }
    }

    /// <summary>Packs the water shader into the free w of ambient (10 x wave + scale) and emissive (angle).</summary>
    static void SetWater(ref PushConstants push, ModelScene scene, int mesh)
    {
        var water = scene.Water(mesh);
        push.Ambient.W = water is { } w ? MathF.Round(w.Wave * 10) + w.Scale : 0;
        push.Emissive.W = water?.Angle ?? 0;
    }

    bool HasGlareSource(ModelScene scene)
    {
        for (int i = 0; i < _meshes.Count; i++)
            if (scene.IsGlareSource(i))
                return true;
        return false;
    }

    /// <summary>
    /// The glare source: the glare meshes again, in their glare colour, hidden by the depth the scene pass left (the
    /// exe redraws the whole scene into its glare target for that, with the glare meshes lifted by +100).
    /// </summary>
    void DrawGlareSource(CommandBuffer cmd, ModelScene scene, StageGlare glare, Matrix4x4 viewProjection, Vector3 light)
    {
        var clear = stackalloc ClearValue[2];
        clear[0] = new ClearValue { Color = new ClearColorValue(0f, 0f, 0f, 1f) };
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _glareSourcePass,
            Framebuffer = _glareFramebuffer,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)_size.Width, (uint)_size.Height)),
            ClearValueCount = 2,
            PClearValues = clear,
        };
        Api.CmdBeginRenderPass(cmd, in begin, SubpassContents.Inline);
        var viewport = new Viewport(0, 0, _size.Width, _size.Height, 0, 1);
        Api.CmdSetViewport(cmd, 0, 1, in viewport);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)_size.Width, (uint)_size.Height));
        Api.CmdSetScissor(cmd, 0, 1, in scissor);
        Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _glareSource);
        var push = new PushConstants
        {
            Mvp = viewProjection,
            LightAndPass = new Vector4(light, 3 + (int)glare.Color),
        };
        for (int i = 0; i < _meshes.Count; i++)
        {
            if (!scene.IsGlareSource(i))
                continue;
            var mesh = _meshes[i];
            var set = mesh.Set;
            Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &set, 0, null);
            var tint = new Vector4(scene.Light(i), 1);
            push.Diffuse = mesh.Diffuse * tint;
            push.Ambient = mesh.Ambient * tint;
            push.Emissive = mesh.Emissive * tint;
            SetWater(ref push, scene, i);
            Api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0, (uint)sizeof(PushConstants), &push);
            var buffers = stackalloc Silk.NET.Vulkan.Buffer[3] { mesh.Vertices.Handle, mesh.Colors!.Handle, (mesh.Layers ?? _noLayers!).Handle };
            var offsets = stackalloc ulong[3];
            Api.CmdBindVertexBuffers(cmd, 0, 3, buffers, offsets);
            Api.CmdBindIndexBuffer(cmd, mesh.Indices.Handle, 0, IndexType.Uint16);
            Api.CmdDrawIndexed(cmd, (uint)mesh.IndexCount, 1, 0, 0, 0);
        }
        Api.CmdEndRenderPass(cmd);
    }

    // ------------------------------------------------------------------ scene resources

    void UploadScene(ModelScene scene)
    {
        // One descriptor set per (texture, layer texture) pair in use.
        var pairs = Enumerable.Range(0, scene.Meshes.Count).Select(m => (scene.TextureIndex(m), scene.LayerTextureIndex(m))).Distinct().ToList();
        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, (uint)Math.Max(2, pairs.Count * 2));
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = (uint)Math.Max(1, pairs.Count),
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        Api.CreateDescriptorPool(Device, in poolInfo, null, out _descriptorPool).ThrowOnError();

        // All texture uploads go into one command buffer; staging buffers live until it completes.
        var staging = new List<VulkanBuffer>();
        var commands = _vk.Pool.CreateCommandBuffer();
        commands.BeginRecording();
        _white = CreateTexture(commands.Handle, new RgbaImage(1, 1, [255, 255, 255, 255]), staging);
        foreach (var image in scene.Textures)
            _textures.Add(image is null ? _white : CreateTexture(commands.Handle, image, staging));
        commands.Submit();
        Api.QueueWaitIdle(_vk.Queue).ThrowOnError();
        foreach (var buffer in staging)
            buffer.Dispose();
        var sets = pairs.ToDictionary(pair => pair, pair => CreateSet(
            pair.Item1 >= 0 ? _textures[pair.Item1] : _white!, pair.Item2 >= 0 ? _textures[pair.Item2] : _white!));

        int maxVertices = 1;
        for (int m = 0; m < scene.Meshes.Count; m++)
        {
            var mesh = scene.Meshes[m];
            var vertices = new VulkanBuffer(_vk, (ulong)(mesh.Vertices.Length * sizeof(float)), BufferUsageFlags.VertexBufferBit);
            vertices.Write<float>(mesh.Vertices);
            var indices = new VulkanBuffer(_vk, (ulong)(mesh.Indices.Length * sizeof(ushort)), BufferUsageFlags.IndexBufferBit);
            indices.Write<ushort>(mesh.Indices);
            var diffuse = mesh.Diffuse;
            if (diffuse.X + diffuse.Y + diffuse.Z < 0.05f)
                diffuse = new Vector4(1, 1, 1, diffuse.W); // some materials only use ambient/emissive; keep their opacity
            var gpu = new GpuMesh(vertices, indices, mesh.Indices.Length, scene.TextureIndex(m), diffuse, mesh.Ambient, mesh.Emissive, mesh.HasAlpha, mesh.Blend)
            {
                // Zeroed: unlit until the scene provides colours.
                Colors = new VulkanBuffer(_vk, (ulong)(mesh.Vertices.Length / RenderModel.VertexStride * 4 * sizeof(float)), BufferUsageFlags.VertexBufferBit),
            };
            new Span<byte>(gpu.Colors.Mapped, (int)gpu.Colors.Size).Clear();
            gpu.Set = sets[(scene.TextureIndex(m), scene.LayerTextureIndex(m))];
            int vertexCount = mesh.Vertices.Length / RenderModel.VertexStride;
            maxVertices = Math.Max(maxVertices, vertexCount);
            if (mesh.Layer is not null)
            {
                gpu.Layers = new VulkanBuffer(_vk, (ulong)(vertexCount * 4 * sizeof(float)), BufferUsageFlags.VertexBufferBit);
                new Span<byte>(gpu.Layers.Mapped, (int)gpu.Layers.Size).Clear();
            }
            _meshes.Add(gpu);
        }
        // Meshes without a second stage read zeros (w = 0: unused).
        _noLayers = new VulkanBuffer(_vk, (ulong)(maxVertices * 4 * sizeof(float)), BufferUsageFlags.VertexBufferBit);
        new Span<byte>(_noLayers.Mapped, (int)_noLayers.Size).Clear();
    }

    /// <summary>Uploads the vertex colours the scene recomputed (Direct3D 9 fixed-function lighting).</summary>
    void UploadColors(ModelScene scene)
    {
        for (int i = 0; i < _meshes.Count; i++)
        {
            var gpu = _meshes[i];
            int version = scene.ColorVersion(i);
            if (gpu.ColorVersion == version || gpu.Colors is not { } buffer)
                continue;
            gpu.ColorVersion = version;
            if (scene.Colors(i) is { } colors)
                buffer.Write<float>(colors);
            else
                new Span<byte>(buffer.Mapped, (int)buffer.Size).Clear();
        }
        for (int i = 0; i < _meshes.Count; i++)
        {
            var gpu = _meshes[i];
            int version = scene.LayerVersion(i);
            if (gpu.LayerVersion == version || gpu.Layers is not { } buffer || scene.LayerData(i) is not { } data)
                continue;
            gpu.LayerVersion = version;
            buffer.Write<float>(data);
        }
    }

    DescriptorSet CreateSet(GpuTexture texture, GpuTexture layer)
    {
        var setLayout = _setLayout;
        var allocate = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        Api.AllocateDescriptorSets(Device, in allocate, out var set).ThrowOnError();
        var images = stackalloc DescriptorImageInfo[2]
        {
            new(_sampler, texture.View, ImageLayout.ShaderReadOnlyOptimal),
            new(_sampler, layer.View, ImageLayout.ShaderReadOnlyOptimal),
        };
        var writes = stackalloc WriteDescriptorSet[2];
        for (int b = 0; b < 2; b++)
            writes[b] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = (uint)b,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &images[b],
            };
        Api.UpdateDescriptorSets(Device, 2, writes, 0, null);
        return set;
    }

    readonly Dictionary<int, int> _textureVersions = [];

    /// <summary>Re-uploads the textures the scene replaced (the swimsuit's wet stain) into their images.</summary>
    void UploadTextures(ModelScene scene)
    {
        List<VulkanBuffer>? staging = null;
        VulkanCommandBufferPool.VulkanCommandBuffer? commands = null;
        for (int i = 0; i < _textures.Count && i < scene.Textures.Count; i++)
        {
            int version = scene.TextureVersion(i);
            if (_textureVersions.GetValueOrDefault(i) == version || scene.Textures[i] is not { } image || ReferenceEquals(_textures[i], _white))
                continue;
            _textureVersions[i] = version;
            if (commands is null)
            {
                staging = [];
                Api.QueueWaitIdle(_vk.Queue).ThrowOnError();
                commands = _vk.Pool.CreateCommandBuffer();
                commands.BeginRecording();
            }
            FillTexture(commands.Handle, _textures[i], image, staging!, ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit);
        }
        if (commands is null)
            return;
        commands.Submit();
        Api.QueueWaitIdle(_vk.Queue).ThrowOnError();
        foreach (var buffer in staging!)
            buffer.Dispose();
    }

    GpuTexture CreateTexture(CommandBuffer cmd, RgbaImage image, List<VulkanBuffer> staging)
    {
        // Full mip chain, generated on the GPU by successive half-size blits (matches the GL backend's mipmaps).
        uint mipLevels = (uint)Math.Floor(Math.Log2(Math.Max(image.Width, image.Height))) + 1;
        var texture = new GpuTexture();
        (texture.Image, texture.Memory, texture.View) = CreateImage(image.Width, image.Height, ColorFormat,
            ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit, ImageAspectFlags.ColorBit, mipLevels);
        FillTexture(cmd, texture, image, staging, ImageLayout.Undefined, AccessFlags.None);
        return texture;
    }

    /// <summary>Copies the pixels into every mip level of the texture (from <paramref name="layout"/>), ready for sampling.</summary>
    void FillTexture(CommandBuffer cmd, GpuTexture texture, RgbaImage image, List<VulkanBuffer> staging, ImageLayout layout, AccessFlags access)
    {
        var upload = new VulkanBuffer(_vk, (ulong)image.Pixels.Length, BufferUsageFlags.TransferSrcBit);
        upload.Write<byte>(image.Pixels);
        staging.Add(upload);
        uint mipLevels = (uint)Math.Floor(Math.Log2(Math.Max(image.Width, image.Height))) + 1;

        VulkanMemory.TransitionLayout(Api, cmd, texture.Image, layout, access,
            ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, mipLevels: mipLevels);
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D((uint)image.Width, (uint)image.Height, 1),
        };
        Api.CmdCopyBufferToImage(cmd, upload.Handle, texture.Image, ImageLayout.TransferDstOptimal, 1, in copy);

        int width = image.Width, height = image.Height;
        for (uint level = 1; level < mipLevels; level++)
        {
            TransitionMip(cmd, texture.Image, level - 1, ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit,
                ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit);
            int nextWidth = Math.Max(1, width / 2), nextHeight = Math.Max(1, height / 2);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level - 1, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level, 0, 1),
            };
            blit.SrcOffsets[1] = new Offset3D(width, height, 1);
            blit.DstOffsets[1] = new Offset3D(nextWidth, nextHeight, 1);
            Api.CmdBlitImage(cmd, texture.Image, ImageLayout.TransferSrcOptimal, texture.Image, ImageLayout.TransferDstOptimal,
                1, in blit, Filter.Linear);
            TransitionMip(cmd, texture.Image, level - 1, ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit,
                ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit);
            width = nextWidth;
            height = nextHeight;
        }
        TransitionMip(cmd, texture.Image, mipLevels - 1, ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit,
            ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit);
    }

    void TransitionMip(CommandBuffer cmd, Image image, uint level, ImageLayout from, AccessFlags fromAccess, ImageLayout to, AccessFlags toAccess)
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
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, level, 1, 0, 1),
        };
        Api.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit | PipelineStageFlags.FragmentShaderBit,
            0, 0, null, 0, null, 1, in barrier);
    }

    void ReleaseScene()
    {
        if (_scene is null && _meshes.Count == 0 && _white is null)
            return;
        Api.DeviceWaitIdle(Device);
        foreach (var mesh in _meshes)
        {
            mesh.Vertices.Dispose();
            mesh.Indices.Dispose();
            mesh.Colors?.Dispose();
            mesh.Layers?.Dispose();
        }
        _noLayers?.Dispose();
        _noLayers = null;
        _meshes.Clear();
        var destroyed = new HashSet<GpuTexture>();
        foreach (var texture in _textures)
            if (destroyed.Add(texture))
                DestroyImage(texture.Image, texture.Memory, texture.View);
        if (_white is not null && destroyed.Add(_white))
            DestroyImage(_white.Image, _white.Memory, _white.View);
        _textures.Clear();
        _white = null;
        if (_descriptorPool.Handle != 0)
            Api.DestroyDescriptorPool(Device, _descriptorPool, null);
        _descriptorPool = default;
        _scene = null;
    }

    // ------------------------------------------------------------------ size-dependent targets

    void CreateTargets(PixelSize size)
    {
        DestroyTargets();
        _size = size;
        (_color, _colorMemory, _colorView) = CreateImage(size.Width, size.Height, ColorFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, ImageAspectFlags.ColorBit);
        (_depth, _depthMemory, _depthView) = CreateImage(size.Width, size.Height, _depthFormat,
            ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit);

        var attachments = stackalloc ImageView[] { _colorView, _depthView };
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _renderPass,
            AttachmentCount = 2,
            PAttachments = attachments,
            Width = (uint)size.Width,
            Height = (uint)size.Height,
            Layers = 1,
        };
        Api.CreateFramebuffer(Device, in info, null, out _framebuffer).ThrowOnError();

        (_glareImage, _glareMemory, _glareView) = CreateImage(size.Width, size.Height, GlareFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, ImageAspectFlags.ColorBit);
        var glareAttachments = stackalloc ImageView[] { _glareView, _depthView };
        var glareInfo = info with { RenderPass = _glareSourcePass, PAttachments = glareAttachments };
        Api.CreateFramebuffer(Device, in glareInfo, null, out _glareFramebuffer).ThrowOnError();
        _glare.Resize(size, _colorView, _glareView, (width, height, format, usage) => CreateImage(width, height, format, usage, ImageAspectFlags.ColorBit));
    }

    void DestroyTargets()
    {
        if (_framebuffer.Handle == 0)
            return;
        Api.DeviceWaitIdle(Device);
        Api.DestroyFramebuffer(Device, _framebuffer, null);
        Api.DestroyFramebuffer(Device, _glareFramebuffer, null);
        DestroyImage(_color, _colorMemory, _colorView);
        DestroyImage(_depth, _depthMemory, _depthView);
        DestroyImage(_glareImage, _glareMemory, _glareView);
        _framebuffer = default;
        _size = default;
    }

    (Image, DeviceMemory, ImageView) CreateImage(int width, int height, Format format, ImageUsageFlags usage, ImageAspectFlags aspect, uint mipLevels = 1)
    {
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = mipLevels,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Api.CreateImage(Device, in info, null, out var image).ThrowOnError();
        Api.GetImageMemoryRequirements(Device, image, out var requirements);
        var allocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = VulkanMemory.FindMemoryType(Api, _vk.PhysicalDevice, requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        Api.AllocateMemory(Device, in allocate, null, out var memory).ThrowOnError();
        Api.BindImageMemory(Device, image, memory, 0).ThrowOnError();

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, mipLevels, 0, 1),
        };
        Api.CreateImageView(Device, in viewInfo, null, out var view).ThrowOnError();
        return (image, memory, view);
    }

    void DestroyImage(Image image, DeviceMemory memory, ImageView view)
    {
        Api.DestroyImageView(Device, view, null);
        Api.DestroyImage(Device, image, null);
        Api.FreeMemory(Device, memory, null);
    }

    // ------------------------------------------------------------------ pipeline objects

    Format PickDepthFormat()
    {
        foreach (var format in new[] { Format.D32Sfloat, Format.D24UnormS8Uint, Format.D16Unorm })
        {
            Api.GetPhysicalDeviceFormatProperties(_vk.PhysicalDevice, format, out var properties);
            if (properties.OptimalTilingFeatures.HasFlag(FormatFeatureFlags.DepthStencilAttachmentBit))
                return format;
        }
        return Format.D16Unorm;
    }

    ShaderModule CreateShader(string name)
    {
        using var stream = typeof(VulkanSceneRenderer).Assembly.GetManifestResourceStream($"OpenDelulu.Shaders.{name}")
            ?? throw new FileNotFoundException($"Embedded shader {name} missing.");
        var code = new byte[stream.Length];
        stream.ReadExactly(code);
        fixed (byte* p = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)p,
            };
            Api.CreateShaderModule(Device, in info, null, out var module).ThrowOnError();
            return module;
        }
    }

    RenderPass CreateRenderPass()
    {
        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = new AttachmentDescription
        {
            Format = ColorFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.TransferSrcOptimal,
        };
        attachments[1] = new AttachmentDescription
        {
            Format = _depthFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store, // the glare source is hidden by it
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var depthRef = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
            PDepthStencilAttachment = &depthRef,
        };
        var dependencies = stackalloc SubpassDependency[2];
        dependencies[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.TransferBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            SrcAccessMask = AccessFlags.TransferReadBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
        };
        dependencies[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = PipelineStageFlags.TransferBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies,
        };
        Api.CreateRenderPass(Device, in info, null, out var renderPass).ThrowOnError();
        return renderPass;
    }

    /// <summary>
    /// The glare source pass: a colour image of its own (cleared, then sampled by <see cref="VulkanGlare"/>) and the
    /// scene pass's depth, tested but not written.
    /// </summary>
    RenderPass CreateGlareSourcePass()
    {
        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = new AttachmentDescription
        {
            Format = GlareFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        attachments[1] = new AttachmentDescription
        {
            Format = _depthFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.DepthStencilAttachmentOptimal,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var depthRef = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
            PDepthStencilAttachment = &depthRef,
        };
        var dependencies = stackalloc SubpassDependency[2];
        dependencies[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.LateFragmentTestsBit | PipelineStageFlags.FragmentShaderBit,
            DstStageMask = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
        };
        dependencies[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = PipelineStageFlags.FragmentShaderBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies,
        };
        Api.CreateRenderPass(Device, in info, null, out var renderPass).ThrowOnError();
        return renderPass;
    }

    Pipeline CreatePipeline(bool translucent, BlendFactor source = BlendFactor.SrcAlpha, BlendFactor destination = BlendFactor.OneMinusSrcAlpha,
        RenderPass renderPass = default, bool depthTest = true)
    {
        using var entry = new ByteString("main");
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = _vertex,
            PName = entry,
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = _fragment,
            PName = entry,
        };

        // RenderModel vertex: position (3), normal (3), uv (2), gloss (1) floats.
        var bindings = stackalloc VertexInputBindingDescription[3];
        bindings[0] = new VertexInputBindingDescription(0, RenderModel.VertexStride * sizeof(float), VertexInputRate.Vertex);
        bindings[1] = new VertexInputBindingDescription(1, 4 * sizeof(float), VertexInputRate.Vertex);
        bindings[2] = new VertexInputBindingDescription(2, 4 * sizeof(float), VertexInputRate.Vertex);
        var attributes = stackalloc VertexInputAttributeDescription[6];
        attributes[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);
        attributes[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32B32Sfloat, 3 * sizeof(float));
        attributes[2] = new VertexInputAttributeDescription(2, 0, Format.R32G32Sfloat, 6 * sizeof(float));
        attributes[3] = new VertexInputAttributeDescription(3, 0, Format.R32Sfloat, 8 * sizeof(float));
        attributes[4] = new VertexInputAttributeDescription(4, 1, Format.R32G32B32A32Sfloat, 0);
        attributes[5] = new VertexInputAttributeDescription(5, 2, Format.R32G32B32A32Sfloat, 0);
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 3,
            PVertexBindingDescriptions = bindings,
            VertexAttributeDescriptionCount = 6,
            PVertexAttributeDescriptions = attributes,
        };
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
        };
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1,
        };
        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.None, // Illusion meshes rely on two-sided rendering (hair, clothes).
            FrontFace = FrontFace.Clockwise,
            LineWidth = 1,
        };
        var multisample = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };
        var depth = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = depthTest,
            DepthWriteEnable = !translucent,
            DepthCompareOp = CompareOp.LessOrEqual,
        };
        var blendAttachment = new PipelineColorBlendAttachmentState
        {
            BlendEnable = translucent,
            SrcColorBlendFactor = source,
            DstColorBlendFactor = destination,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
            AlphaBlendOp = BlendOp.Add,
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
        };
        var blend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1,
            PAttachments = &blendAttachment,
        };
        var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamic = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamicStates,
        };
        var info = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2,
            PStages = stages,
            PVertexInputState = &vertexInput,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState = &multisample,
            PDepthStencilState = &depth,
            PColorBlendState = &blend,
            PDynamicState = &dynamic,
            Layout = _pipelineLayout,
            RenderPass = renderPass.Handle != 0 ? renderPass : _renderPass,
            Subpass = 0,
            BasePipelineIndex = -1,
        };
        Api.CreateGraphicsPipelines(Device, default, 1, in info, null, out var pipeline).ThrowOnError();
        return pipeline;
    }

    public void Dispose()
    {
        Api.DeviceWaitIdle(Device);
        ReleaseScene();
        DestroyTargets();
        _glare.Dispose();
        Api.DestroySampler(Device, _sampler, null);
        Api.DestroyPipeline(Device, _opaque, null);
        Api.DestroyPipeline(Device, _translucent, null);
        Api.DestroyPipeline(Device, _additive, null);
        Api.DestroyPipeline(Device, _darken, null);
        Api.DestroyPipeline(Device, _overlay, null);
        Api.DestroyPipeline(Device, _glareSource, null);
        Api.DestroyRenderPass(Device, _renderPass, null);
        Api.DestroyRenderPass(Device, _glareSourcePass, null);
        Api.DestroyPipelineLayout(Device, _pipelineLayout, null);
        Api.DestroyDescriptorSetLayout(Device, _setLayout, null);
        Api.DestroyShaderModule(Device, _vertex, null);
        Api.DestroyShaderModule(Device, _fragment, null);
    }
}
