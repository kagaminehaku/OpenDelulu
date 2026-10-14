using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using OpenDelulu.Engine.Assets;
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

    [StructLayout(LayoutKind.Sequential)]
    struct PushConstants
    {
        public Matrix4x4 Mvp;
        public Vector4 Diffuse;
        public Vector4 LightAndPass;
    }

    sealed class GpuTexture
    {
        public Image Image;
        public DeviceMemory Memory;
        public ImageView View;
        public DescriptorSet Set;
    }

    sealed class GpuMesh(VulkanBuffer vertices, VulkanBuffer indices, int indexCount, int texture, Vector4 diffuse, bool hasAlpha)
    {
        public VulkanBuffer Vertices { get; } = vertices;
        public VulkanBuffer Indices { get; } = indices;
        public int IndexCount { get; } = indexCount;
        public int Texture { get; } = texture;
        public Vector4 Diffuse { get; } = diffuse;
        public bool HasAlpha { get; } = hasAlpha;
    }

    readonly VulkanContext _vk;
    readonly Format _depthFormat;
    readonly ShaderModule _vertex, _fragment;
    readonly DescriptorSetLayout _setLayout;
    readonly PipelineLayout _pipelineLayout;
    readonly RenderPass _renderPass;
    readonly Pipeline _opaque, _translucent;
    readonly Sampler _sampler;

    PixelSize _size;
    Image _color, _depth;
    DeviceMemory _colorMemory, _depthMemory;
    ImageView _colorView, _depthView;
    Framebuffer _framebuffer;

    ModelScene? _scene;
    readonly List<GpuMesh> _meshes = [];
    readonly List<GpuTexture> _textures = [];
    GpuTexture? _white;
    DescriptorPool _descriptorPool;

    Vk Api => _vk.Api;
    Device Device => _vk.Device;

    public VulkanSceneRenderer(VulkanContext vk)
    {
        _vk = vk;
        _depthFormat = PickDepthFormat();
        _vertex = CreateShader("model.vert.spv");
        _fragment = CreateShader("model.frag.spv");

        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var setLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
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
        _opaque = CreatePipeline(translucent: false);
        _translucent = CreatePipeline(translucent: true);

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
    public bool Render(VulkanSharedImage target, ModelScene? scene, OrbitCamera camera, double deltaSeconds)
    {
        if (target.Size != _size)
            CreateTargets(target.Size);

        if (!ReferenceEquals(scene, _scene))
        {
            ReleaseScene();
            if (scene is not null)
                UploadScene(scene);
            _scene = scene;
        }

        // The swapchain waited for the previous frame, so the mapped vertex buffers are free to rewrite.
        bool animating = false;
        if (scene?.Animator is { } animator)
        {
            if (animator.Update(deltaSeconds, scene.IsVisible))
                for (int i = 0; i < _meshes.Count; i++)
                    if (scene.IsVisible(i))
                        _meshes[i].Vertices.Write<float>(scene.Model.Meshes[i].Vertices);
            animating = animator.IsAnimating;
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
            // No Y flip: Vulkan's clip space points down, but the compositor samples the shared image with its
            // origin at the bottom (verified on the D3D11 shared-texture path), and the two cancel out.
            var (viewProjection, light) = camera.Compute(scene.Model, _size.Width / (float)_size.Height, flipY: false);
            DrawPass(cmd, scene, _opaque, viewProjection, light, translucent: false);
            DrawPass(cmd, scene, _translucent, viewProjection, light, translucent: true);
        }
        Api.CmdEndRenderPass(cmd);

        // Offscreen color is now TRANSFER_SRC (render pass final layout); copy it into the shared image.
        target.TransitionLayout(cmd, ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit);
        var layers = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1);
        var region = new ImageCopy(layers, new Offset3D(0, 0, 0), layers, new Offset3D(0, 0, 0),
            new Extent3D((uint)_size.Width, (uint)_size.Height, 1));
        Api.CmdCopyImage(cmd, _color, ImageLayout.TransferSrcOptimal, target.Image, ImageLayout.TransferDstOptimal, 1, in region);
        commands.Submit();
        return animating;
    }

    void DrawPass(CommandBuffer cmd, ModelScene scene, Pipeline pipeline, Matrix4x4 viewProjection, Vector3 light, bool translucent)
    {
        Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
        var push = new PushConstants
        {
            Mvp = viewProjection,
            LightAndPass = new Vector4(light, translucent ? 1f : 0f),
        };
        for (int i = 0; i < _meshes.Count; i++)
        {
            var mesh = _meshes[i];
            if (!scene.IsVisible(i) || (translucent && !mesh.HasAlpha))
                continue;

            var set = (mesh.Texture >= 0 ? _textures[mesh.Texture] : _white!).Set;
            Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &set, 0, null);
            push.Diffuse = mesh.Diffuse;
            Api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                0, (uint)sizeof(PushConstants), &push);
            var buffer = mesh.Vertices.Handle;
            ulong offset = 0;
            Api.CmdBindVertexBuffers(cmd, 0, 1, &buffer, &offset);
            Api.CmdBindIndexBuffer(cmd, mesh.Indices.Handle, 0, IndexType.Uint16);
            Api.CmdDrawIndexed(cmd, (uint)mesh.IndexCount, 1, 0, 0, 0);
        }
    }

    // ------------------------------------------------------------------ scene resources

    void UploadScene(ModelScene scene)
    {
        var model = scene.Model;
        int textureCount = model.Textures.Count + 1;
        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, (uint)textureCount);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = (uint)textureCount,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        Api.CreateDescriptorPool(Device, in poolInfo, null, out _descriptorPool).ThrowOnError();

        // All texture uploads go into one command buffer; staging buffers live until it completes.
        var staging = new List<VulkanBuffer>();
        var commands = _vk.Pool.CreateCommandBuffer();
        commands.BeginRecording();
        _white = CreateTexture(commands.Handle, new RgbaImage(1, 1, [255, 255, 255, 255]), staging);
        foreach (var image in model.Textures)
            _textures.Add(image is null ? _white : CreateTexture(commands.Handle, image, staging));
        commands.Submit();
        Api.QueueWaitIdle(_vk.Queue).ThrowOnError();
        foreach (var buffer in staging)
            buffer.Dispose();

        foreach (var mesh in model.Meshes)
        {
            var vertices = new VulkanBuffer(_vk, (ulong)(mesh.Vertices.Length * sizeof(float)), BufferUsageFlags.VertexBufferBit);
            vertices.Write<float>(mesh.Vertices);
            var indices = new VulkanBuffer(_vk, (ulong)(mesh.Indices.Length * sizeof(ushort)), BufferUsageFlags.IndexBufferBit);
            indices.Write<ushort>(mesh.Indices);
            var diffuse = mesh.Diffuse;
            if (diffuse.X + diffuse.Y + diffuse.Z < 0.05f)
                diffuse = Vector4.One; // some materials only use ambient/emissive
            _meshes.Add(new GpuMesh(vertices, indices, mesh.Indices.Length, mesh.TextureIndex, diffuse, mesh.HasAlpha));
        }
    }

    GpuTexture CreateTexture(CommandBuffer cmd, RgbaImage image, List<VulkanBuffer> staging)
    {
        var upload = new VulkanBuffer(_vk, (ulong)image.Pixels.Length, BufferUsageFlags.TransferSrcBit);
        upload.Write<byte>(image.Pixels);
        staging.Add(upload);

        // Full mip chain, generated on the GPU by successive half-size blits (matches the GL backend's mipmaps).
        uint mipLevels = (uint)Math.Floor(Math.Log2(Math.Max(image.Width, image.Height))) + 1;
        var texture = new GpuTexture();
        (texture.Image, texture.Memory, texture.View) = CreateImage(image.Width, image.Height, ColorFormat,
            ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit, ImageAspectFlags.ColorBit, mipLevels);

        VulkanMemory.TransitionLayout(Api, cmd, texture.Image, ImageLayout.Undefined, AccessFlags.None,
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

        var setLayout = _setLayout;
        var allocate = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        Api.AllocateDescriptorSets(Device, in allocate, out texture.Set).ThrowOnError();
        var imageInfo = new DescriptorImageInfo(_sampler, texture.View, ImageLayout.ShaderReadOnlyOptimal);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = texture.Set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &imageInfo,
        };
        Api.UpdateDescriptorSets(Device, 1, in write, 0, null);
        return texture;
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
        }
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
    }

    void DestroyTargets()
    {
        if (_framebuffer.Handle == 0)
            return;
        Api.DeviceWaitIdle(Device);
        Api.DestroyFramebuffer(Device, _framebuffer, null);
        DestroyImage(_color, _colorMemory, _colorView);
        DestroyImage(_depth, _depthMemory, _depthView);
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
            StoreOp = AttachmentStoreOp.DontCare,
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

    Pipeline CreatePipeline(bool translucent)
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

        // RenderModel vertex: position (3), normal (3), uv (2) floats.
        var binding = new VertexInputBindingDescription(0, RenderModel.VertexStride * sizeof(float), VertexInputRate.Vertex);
        var attributes = stackalloc VertexInputAttributeDescription[3];
        attributes[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);
        attributes[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32B32Sfloat, 3 * sizeof(float));
        attributes[2] = new VertexInputAttributeDescription(2, 0, Format.R32G32Sfloat, 6 * sizeof(float));
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = &binding,
            VertexAttributeDescriptionCount = 3,
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
            DepthTestEnable = true,
            DepthWriteEnable = !translucent,
            DepthCompareOp = CompareOp.LessOrEqual,
        };
        var blendAttachment = new PipelineColorBlendAttachmentState
        {
            BlendEnable = translucent,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
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
            RenderPass = _renderPass,
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
        Api.DestroySampler(Device, _sampler, null);
        Api.DestroyPipeline(Device, _opaque, null);
        Api.DestroyPipeline(Device, _translucent, null);
        Api.DestroyRenderPass(Device, _renderPass, null);
        Api.DestroyPipelineLayout(Device, _pipelineLayout, null);
        Api.DestroyDescriptorSetLayout(Device, _setLayout, null);
        Api.DestroyShaderModule(Device, _vertex, null);
        Api.DestroyShaderModule(Device, _fragment, null);
    }
}
