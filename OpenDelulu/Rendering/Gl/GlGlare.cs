using System;
using OpenDelulu.Engine.Stages;
using Avalonia.OpenGL;
using static Avalonia.OpenGL.GlConsts;

namespace OpenDelulu.Rendering.Gl;

/// <summary>
/// The stage glare for the OpenGL backend; same passes as <see cref="Vulkan.VulkanGlare"/>, with 8-bit images (GLES 2
/// has no guaranteed float targets, so glare meshes adding past 1 are clamped). The source image has a depth buffer of
/// its own: the caller draws the scene's depth into it, then the glare meshes (<see cref="BeginSource"/>).
/// </summary>
internal sealed class GlGlare
{
    const int GL_FRAMEBUFFER = 0x8D40, GL_RENDERBUFFER = 0x8D41, GL_COLOR_ATTACHMENT0 = 0x8CE0, GL_DEPTH_ATTACHMENT = 0x8D00;
    const int GL_DEPTH_COMPONENT16 = 0x81A5;
    const int Small = 0, BlurTemp = 1, Bloom = 2, StarTemp = 3, StarSum = 4;
    const int ModeShrink = 0, ModeBlur = 1, ModeStar = 2, ModeAdd = 3;

    const string Vertex = """
        ATTRIB vec2 aPos;
        VOUT vec2 vUV;
        void main()
        {
            vUV = aPos * 0.5 + 0.5;
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    // Keep in sync with the Vulkan shader (glare.frag).
    const string Fragment = """
        uniform sampler2D uSource;
        uniform vec4 uTexel;
        uniform vec4 uParams;
        VIN vec2 vUV;
        vec3 chromatic(int s)
        {
            if (s == 0) return vec3(0.5, 0.5, 0.5);
            if (s == 1) return vec3(0.8, 0.3, 0.3);
            if (s == 2) return vec3(1.0, 0.2, 0.2);
            if (s == 3) return vec3(0.5, 0.2, 0.6);
            if (s == 4) return vec3(0.2, 0.2, 1.0);
            if (s == 5) return vec3(0.2, 0.3, 0.7);
            if (s == 6) return vec3(0.2, 0.6, 0.2);
            return vec3(0.3, 0.5, 0.3);
        }
        void main()
        {
            float mode = uParams.w;
            vec3 sum = vec3(0.0);
            if (mode < 0.5) {
                for (int y = 0; y < 2; y++)
                    for (int x = 0; x < 2; x++)
                        sum += TEX(uSource, vUV + (vec2(float(x), float(y)) * 2.0 - 1.0) * uTexel.xy).rgb;
                fragColor = vec4(sum / 4.0, 1.0);
            } else if (mode < 1.5) {
                float deviation = max(uParams.x, 0.001);
                for (int i = -7; i <= 7; i++) {
                    float g = exp(-float(i * i) / (2.0 * deviation * deviation)) / sqrt(6.2831853 * deviation * deviation);
                    sum += TEX(uSource, vUV + float(i) * uTexel.xy * uTexel.zw).rgb * g;
                }
                fragColor = vec4(sum * uParams.y, 1.0);
            } else if (mode < 2.5) {
                for (int s = 0; s < 8; s++) {
                    vec2 offset = float(s) * uTexel.zw;
                    if (abs(offset.x) >= 0.9 || abs(offset.y) >= 0.9)
                        continue;
                    vec3 aberration = mix(chromatic(s), vec3(0.63), float(s + 1) / 8.0);
                    vec3 color = mix(aberration, vec3(1.0), 0.7);
                    sum += TEX(uSource, vUV + offset).rgb * color * pow(0.95, uParams.x * float(s)) * uParams.y;
                }
                fragColor = vec4(sum, 1.0);
            } else {
                fragColor = vec4(TEX(uSource, vUV).rgb * uParams.x, 1.0);
            }
        }
        """;

    readonly GlExtras _ext;
    readonly int _program, _uSource, _uTexel, _uParams, _triangle;
    int _width, _height, _smallWidth, _smallHeight;
    int _sourceFramebuffer, _sourceTexture, _sourceDepth;
    readonly int[] _framebuffers = new int[5], _textures = new int[5];

    public GlGlare(GlInterface gl, GlExtras ext, string vertexHeader, string fragmentHeader)
    {
        _ext = ext;
        int vs = gl.CreateShader(GL_VERTEX_SHADER);
        GlModelView.CheckShader(gl.CompileShaderAndGetError(vs, vertexHeader + Vertex), "glare vertex");
        int fs = gl.CreateShader(GL_FRAGMENT_SHADER);
        GlModelView.CheckShader(gl.CompileShaderAndGetError(fs, fragmentHeader + Fragment), "glare fragment");
        _program = gl.CreateProgram();
        gl.AttachShader(_program, vs);
        gl.AttachShader(_program, fs);
        gl.BindAttribLocationString(_program, 0, "aPos");
        GlModelView.CheckShader(gl.LinkProgramAndGetError(_program), "glare link");
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        _uSource = gl.GetUniformLocationString(_program, "uSource");
        _uTexel = gl.GetUniformLocationString(_program, "uTexel");
        _uParams = gl.GetUniformLocationString(_program, "uParams");

        // One triangle covering the screen.
        _triangle = gl.GenBuffer();
        gl.BindBuffer(GL_ARRAY_BUFFER, _triangle);
        unsafe
        {
            float* corners = stackalloc float[] { -1, -1, 3, -1, -1, 3 };
            gl.BufferData(GL_ARRAY_BUFFER, 6 * sizeof(float), (IntPtr)corners, GL_STATIC_DRAW);
        }
        gl.BindBuffer(GL_ARRAY_BUFFER, 0);
    }

    /// <summary>Turn of the star lines (radians); the exe uses _11 of the inverse view matrix.</summary>
    public float StarInclination { get; set; }

    /// <summary>Binds and clears the full-size glare source (colour and depth) for a frame of the given size.</summary>
    public void BeginSource(GlInterface gl, int width, int height)
    {
        if (width != _width || height != _height)
            Resize(gl, width, height);
        gl.BindFramebuffer(GL_FRAMEBUFFER, _sourceFramebuffer);
        gl.Viewport(0, 0, width, height);
        gl.ClearColor(0, 0, 0, 1);
        gl.ClearDepth(1);
        gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
    }

    void Resize(GlInterface gl, int width, int height)
    {
        DeleteTargets(gl);
        _width = width;
        _height = height;
        _smallWidth = Math.Max(1, width / 4);
        _smallHeight = Math.Max(1, height / 4);
        _sourceTexture = CreateTexture(gl, width, height);
        _sourceDepth = gl.GenRenderbuffer();
        gl.BindRenderbuffer(GL_RENDERBUFFER, _sourceDepth);
        gl.RenderbufferStorage(GL_RENDERBUFFER, GL_DEPTH_COMPONENT16, width, height);
        gl.BindRenderbuffer(GL_RENDERBUFFER, 0);
        _sourceFramebuffer = gl.GenFramebuffer();
        gl.BindFramebuffer(GL_FRAMEBUFFER, _sourceFramebuffer);
        gl.FramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _sourceTexture, 0);
        gl.FramebufferRenderbuffer(GL_FRAMEBUFFER, GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, _sourceDepth);
        for (int i = 0; i < _textures.Length; i++)
        {
            _textures[i] = CreateTexture(gl, _smallWidth, _smallHeight);
            _framebuffers[i] = gl.GenFramebuffer();
            gl.BindFramebuffer(GL_FRAMEBUFFER, _framebuffers[i]);
            gl.FramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _textures[i], 0);
        }
    }

    static int CreateTexture(GlInterface gl, int width, int height)
    {
        int texture = gl.GenTexture();
        gl.BindTexture(GL_TEXTURE_2D, texture);
        gl.TexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, width, height, 0, GL_RGBA, GL_UNSIGNED_BYTE, IntPtr.Zero);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        gl.BindTexture(GL_TEXTURE_2D, 0);
        return texture;
    }

    /// <summary>Spreads the source into a star or bloom and adds it onto <paramref name="framebuffer"/>.</summary>
    public void Apply(GlInterface gl, int framebuffer, StageGlare glare)
    {
        gl.UseProgram(_program);
        gl.Uniform1i(_uSource, 0);
        gl.ActiveTexture(GL_TEXTURE0);
        gl.Disable(GL_DEPTH_TEST);
        gl.DepthMask(0);
        for (int i = 1; i <= 4; i++)
            _ext.DisableVertexAttribArray(i);
        gl.BindBuffer(GL_ARRAY_BUFFER, _triangle);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, GL_FLOAT, 0, 2 * sizeof(float), IntPtr.Zero);

        float tx = 1f / _smallWidth, ty = 1f / _smallHeight;
        Pass(gl, _framebuffers[Small], _sourceTexture, false, 1f / _width, 1f / _height, 0, 0, 0, 0, ModeShrink);
        int result;
        float weight;
        if (glare.Star)
        {
            float attenuation = 214.41666f / (_smallWidth + _smallHeight);
            for (int line = 0; line < 4; line++)
            {
                float angle = line * MathF.PI / 2 + StarInclination;
                float sx = MathF.Sin(angle) * glare.StarLengthX * tx, sy = MathF.Cos(angle) * glare.StarLengthY * ty;
                Pass(gl, _framebuffers[StarTemp], _textures[Small], false, tx, ty, sx, sy, attenuation, 0.5f, ModeStar);
                Pass(gl, _framebuffers[StarSum], _textures[StarTemp], line > 0, tx, ty, sx * 8, sy * 8, attenuation * 8, 1, ModeStar);
            }
            result = StarSum;
            weight = glare.CrossRate;
        }
        else
        {
            int source = Small;
            for (int i = 0; i < glare.BloomPasses; i++)
            {
                Pass(gl, _framebuffers[BlurTemp], _textures[source], false, tx, 0, 1, 0, glare.BloomDeviation, glare.BloomMultiplier, ModeBlur);
                Pass(gl, _framebuffers[Bloom], _textures[BlurTemp], false, 0, ty, 0, 1, glare.BloomDeviation, glare.BloomMultiplier, ModeBlur);
                source = Bloom;
            }
            result = Bloom;
            weight = 1;
        }

        gl.BindFramebuffer(GL_FRAMEBUFFER, framebuffer);
        gl.Viewport(0, 0, _width, _height);
        gl.Enable(GlExtras.GL_BLEND);
        _ext.BlendFunc(1, 1);
        gl.BindTexture(GL_TEXTURE_2D, _textures[result]);
        _ext.Uniform4f(_uTexel, 0, 0, 0, 0);
        _ext.Uniform4f(_uParams, weight, 0, 0, ModeAdd);
        gl.DrawArrays(GL_TRIANGLES, 0, 3);
        gl.Disable(GlExtras.GL_BLEND);
        gl.DepthMask(1);
        gl.BindBuffer(GL_ARRAY_BUFFER, 0);
        gl.BindTexture(GL_TEXTURE_2D, 0);
    }

    void Pass(GlInterface gl, int framebuffer, int source, bool add, float tx, float ty, float tz, float tw, float px, float py, int mode)
    {
        gl.BindFramebuffer(GL_FRAMEBUFFER, framebuffer);
        gl.Viewport(0, 0, _smallWidth, _smallHeight);
        if (add)
        {
            gl.Enable(GlExtras.GL_BLEND);
            _ext.BlendFunc(1, 1);
        }
        else
        {
            gl.Disable(GlExtras.GL_BLEND);
        }
        gl.BindTexture(GL_TEXTURE_2D, source);
        _ext.Uniform4f(_uTexel, tx, ty, tz, tw);
        _ext.Uniform4f(_uParams, px, py, 0, mode);
        gl.DrawArrays(GL_TRIANGLES, 0, 3);
    }

    void DeleteTargets(GlInterface gl)
    {
        if (_sourceFramebuffer == 0)
            return;
        gl.DeleteFramebuffer(_sourceFramebuffer);
        gl.DeleteRenderbuffer(_sourceDepth);
        gl.DeleteTexture(_sourceTexture);
        for (int i = 0; i < _textures.Length; i++)
        {
            gl.DeleteFramebuffer(_framebuffers[i]);
            gl.DeleteTexture(_textures[i]);
        }
        _sourceFramebuffer = 0;
        _width = _height = 0;
    }

    public void Dispose(GlInterface gl)
    {
        DeleteTargets(gl);
        gl.DeleteBuffer(_triangle);
        gl.DeleteProgram(_program);
    }
}
