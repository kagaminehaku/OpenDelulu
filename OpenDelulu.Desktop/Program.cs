using System;
using Avalonia;
using OpenDelulu.Rendering;
using OpenDelulu.Services;

namespace OpenDelulu.Desktop;

sealed class Program
{
    static bool? _compositorOverride;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        ParseArguments(args);
        AppHost.SupportsVulkanCompositor = OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
        // "Pure Vulkan": when the user prefers the Vulkan backend, the whole window is composed with
        // Vulkan too, so 3D frames are shared as Vulkan images (no D3D11 texture in between).
        var preferred = RenderBackendSettings.Parse(AppHost.StartupBackend ?? AppSettings.Load().RenderBackend);
        RenderBackendSettings.VulkanCompositor = AppHost.SupportsVulkanCompositor
            && (_compositorOverride ?? preferred == RenderBackendKind.Vulkan);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
        if (RenderBackendSettings.VulkanCompositor)
        {
            // Falls back to the usual modes if Vulkan cannot start.
            builder = builder
                .With(new Win32PlatformOptions
                {
                    RenderingMode = [Win32RenderingMode.Vulkan, Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
                })
                .With(new X11PlatformOptions
                {
                    RenderingMode = [X11RenderingMode.Vulkan, X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software],
                });
        }
        return builder;
    }

    /// <summary>
    /// --game &lt;folder&gt;   --character &lt;id&gt;   --swimsuit &lt;row&gt;   --cut &lt;n&gt;
    /// --open &lt;archive.pp&gt; &lt;file&gt;   --backend auto|gl|vulkan   --compositor vulkan|default
    /// </summary>
    static void ParseArguments(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--game" && i + 1 < args.Length)
                AppHost.StartupGameFolder = args[++i];
            else if (args[i] == "--character" && i + 1 < args.Length && int.TryParse(args[i + 1], out int girl))
            {
                AppHost.StartupCharacter = girl;
                i++;
            }
            else if (args[i] == "--swimsuit" && i + 1 < args.Length && int.TryParse(args[i + 1], out int row))
            {
                AppHost.StartupSwimsuit = row;
                i++;
            }
            else if (args[i] == "--cut" && i + 1 < args.Length && int.TryParse(args[i + 1], out int cut))
            {
                AppHost.StartupCut = cut;
                i++;
            }
            else if (args[i] == "--backend" && i + 1 < args.Length)
                AppHost.StartupBackend = args[++i];
            else if (args[i] == "--zoom" && i + 1 < args.Length
                     && float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out float zoom))
            {
                AppHost.StartupZoom = zoom;
                i++;
            }
            else if (args[i] == "--compositor" && i + 1 < args.Length)
                _compositorOverride = args[++i].Equals("vulkan", StringComparison.OrdinalIgnoreCase);
            else if (args[i] == "--open" && i + 2 < args.Length)
                AppHost.StartupFile = (args[++i], args[++i]);
        }
    }
}
