using System;
using System.IO;
using Avalonia.Threading;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Engine.Game;
using OpenDelulu.Formats.Imaging;

namespace OpenDelulu.ViewModels;

/// <summary>
/// Screenshots like the exe (GameMainCtrl 0x4036B0, asked by the scenes' flag +0xA18D with the shutter se07): the 3D view
/// saved as data/cap/YYYY_MMDD_HHMM_SSmmm.bmp of the edition whose saves are used (F12 or Print Screen). Only the Vulkan
/// view can read its frame back.
/// </summary>
public sealed partial class PlayViewModel
{
    public void Capture()
    {
        if (Scene is not { } scene)
            return;
        scene.CaptureRequest = (pixels, width, height) =>
        {
            string? folder = OriginalSaves.SaveFolder(_fs.Install) is { } saves ? Path.Combine(Path.GetDirectoryName(saves)!, "cap") : null;
            string? saved = null, error = null;
            try
            {
                if (folder is not null)
                {
                    Directory.CreateDirectory(folder);
                    var now = DateTime.Now;
                    saved = Path.Combine(folder, $"{now:yyyy}_{now:MMdd}_{now:HHmm}_{now:ss}{now:fff}.bmp");
                    File.WriteAllBytes(saved, BmpWriter.Encode(width, height, pixels));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
            }
            Dispatcher.UIThread.Post(() =>
            {
                Sfx(7);
                ShowStatus(saved is not null ? F("play.captured", Path.GetFileName(saved)) : F("play.captureError", error ?? ""), seconds: 3);
            });
        };
        scene.NotifyChanged();
    }
}
