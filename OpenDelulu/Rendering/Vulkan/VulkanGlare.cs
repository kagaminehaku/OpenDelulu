using System;
using System.Numerics;
using Avalonia;
using OpenDelulu.Engine.Stages;
using Silk.NET.Vulkan;

namespace OpenDelulu.Rendering.Vulkan;

/// <summary>
/// The stage glare (.glare, the exe's gi_dx9glare = the DirectX SDK star &amp; bloom) as Vulkan post-processing. The
/// glare source (glare meshes only, hidden by the scene; drawn by <see cref="VulkanSceneRenderer"/>) is shrunk to a
/// quarter, then either spread into four star lines (two 8-sample passes each) or blurred by the 15-tap Gaussian bloom,
/// and added onto the frame.
/// </summary>
internal sealed unsafe class VulkanGlare : IDisposable
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct Push
    {
        public Vector4 Texel;
        public Vector4 Params;
    }

    sealed class Target
    {
        public Image Image;
        public DeviceMemory Memory;
        public ImageView View;
        public Framebuffer Framebuffer;
        public int Width, Height;
    }

    const int Small = 0, BlurTemp = 1, Bloom = 2, StarTemp = 3, StarSum = 4;
    const int ModeShrink = 0, ModeBlur = 1, ModeStar = 2, ModeAdd = 3;

    readonly VulkanContext _vk;
    readonly Format _format, _sceneFormat;
    readonly ShaderModule _vertex, _fragment;
    readonly DescriptorSetLayout _setLayout;
    readonly PipelineLayout _layout;
    readonly RenderPass _clearPass, _loadPass, _addPass;
    readonly Pipeline _plain, _additive, _add;
    readonly Sampler _sampler;
    DescriptorPool _pool;
    readonly Target?[] _small = new Target?[5];
    Framebuffer _sceneFramebuffer;
    PixelSize _size;
    DescriptorSet _setSource;
    readonly DescriptorSet[] _setSmall = new DescriptorSet[5];

    Vk Api => _vk.Api;
    Device Device => _vk.Device;

    /// <param name="format">Format of the glare images (half floats: like the exe's float target, sums may pass 1).</param>
    /// <param name="sceneFormat">Format of the frame the glare is added to.</param>
    public VulkanGlare(VulkanContext vk, Format format, Format sceneFormat, Func<string, ShaderModule> shader)
    {
        _vk = vk;
        _format = format;
        _sceneFormat = sceneFormat;
        _vertex = shader("post.vert.spv");
        _fragment = shader("glare.frag.spv");

        var binding = new DescriptorSetLayoutBinding(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
        var setInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &binding };
        Api.CreateDescriptorSetLayout(Device, in setInfo, null, out _setLayout).ThrowOnError();
        var push = new PushConstantRange(ShaderStageFlags.FragmentBit, 0, (uint)sizeof(Push));
        var setLayout = _setLayout;
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &push,
        };
        Api.CreatePipelineLayout(Device, in layoutInfo, null, out _layout).ThrowOnError();

        _clearPass = CreatePass(_format, AttachmentLoadOp.Clear, ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal);
        _loadPass = CreatePass(_format, AttachmentLoadOp.Load, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.ShaderReadOnlyOptimal);
        // The frame itself, left by the scene pass ready to be copied out.
        _addPass = CreatePass(_sceneFormat, AttachmentLoadOp.Load, ImageLayout.TransferSrcOptimal, ImageLayout.TransferSrcOptimal);
        _plain = CreatePipeline(_clearPass, additive: false);
        _additive = CreatePipeline(_clearPass, additive: true);
        _add = CreatePipeline(_addPass, additive: true);

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        Api.CreateSampler(Device, in samplerInfo, null, out _sampler).ThrowOnError();
    }

    /// <summary>Turn of the star lines (radians); the exe uses _11 of the inverse view matrix.</summary>
    public float StarInclination { get; set; }

    RenderPass CreatePass(Format format, AttachmentLoadOp load, ImageLayout initial, ImageLayout final)
    {
        var attachment = new AttachmentDescription
        {
            Format = format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = load,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = initial,
            FinalLayout = final,
        };
        var reference = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &reference,
        };
        // Earlier passes' writes are sampled here; this pass's writes are sampled or copied later.
        var dependencies = stackalloc SubpassDependency[2];
        dependencies[0] = new SubpassDependency(Vk.SubpassExternal, 0,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ColorAttachmentOutputBit,
            AccessFlags.ColorAttachmentWriteBit | AccessFlags.TransferReadBit,
            AccessFlags.ShaderReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.ColorAttachmentReadBit);
        dependencies[1] = new SubpassDependency(0, Vk.SubpassExternal,
            PipelineStageFlags.ColorAttachmentOutputBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit,
            AccessFlags.ColorAttachmentWriteBit,
            AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit);
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies,
        };
        Api.CreateRenderPass(Device, in info, null, out var pass).ThrowOnError();
        return pass;
    }

    Pipeline CreatePipeline(RenderPass pass, bool additive)
    {
        using var entry = new ByteString("main");
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = _vertex, PName = entry };
        stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = _fragment, PName = entry };
        var vertexInput = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
        var assembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
        var viewport = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var raster = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.None,
            LineWidth = 1,
        };
        var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
        var blend = new PipelineColorBlendAttachmentState
        {
            BlendEnable = additive,
            SrcColorBlendFactor = BlendFactor.One,
            DstColorBlendFactor = BlendFactor.One,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.Zero,
            DstAlphaBlendFactor = BlendFactor.One,
            AlphaBlendOp = BlendOp.Add,
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
        };
        var blendState = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blend };
        var dynamics = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamics };
        var info = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2,
            PStages = stages,
            PVertexInputState = &vertexInput,
            PInputAssemblyState = &assembly,
            PViewportState = &viewport,
            PRasterizationState = &raster,
            PMultisampleState = &multisample,
            PColorBlendState = &blendState,
            PDynamicState = &dynamic,
            Layout = _layout,
            RenderPass = pass,
        };
        Api.CreateGraphicsPipelines(Device, default, 1, in info, null, out var pipeline).ThrowOnError();
        return pipeline;
    }

    /// <summary>
    /// (Re)creates the quarter-size images for a frame of <paramref name="size"/>: <paramref name="sceneView"/> is the
    /// frame the glare is added to, <paramref name="sourceView"/> the full-size glare source.
    /// </summary>
    public void Resize(PixelSize size, ImageView sceneView, ImageView sourceView,
        Func<int, int, Format, ImageUsageFlags, (Image, DeviceMemory, ImageView)> createImage)
    {
        DestroyTargets();
        _size = size;
        int width = Math.Max(1, size.Width / 4), height = Math.Max(1, size.Height / 4);
        for (int i = 0; i < _small.Length; i++)
        {
            var (image, memory, view) = createImage(width, height, _format, ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit);
            _small[i] = new Target { Image = image, Memory = memory, View = view, Width = width, Height = height, Framebuffer = CreateFramebuffer(_clearPass, view, width, height) };
        }
        _sceneFramebuffer = CreateFramebuffer(_addPass, sceneView, size.Width, size.Height);

        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 6);
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 6, PoolSizeCount = 1, PPoolSizes = &poolSize };
        Api.CreateDescriptorPool(Device, in poolInfo, null, out _pool).ThrowOnError();
        _setSource = CreateSet(sourceView);
        for (int i = 0; i < _small.Length; i++)
            _setSmall[i] = CreateSet(_small[i]!.View);
    }

    Framebuffer CreateFramebuffer(RenderPass pass, ImageView view, int width, int height)
    {
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = pass,
            AttachmentCount = 1,
            PAttachments = &view,
            Width = (uint)width,
            Height = (uint)height,
            Layers = 1,
        };
        Api.CreateFramebuffer(Device, in info, null, out var framebuffer).ThrowOnError();
        return framebuffer;
    }

    DescriptorSet CreateSet(ImageView source)
    {
        var setLayout = _setLayout;
        var allocate = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _pool, DescriptorSetCount = 1, PSetLayouts = &setLayout };
        Api.AllocateDescriptorSets(Device, in allocate, out var set).ThrowOnError();
        var image = new DescriptorImageInfo(_sampler, source, ImageLayout.ShaderReadOnlyOptimal);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &image,
        };
        Api.UpdateDescriptorSets(Device, 1, in write, 0, null);
        return set;
    }

    void Pass(CommandBuffer cmd, RenderPass pass, Framebuffer framebuffer, int width, int height, Pipeline pipeline, DescriptorSet set, Push push)
    {
        var clear = new ClearValue(new ClearColorValue(0, 0, 0, 1));
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = pass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height)),
            ClearValueCount = 1,
            PClearValues = &clear,
        };
        Api.CmdBeginRenderPass(cmd, in begin, SubpassContents.Inline);
        var viewport = new Viewport(0, 0, width, height, 0, 1);
        Api.CmdSetViewport(cmd, 0, 1, in viewport);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height));
        Api.CmdSetScissor(cmd, 0, 1, in scissor);
        Api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
        Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);
        Api.CmdPushConstants(cmd, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(Push), &push);
        Api.CmdDraw(cmd, 3, 1, 0, 0);
        Api.CmdEndRenderPass(cmd);
    }

    void Pass(CommandBuffer cmd, RenderPass pass, int target, Pipeline pipeline, DescriptorSet set, Push push)
    {
        var t = _small[target]!;
        Pass(cmd, pass, t.Framebuffer, t.Width, t.Height, pipeline, set, push);
    }

    /// <summary>
    /// Records the glare passes and adds the result onto the frame. The glare source must be readable
    /// (SHADER_READ_ONLY) and the frame in TRANSFER_SRC, where it stays.
    /// </summary>
    public void Apply(CommandBuffer cmd, StageGlare glare)
    {
        if (_small[0] is not { } small)
            return;
        var sourceTexel = new Vector2(1f / _size.Width, 1f / _size.Height);
        var texel = new Vector2(1f / small.Width, 1f / small.Height);
        Pass(cmd, _clearPass, Small, _plain, _setSource, new Push { Texel = new Vector4(sourceTexel, 0, 0), Params = new Vector4(0, 0, 0, ModeShrink) });

        int result;
        float weight;
        if (glare.Star)
        {
            // RenderStar: four lines a quarter turn apart, each two passes of 8 samples; the second pass steps 8 times
            // further and so does its attenuation (0.95^(214.41666 / (w + h) × sample)). The lines are summed and
            // weighted by the cross rate.
            float attenuation = 214.41666f / (small.Width + small.Height);
            for (int line = 0; line < 4; line++)
            {
                float angle = line * MathF.PI / 2 + StarInclination;
                var step = new Vector2(MathF.Sin(angle) * glare.StarLengthX * texel.X, MathF.Cos(angle) * glare.StarLengthY * texel.Y);
                Pass(cmd, _clearPass, StarTemp, _plain, _setSmall[Small],
                    new Push { Texel = new Vector4(texel, step.X, step.Y), Params = new Vector4(attenuation, 0.5f, 0, ModeStar) });
                Pass(cmd, line == 0 ? _clearPass : _loadPass, StarSum, line == 0 ? _plain : _additive, _setSmall[StarTemp],
                    new Push { Texel = new Vector4(texel, step.X * 8, step.Y * 8), Params = new Vector4(attenuation * 8, 1, 0, ModeStar) });
            }
            result = StarSum;
            weight = glare.CrossRate;
        }
        else
        {
            // The bloom: horizontal then vertical Gaussian, as many times as the file asks.
            int source = Small;
            var blur = new Vector4(glare.BloomDeviation, glare.BloomMultiplier, 0, ModeBlur);
            for (int i = 0; i < glare.BloomPasses; i++)
            {
                Pass(cmd, _clearPass, BlurTemp, _plain, _setSmall[source], new Push { Texel = new Vector4(texel.X, 0, 1, 0), Params = blur });
                Pass(cmd, _clearPass, Bloom, _plain, _setSmall[BlurTemp], new Push { Texel = new Vector4(0, texel.Y, 0, 1), Params = blur });
                source = Bloom;
            }
            result = Bloom;
            weight = 1;
        }
        Pass(cmd, _addPass, _sceneFramebuffer, _size.Width, _size.Height, _add, _setSmall[result],
            new Push { Params = new Vector4(weight, 0, 0, ModeAdd) });
    }

    void DestroyTargets()
    {
        if (_small[0] is null)
            return;
        Api.DeviceWaitIdle(Device);
        Api.DestroyDescriptorPool(Device, _pool, null);
        for (int i = 0; i < _small.Length; i++)
        {
            DestroyTarget(_small[i]);
            _small[i] = null;
        }
        Api.DestroyFramebuffer(Device, _sceneFramebuffer, null);
    }

    void DestroyTarget(Target? target)
    {
        if (target is null)
            return;
        Api.DestroyFramebuffer(Device, target.Framebuffer, null);
        Api.DestroyImageView(Device, target.View, null);
        Api.DestroyImage(Device, target.Image, null);
        Api.FreeMemory(Device, target.Memory, null);
    }

    public void Dispose()
    {
        DestroyTargets();
        Api.DestroySampler(Device, _sampler, null);
        Api.DestroyPipeline(Device, _plain, null);
        Api.DestroyPipeline(Device, _additive, null);
        Api.DestroyPipeline(Device, _add, null);
        Api.DestroyRenderPass(Device, _clearPass, null);
        Api.DestroyRenderPass(Device, _loadPass, null);
        Api.DestroyRenderPass(Device, _addPass, null);
        Api.DestroyPipelineLayout(Device, _layout, null);
        Api.DestroyDescriptorSetLayout(Device, _setLayout, null);
        Api.DestroyShaderModule(Device, _vertex, null);
        Api.DestroyShaderModule(Device, _fragment, null);
    }
}
