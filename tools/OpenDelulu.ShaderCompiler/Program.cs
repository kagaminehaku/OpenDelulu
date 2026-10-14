using Silk.NET.Shaderc;

// Usage: dotnet run --project tools/OpenDelulu.ShaderCompiler -- OpenDelulu/Rendering/Vulkan/Shaders
// Compiles every *.vert / *.frag in the folder to *.vert.spv / *.frag.spv.
string folder = args.Length > 0 ? args[0] : "OpenDelulu/Rendering/Vulkan/Shaders";
var shaderc = Shaderc.GetApi();
unsafe
{
    var compiler = shaderc.CompilerInitialize();
    var options = shaderc.CompileOptionsInitialize();
    shaderc.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);
    shaderc.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan10);

    int failures = 0;
    foreach (var path in Directory.EnumerateFiles(folder).Where(p => p.EndsWith(".vert") || p.EndsWith(".frag")))
    {
        string source = File.ReadAllText(path);
        var kind = path.EndsWith(".vert") ? ShaderKind.VertexShader : ShaderKind.FragmentShader;
        var result = shaderc.CompileIntoSpv(compiler, source, (nuint)System.Text.Encoding.UTF8.GetByteCount(source),
            kind, Path.GetFileName(path), "main", options);
        if (shaderc.ResultGetCompilationStatus(result) != CompilationStatus.Success)
        {
            Console.Error.WriteLine($"{path}: {shaderc.ResultGetErrorMessageS(result)}");
            failures++;
        }
        else
        {
            var bytes = new ReadOnlySpan<byte>(shaderc.ResultGetBytes(result), (int)shaderc.ResultGetLength(result));
            File.WriteAllBytes(path + ".spv", bytes.ToArray());
            Console.WriteLine($"{path} -> {path}.spv ({bytes.Length} bytes)");
        }
        shaderc.ResultRelease(result);
    }
    shaderc.CompileOptionsRelease(options);
    shaderc.CompilerRelease(compiler);
    return failures;
}
