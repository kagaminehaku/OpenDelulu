using Avalonia.OpenGL;

namespace OpenDelulu.Rendering;

/// <summary>GL entry points that Avalonia's <see cref="GlInterface"/> does not expose.</summary>
internal sealed unsafe class GlExtras
{
    public const int GL_REPEAT = 0x2901;
    public const int GL_LINEAR_MIPMAP_LINEAR = 0x2703;
    public const int GL_BLEND = 0x0BE2;
    public const int GL_SRC_ALPHA = 0x0302;
    public const int GL_ONE_MINUS_SRC_ALPHA = 0x0303;
    public const int GL_LEQUAL = 0x0203;

    readonly delegate* unmanaged[Stdcall]<int, float, float, float, float, void> _uniform4f;
    readonly delegate* unmanaged[Stdcall]<int, float, float, float, void> _uniform3f;
    readonly delegate* unmanaged[Stdcall]<int, int, void> _blendFunc;
    readonly delegate* unmanaged[Stdcall]<int, void> _generateMipmap;

    public GlExtras(GlInterface gl)
    {
        _uniform4f = (delegate* unmanaged[Stdcall]<int, float, float, float, float, void>)gl.GetProcAddress("glUniform4f");
        _uniform3f = (delegate* unmanaged[Stdcall]<int, float, float, float, void>)gl.GetProcAddress("glUniform3f");
        _blendFunc = (delegate* unmanaged[Stdcall]<int, int, void>)gl.GetProcAddress("glBlendFunc");
        _generateMipmap = (delegate* unmanaged[Stdcall]<int, void>)gl.GetProcAddress("glGenerateMipmap");
        if (_uniform4f == null || _uniform3f == null || _blendFunc == null)
            throw new System.NotSupportedException("Required GL functions are missing.");
    }

    public bool CanGenerateMipmap => _generateMipmap != null;

    public void Uniform4f(int location, float x, float y, float z, float w) => _uniform4f(location, x, y, z, w);

    public void Uniform3f(int location, float x, float y, float z) => _uniform3f(location, x, y, z);

    public void BlendFunc(int src, int dst) => _blendFunc(src, dst);

    public void GenerateMipmap(int target)
    {
        if (_generateMipmap != null)
            _generateMipmap(target);
    }
}
