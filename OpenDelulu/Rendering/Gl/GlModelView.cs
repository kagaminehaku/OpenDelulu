using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using static Avalonia.OpenGL.GlConsts;

namespace OpenDelulu.Rendering.Gl;

/// <summary>
/// OpenGL backend. Targets OpenGL ES 2/3 and desktop GL 3.3, so the same code runs on Windows (ANGLE,
/// i.e. Direct3D 11 underneath), Linux, macOS, Android and iOS.
/// </summary>
internal sealed class GlModelView : OpenGlControlBase, IModelViewBackend
{
    sealed record GpuMesh(int VertexBuffer, int IndexBuffer, int IndexCount, int TextureIndex, Vector4 Diffuse, bool HasAlpha);

    GlExtras? _ext;
    int _program, _vao, _whiteTexture;
    int _uMvp, _uTex, _uDiffuse, _uLightDir, _uAlphaPass;
    readonly List<GpuMesh> _meshes = [];
    readonly List<int> _textures = [];
    ModelScene? _uploaded;
    ModelScene? _scene;
    bool _sceneDirty;
    readonly FrameClock _clock = new();

    public ModelScene? Scene
    {
        get => _scene;
        set
        {
            if (ReferenceEquals(_scene, value))
                return;
            _scene = value;
            _sceneDirty = true;
            RequestFrame();
        }
    }

    public OrbitCamera Camera { get; set; } = new();
    public string Description { get; private set; } = "OpenGL";

    public event Action<string>? Failed;
    public event Action? Ready;

    public void RequestFrame() => RequestNextFrameRendering();

    // ---------------------------------------------------------------- GL lifecycle

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            InitializeGl(gl);
            Description = $"{(GlVersion.Type == GlProfileType.OpenGLES ? "OpenGL ES" : "OpenGL")} {GlVersion.Major}.{GlVersion.Minor} · {gl.Renderer}";
            Ready?.Invoke();
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex.Message);
        }
    }

    protected override void OnOpenGlLost()
    {
        base.OnOpenGlLost();
        Failed?.Invoke("Mất ngữ cảnh OpenGL");
    }

    void InitializeGl(GlInterface gl)
    {
        _ext = new GlExtras(gl);
        bool es = GlVersion.Type == GlProfileType.OpenGLES;
        bool modern = GlVersion.Major >= 3;
        string header = (es, modern) switch
        {
            (true, true) => "#version 300 es\nprecision highp float;\n#define ATTRIB in\n#define VOUT out\n#define VIN in\n#define TEX texture\nout vec4 fragColor;\n",
            (true, false) => "#version 100\nprecision highp float;\n#define ATTRIB attribute\n#define VOUT varying\n#define VIN varying\n#define TEX texture2D\n#define fragColor gl_FragColor\n",
            _ => "#version 330 core\n#define ATTRIB in\n#define VOUT out\n#define VIN in\n#define TEX texture\nout vec4 fragColor;\n",
        };
        // "out vec4 fragColor" is only legal in fragment shaders; strip it for the vertex stage.
        string vertexHeader = header.Replace("out vec4 fragColor;\n", "");

        int vs = gl.CreateShader(GL_VERTEX_SHADER);
        CheckShader(gl.CompileShaderAndGetError(vs, vertexHeader + Shaders.Vertex), "vertex");
        int fs = gl.CreateShader(GL_FRAGMENT_SHADER);
        CheckShader(gl.CompileShaderAndGetError(fs, header + Shaders.Fragment), "fragment");

        _program = gl.CreateProgram();
        gl.AttachShader(_program, vs);
        gl.AttachShader(_program, fs);
        gl.BindAttribLocationString(_program, 0, "aPos");
        gl.BindAttribLocationString(_program, 1, "aNormal");
        gl.BindAttribLocationString(_program, 2, "aUV");
        CheckShader(gl.LinkProgramAndGetError(_program), "link");
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);

        _uMvp = gl.GetUniformLocationString(_program, "uMvp");
        _uTex = gl.GetUniformLocationString(_program, "uTex");
        _uDiffuse = gl.GetUniformLocationString(_program, "uDiffuse");
        _uLightDir = gl.GetUniformLocationString(_program, "uLightDir");
        _uAlphaPass = gl.GetUniformLocationString(_program, "uAlphaPass");

        if (gl.IsGenVertexArraysAvailable)
            _vao = gl.GenVertexArray();

        _whiteTexture = CreateTexture(gl, 1, 1, [255, 255, 255, 255]);
        _sceneDirty = true;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        ReleaseScene(gl);
        if (_whiteTexture != 0) gl.DeleteTexture(_whiteTexture);
        if (_vao != 0) gl.DeleteVertexArray(_vao);
        if (_program != 0) gl.DeleteProgram(_program);
        _whiteTexture = _vao = _program = 0;
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        var scene = _scene;
        if (_sceneDirty)
        {
            _sceneDirty = false;
            ReleaseScene(gl);
            if (scene is not null)
                UploadScene(gl, scene);
        }

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int width = Math.Max(1, (int)(Bounds.Width * scaling));
        int height = Math.Max(1, (int)(Bounds.Height * scaling));
        gl.Viewport(0, 0, width, height);
        gl.ClearColor(0.11f, 0.12f, 0.15f, 1f);
        gl.ClearDepth(1f);
        gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);

        if (scene is null || _uploaded != scene || _meshes.Count == 0)
            return;

        AnimateScene(gl, scene);

        var (mvp, light) = Camera.Compute(scene.Model, width / (float)height, flipY: false);

        gl.UseProgram(_program);
        if (_vao != 0) gl.BindVertexArray(_vao);
        gl.UniformMatrix4fv(_uMvp, 1, false, &mvp);
        gl.Uniform1i(_uTex, 0);
        _ext!.Uniform3f(_uLightDir, light.X, light.Y, light.Z);
        gl.ActiveTexture(GL_TEXTURE0);
        gl.Enable(GL_DEPTH_TEST);
        gl.DepthFunc(GlExtras.GL_LEQUAL);
        gl.Disable(GL_CULL_FACE); // Illusion meshes rely on two-sided rendering (hair, clothes).

        // Pass 1: opaque texels, depth write on.
        gl.DepthMask(1);
        gl.Disable(GlExtras.GL_BLEND);
        gl.Uniform1f(_uAlphaPass, 0f);
        DrawMeshes(gl, scene, translucentOnly: false);

        // Pass 2: soft edges of alpha-textured meshes, blended, depth write off.
        gl.DepthMask(0);
        gl.Enable(GlExtras.GL_BLEND);
        _ext.BlendFunc(GlExtras.GL_SRC_ALPHA, GlExtras.GL_ONE_MINUS_SRC_ALPHA);
        gl.Uniform1f(_uAlphaPass, 1f);
        DrawMeshes(gl, scene, translucentOnly: true);

        gl.DepthMask(1);
        gl.Disable(GlExtras.GL_BLEND);
        gl.Disable(GL_DEPTH_TEST);
        if (_vao != 0) gl.BindVertexArray(0);
        gl.UseProgram(0);
    }

    /// <summary>Advances the animator, re-uploads the re-skinned vertices and keeps frames coming while it moves.</summary>
    unsafe void AnimateScene(GlInterface gl, ModelScene scene)
    {
        if (scene.Animator is not { } animator)
            return;
        if (animator.Update(_clock.Tick(), scene.IsVisible))
        {
            var meshes = scene.Model.Meshes;
            for (int i = 0; i < _meshes.Count; i++)
            {
                if (!scene.IsVisible(i))
                    continue;
                var vertices = meshes[i].Vertices;
                gl.BindBuffer(GL_ARRAY_BUFFER, _meshes[i].VertexBuffer);
                fixed (float* v = vertices)
                    gl.BufferData(GL_ARRAY_BUFFER, vertices.Length * sizeof(float), (IntPtr)v, GL_DYNAMIC_DRAW);
            }
            gl.BindBuffer(GL_ARRAY_BUFFER, 0);
        }

        if (animator.IsAnimating)
            RequestNextFrameRendering();
        else
            _clock.Pause();
    }

    const int GL_DYNAMIC_DRAW = 0x88E8;

    void DrawMeshes(GlInterface gl, ModelScene scene, bool translucentOnly)
    {
        for (int i = 0; i < _meshes.Count; i++)
        {
            var mesh = _meshes[i];
            if (!scene.IsVisible(i) || (translucentOnly && !mesh.HasAlpha))
                continue;

            gl.BindTexture(GL_TEXTURE_2D, mesh.TextureIndex >= 0 ? _textures[mesh.TextureIndex] : _whiteTexture);
            var d = mesh.Diffuse;
            _ext!.Uniform4f(_uDiffuse, d.X, d.Y, d.Z, d.W);

            gl.BindBuffer(GL_ARRAY_BUFFER, mesh.VertexBuffer);
            const int stride = RenderModelStride * sizeof(float);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, stride, 0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, stride, 3 * sizeof(float));
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 2, GL_FLOAT, 0, stride, 6 * sizeof(float));
            gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, mesh.IndexBuffer);
            gl.DrawElements(GL_TRIANGLES, mesh.IndexCount, GL_UNSIGNED_SHORT, 0);
        }
    }

    const int RenderModelStride = Engine.Assets.RenderModel.VertexStride;

    unsafe void UploadScene(GlInterface gl, ModelScene scene)
    {
        var model = scene.Model;
        foreach (var image in model.Textures)
            _textures.Add(image is null ? _whiteTexture : CreateTexture(gl, image.Width, image.Height, image.Pixels));

        foreach (var mesh in model.Meshes)
        {
            int vbo = gl.GenBuffer();
            gl.BindBuffer(GL_ARRAY_BUFFER, vbo);
            fixed (float* v = mesh.Vertices)
                gl.BufferData(GL_ARRAY_BUFFER, mesh.Vertices.Length * sizeof(float), (IntPtr)v, GL_STATIC_DRAW);

            int ibo = gl.GenBuffer();
            gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, ibo);
            fixed (ushort* idx = mesh.Indices)
                gl.BufferData(GL_ELEMENT_ARRAY_BUFFER, mesh.Indices.Length * sizeof(ushort), (IntPtr)idx, GL_STATIC_DRAW);

            var diffuse = mesh.Diffuse;
            if (diffuse.X + diffuse.Y + diffuse.Z < 0.05f)
                diffuse = Vector4.One; // some materials only use ambient/emissive
            _meshes.Add(new GpuMesh(vbo, ibo, mesh.Indices.Length, mesh.TextureIndex, diffuse, mesh.HasAlpha));
        }
        gl.BindBuffer(GL_ARRAY_BUFFER, 0);
        gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, 0);
        _uploaded = scene;
    }

    void ReleaseScene(GlInterface gl)
    {
        foreach (var mesh in _meshes)
        {
            gl.DeleteBuffer(mesh.VertexBuffer);
            gl.DeleteBuffer(mesh.IndexBuffer);
        }
        foreach (int texture in _textures)
            if (texture != _whiteTexture)
                gl.DeleteTexture(texture);
        _meshes.Clear();
        _textures.Clear();
        _uploaded = null;
    }

    unsafe int CreateTexture(GlInterface gl, int width, int height, byte[] rgba)
    {
        int texture = gl.GenTexture();
        gl.BindTexture(GL_TEXTURE_2D, texture);
        fixed (byte* p = rgba)
            gl.TexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, width, height, 0, GL_RGBA, GL_UNSIGNED_BYTE, (IntPtr)p);
        bool pot = (width & (width - 1)) == 0 && (height & (height - 1)) == 0;
        bool mips = pot && _ext!.CanGenerateMipmap;
        if (mips)
            _ext!.GenerateMipmap(GL_TEXTURE_2D);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, mips ? GlExtras.GL_LINEAR_MIPMAP_LINEAR : GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        // GLES2 only allows REPEAT on power-of-two textures.
        int wrap = pot ? GlExtras.GL_REPEAT : GL_CLAMP_TO_EDGE;
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, wrap);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, wrap);
        return texture;
    }

    static void CheckShader(string? error, string stage)
    {
        if (!string.IsNullOrEmpty(error))
        {
            Trace.WriteLine($"[ModelViewer] {stage} shader error: {error}");
            throw new InvalidOperationException($"{stage} shader: {error}");
        }
    }

    static class Shaders
    {
        public const string Vertex = """
            ATTRIB vec3 aPos;
            ATTRIB vec3 aNormal;
            ATTRIB vec2 aUV;
            uniform mat4 uMvp;
            VOUT vec3 vNormal;
            VOUT vec2 vUV;
            void main()
            {
                vNormal = aNormal;
                vUV = aUV;
                gl_Position = uMvp * vec4(aPos, 1.0);
            }
            """;

        // uAlphaPass 0: opaque texels (alpha test). 1: translucent edges only.
        public const string Fragment = """
            uniform sampler2D uTex;
            uniform vec4 uDiffuse;
            uniform vec3 uLightDir;
            uniform float uAlphaPass;
            VIN vec3 vNormal;
            VIN vec2 vUV;
            void main()
            {
                vec4 texel = TEX(uTex, vUV);
                float alpha = texel.a; // material alpha is unreliable in SB3 data
                if (uAlphaPass < 0.5) {
                    if (alpha < 0.5) discard;
                } else {
                    if (alpha >= 0.5 || alpha < 0.02) discard;
                }
                float lambert = abs(dot(normalize(vNormal), uLightDir));
                vec3 color = texel.rgb * uDiffuse.rgb * (0.45 + 0.6 * lambert);
                fragColor = vec4(color, alpha);
            }
            """;
    }
}
