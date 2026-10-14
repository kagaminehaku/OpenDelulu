using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using static Avalonia.OpenGL.GlConsts;

namespace OpenDelulu.Rendering;

/// <summary>
/// Orbit-camera viewer for a <see cref="ModelScene"/>. Targets OpenGL ES 2/3 and desktop GL 3.3,
/// so the same code runs on Windows (ANGLE), Linux, macOS, Android and iOS.
/// Drag = rotate, wheel / pinch = zoom.
/// </summary>
public sealed class ModelViewer : OpenGlControlBase
{
    public static readonly StyledProperty<ModelScene?> SceneProperty =
        AvaloniaProperty.Register<ModelViewer, ModelScene?>(nameof(Scene));

    public ModelScene? Scene
    {
        get => GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public static readonly StyledProperty<bool> KeepCameraProperty =
        AvaloniaProperty.Register<ModelViewer, bool>(nameof(KeepCamera));

    /// <summary>Keep rotation/zoom when the scene changes (e.g. switching swimsuits).</summary>
    public bool KeepCamera
    {
        get => GetValue(KeepCameraProperty);
        set => SetValue(KeepCameraProperty, value);
    }

    sealed record GpuMesh(int VertexBuffer, int IndexBuffer, int IndexCount, int TextureIndex, Vector4 Diffuse, bool HasAlpha);

    GlExtras? _ext;
    int _program, _vao, _whiteTexture;
    int _uMvp, _uTex, _uDiffuse, _uLightDir, _uAlphaPass;
    readonly List<GpuMesh> _meshes = [];
    readonly List<int> _textures = [];
    ModelScene? _uploaded;
    bool _sceneDirty;

    // Illusion characters face +Z, so the default camera looks from the front (+Z towards -Z).
    const float DefaultYaw = MathF.PI;
    float _yaw = DefaultYaw, _pitch = 0.1f, _zoom = 1f;
    Point? _dragFrom;

    public ModelViewer()
    {
        GestureRecognizers.Add(new PinchGestureRecognizer());
        AddHandler(PinchEvent, (_, e) =>
        {
            _zoom = Math.Clamp(_zoom / (float)Math.Max(0.5, Math.Min(2, e.Scale)), 0.05f, 20f);
            RequestNextFrameRendering();
        });
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SceneProperty)
            return;
        if (change.OldValue is ModelScene oldScene)
            oldScene.Changed -= RequestNextFrameRendering;
        if (change.NewValue is ModelScene newScene)
            newScene.Changed += RequestNextFrameRendering;
        _sceneDirty = true;
        if (!KeepCamera || change.OldValue is null)
        {
            _yaw = DefaultYaw;
            _pitch = 0.1f;
            _zoom = 1f;
        }
        RequestNextFrameRendering();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragFrom = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from)
            return;
        var to = e.GetPosition(this);
        _yaw -= (float)(to.X - from.X) * 0.01f;
        _pitch = Math.Clamp(_pitch + (float)(to.Y - from.Y) * 0.01f, -1.5f, 1.5f);
        _dragFrom = to;
        RequestNextFrameRendering();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _zoom = Math.Clamp(_zoom * MathF.Pow(0.88f, (float)e.Delta.Y), 0.05f, 20f);
        RequestNextFrameRendering();
        e.Handled = true;
    }

    // ---------------------------------------------------------------- GL lifecycle

    protected override void OnOpenGlInit(GlInterface gl)
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
        var scene = Scene;
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

        var model = scene.Model;
        float radius = model.Radius;
        var target = model.Center;
        float distance = radius * 2.4f * _zoom;
        var eyeDir = new Vector3(
            MathF.Sin(_yaw) * MathF.Cos(_pitch),
            MathF.Sin(_pitch),
            -MathF.Cos(_yaw) * MathF.Cos(_pitch));
        var eye = target + eyeDir * distance;

        // Direct3D content: left-handed view/projection, row-vector matrices (uploaded untransposed).
        var view = Matrix4x4.CreateLookAtLeftHanded(eye, target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
            MathF.PI / 4, width / (float)height, Math.Max(distance - radius * 1.5f, radius * 0.01f), distance + radius * 3f);
        var mvp = view * projection;
        var light = Vector3.Normalize(eyeDir + new Vector3(0, 0.6f, 0));

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
