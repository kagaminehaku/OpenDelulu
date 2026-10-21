using System;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Engine.Hud;
using OpenDelulu.Rendering;

namespace OpenDelulu.ViewModels;

/// <summary>
/// The original camera menu (ds01_02): the "point" icon unfolds Reset / Face / Chest / Crotch / Behind (labels
/// ds01_02_01_01..05_01: Reset, Face, Bust, Waist, Rear = keys Q W E R T), the "auto" icon (ds01_02_02_02 on, green / _01 off, grey) lets the scene move
/// the camera by itself (the face-to-face view while talking). Shown during dates and oil, top right by default.
/// </summary>
public sealed partial class PlayViewModel
{
    static readonly string[] CameraHudPoints = ["Reset", "Kao", "Mune", "Kokan", "Siri"];
    static readonly CameraView?[] CameraHudViews = [null, CameraView.Face, CameraView.Chest, CameraView.Crotch, CameraView.Behind];

    bool _cameraHudLoadFailed, _cameraHudOpen;
    int _cameraHudPoint;

    [ObservableProperty] public partial DsHud? CameraHud { get; private set; }

    /// <summary>The scene may move the camera on its own (conversation close-ups).</summary>
    public bool AutoCamera { get; private set; } = true;

    DsHud? EnsureCameraHud()
    {
        if (CameraHud is not null || _cameraHudLoadFailed)
            return CameraHud;
        try
        {
            var hud = new DsHud(DsMenu.Load(_fs, "ds01_02_00_00_00"), 96 + 200, 24 + 2 * 96)
            {
                State = () => _cameraHudOpen ? "ポイント表示" : "カメラ表示",
                MinimizedState = "カメラ最小化",
                IsChecked = key => Array.IndexOf(CameraHudPoints, key) == _cameraHudPoint,
                ImageFor = CameraHudImage,
            };
            hud.Pressed += PressCameraHud;
            hud.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DsHud.IsClosed))
                    OnPropertyChanged(nameof(IsHudClosed));
            };
            hud.Move(1024 - 96, 0);
            CameraHud = hud;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException)
        {
            _cameraHudLoadFailed = true;
        }
        return CameraHud;
    }

    Bitmap? CameraHudImage(string key) => Array.IndexOf(CameraHudPoints, key) is var point and >= 0
        ? DsItemTexture("ds01_02", 1, point, 1)
        : key == "CameraIcon02" ? Ui.Get("sb3_0300.pp", $"ds01_02_02_{(AutoCamera ? 2 : 1):00}_00.bmp") : null;

    void ShowCameraHud() => EnsureCameraHud()?.Show();

    void PressCameraHud(string key)
    {
        if (CameraHud is not { } hud)
            return;
        switch (key)
        {
            case "CameraIcon01":
                _cameraHudOpen = !_cameraHudOpen;
                hud.Play(instant: false);
                return;
            case "CameraIcon02":
                AutoCamera = !AutoCamera;
                hud.Refresh();
                return;
        }
        if (Array.IndexOf(CameraHudPoints, key) is var point and >= 0)
        {
            _cameraHudPoint = point;
            _cameraHudOpen = false;
            hud.Play(instant: false);
            Scene?.RequestCamera(CameraHudViews[point]);
        }
    }
}
