using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Engine.Hud;

namespace OpenDelulu.ViewModels;

/// <summary>
/// The original date menu (ds01_03): change clothes (m03_kigae), oil (m03_oil), H (m03_h) and end the date (m00_yameru),
/// with the same handle / minimise / close bar as the other ds menus. Talking, the date activities and moving stay in
/// the text menu (the original does them in the scene itself).
/// </summary>
public sealed partial class PlayViewModel
{
    bool _dateHudLoadFailed;

    [ObservableProperty] public partial DsHud? DateHud { get; private set; }

    /// <summary>Loads ds01_03 the first time the date menu is needed.</summary>
    DsHud? EnsureDateHud()
    {
        if (DateHud is not null || _dateHudLoadFailed)
            return DateHud;
        try
        {
            var hud = new DsHud(DsMenu.Load(_fs, "ds01_03_00_00_00"), 96, 24 + 4 * 96);
            hud.Pressed += PressDateHud;
            hud.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DsHud.IsClosed))
                    OnPropertyChanged(nameof(IsHudClosed));
            };
            DateHud = hud;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException)
        {
            _dateHudLoadFailed = true;
        }
        return DateHud;
    }

    /// <summary>Hides the date menu and the camera menu (conversations, other screens, leaving).</summary>
    void HideDateHud()
    {
        DateHud?.Hide();
        CameraHud?.Hide();
    }

    void PressDateHud(string key)
    {
        if (_girl is null || _oil is not null || IsBusy || Dialogue.CanAdvance)
            return;
        switch (key)
        {
            case "DIcon01":
                HideDateHud();
                ShowSwimsuits();
                break;
            case "DIcon02":
                // The oil menu has the pose list; start standing like the original.
                if (_girl.Girl.OilPoses.Count > 0)
                {
                    HideDateHud();
                    _ = StartOilAsync(_girl.Girl.OilPoses[0]);
                }
                break;
            case "DIcon03":
                if (CanLeadIn)
                {
                    HideDateHud();
                    LeadIn();
                }
                else
                    ShowStatus(T("play.noLeadIn"), seconds: 3);
                break;
            case "DIcon04":
                Goodbye();
                break;
        }
    }
}
