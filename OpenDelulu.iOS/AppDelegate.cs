using System;
using System.IO;
using Foundation;
using UIKit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.iOS;
using Avalonia.Media;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Services;

namespace OpenDelulu.iOS;

// The UIApplicationDelegate for the application. This class is responsible for launching the
// User Interface of the application, as well as listening (and optionally responding) to
// application events from iOS.
[Register("AppDelegate")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711 // Identifiers should not have incorrect suffix
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // Documents is exposed in the Files app / Finder (UIFileSharingEnabled in Info.plist).
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        AppHost.SuggestedGameFolder = Path.Combine(documents, "Sexy Beach 3");
        AudioSystem.OutputFactory = () => new IosAudioOutput();
        AppHost.StorageHintKey = "hint.ios";

        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            // ModelViewer is an OpenGL ES control; keep Metal as fallback for the rest of the UI.
            .With(new iOSPlatformOptions { RenderingMode = [iOSRenderingMode.OpenGl, iOSRenderingMode.Metal] });
    }
}
