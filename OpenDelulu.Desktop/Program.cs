using System;
using Avalonia;
using OpenDelulu.Services;

namespace OpenDelulu.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        ParseArguments(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>--game &lt;folder&gt;   --character &lt;id&gt;   --swimsuit &lt;row&gt;   --open &lt;archive.pp&gt; &lt;file&gt;</summary>
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
            else if (args[i] == "--open" && i + 2 < args.Length)
                AppHost.StartupFile = (args[++i], args[++i]);
        }
    }
}
