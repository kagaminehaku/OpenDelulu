using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Services;

namespace OpenDelulu.Android;

[Activity(
    Label = "Open Delulu",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    // The game's screens are 1024x768: landscape (either way round) on a phone.
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    /// <summary>
    /// The desktop's --mute and --no-movies as launch extras (adb shell am start ... --ez mute true --ez nomovies true),
    /// for automated runs: muted, no sound device is opened at all.
    /// </summary>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        if (Intent?.GetBooleanExtra("mute", false) == true)
        {
            AppHost.StartupMute = true;
            AudioSystem.OutputFactory = () => new NullAudioOutput();
        }
        if (Intent?.GetBooleanExtra("nomovies", false) == true)
            AppHost.PlayMovies = false;
        base.OnCreate(savedInstanceState);
    }
}
