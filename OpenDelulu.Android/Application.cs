using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Services;

namespace OpenDelulu.Android
{
    [Application(Label = "Open Delulu")]
    public class Application : AvaloniaAndroidApplication<App>
    {
        protected Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        public override void OnCreate()
        {
            // App-specific external storage is readable without any permission and reachable over USB:
            // copy the "Sexy Beach 3" folder (with SexyBeach3/, SexyBeach3Plus/...) into it.
            var external = GetExternalFilesDir(null)?.AbsolutePath;
            if (external is not null)
            {
                AppHost.SuggestedGameFolder = System.IO.Path.Combine(external, "Sexy Beach 3");
                AppHost.StorageHintKey = "hint.android";
                AppHost.StorageHintArgs = [external];
            }
            AudioSystem.OutputFactory = () => new AndroidAudioOutput();
            base.OnCreate();
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            return base.CustomizeAppBuilder(builder)
            .WithInterFont();
        }
    }
}
