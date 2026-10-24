using System;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenDelulu.Engine.Hud;
using OpenDelulu.Engine.Oil;

namespace OpenDelulu.ViewModels;

/// <summary>
/// The original oil menu (ds01_04): four 96×96 icons: oil (the chosen one of 7: sun block, three tan oils, three
/// whiteners), tool (bottle, hand, brush, sponge), pose (stand, sit, lie down, all fours) and Exit. An icon unfolds its
/// list (clips オイル/アイテム/ポーズ表示). Labels and icons come from ds01_04_GG_II_01/_02 in sb3_0300.pp; the chosen
/// entry of each list gets the m00_check mark.
/// </summary>
public sealed partial class PlayViewModel
{
    static readonly string[] OilHudOils = ["Cream", "Oil01", "Oil02", "Oil03", "Cream01", "Cream02", "Cream03"];
    static readonly string[] OilHudTools = ["Bin", "Hand", "Spo", "Fude"];
    static readonly string[] OilHudPoses = ["Tati", "Suwari", "Aomuke", "Utubuse"];
    /// <summary>
    /// Drip (ek01..03) per oil: the white ek01 for the sun block, the yellowish ek03 for the tan oils, the pinkish ek02
    /// for the whiteners. The exe's choice is not decoded; this goes by the drip textures' colours.
    /// </summary>
    static readonly int[] OilHudDrip = [0, 2, 2, 2, 1, 1, 1];

    bool _oilHudLoadFailed;
    int _oilHudGroup, _oilHudOil = 1, _oilHudTool = 1;

    /// <summary>The original oil menu, once loaded (null when the game lacks ds01_04).</summary>
    [ObservableProperty] public partial DsHud? OilHud { get; private set; }

    int OilHudPoseSlot => _oil is { } oil ? Math.Max(0, oil.Pose.Number - 1) : 0;

    /// <summary>Loads ds01_04 once (on the oil scene's loading thread).</summary>
    DsMenu? LoadOilHudMenu()
    {
        if (OilHud is not null || _oilHudLoadFailed)
            return null;
        try
        {
            return DsMenu.Load(_fs, "ds01_04_00_00_00");
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException)
        {
            _oilHudLoadFailed = true;
            return null;
        }
    }

    void CreateOilHud(DsMenu? menu)
    {
        if (menu is null || OilHud is not null)
            return;
        var hud = new DsHud(menu, 96 + 200, 24 + 4 * 96)
        {
            State = () => _oilHudGroup switch { 1 => "オイル表示", 2 => "アイテム表示", 3 => "ポーズ表示", _ => "シーン表示" },
            IsChecked = key => Array.IndexOf(OilHudOils, key) is var o and >= 0 ? o == _oilHudOil
                : Array.IndexOf(OilHudTools, key) is var t and >= 0 ? t == _oilHudTool
                : Array.IndexOf(OilHudPoses, key) is var p and >= 0 && p == OilHudPoseSlot,
            ImageFor = OilHudImage,
        };
        hud.Pressed += PressOilHud;
        hud.Move(0, 24); // below the system menu bar
        hud.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DsHud.IsClosed))
                OnPropertyChanged(nameof(IsHudClosed));
        };
        OilHud = hud;
    }

    Bitmap? DsItemTexture(string menu, int group, int slot, int kind) =>
        Ui.Get("sb3_0300.pp", $"{menu}_{group:00}_{slot + 1:00}_{kind:00}.bmp");

    Bitmap? OilHudImage(string key)
    {
        if (Array.IndexOf(OilHudOils, key) is var oil and >= 0)
            return DsItemTexture("ds01_04", 1, oil, 1);
        if (Array.IndexOf(OilHudTools, key) is var tool and >= 0)
            return DsItemTexture("ds01_04", 2, tool, 1);
        if (Array.IndexOf(OilHudPoses, key) is var pose and >= 0)
            return DsItemTexture("ds01_04", 3, pose, 1);
        return key switch
        {
            "OilIcon01" => DsItemTexture("ds01_04", 1, _oilHudOil, 2),
            "OilIcon02" => DsItemTexture("ds01_04", 2, _oilHudTool, 2),
            "OilIcon03" => DsItemTexture("ds01_04", 3, OilHudPoseSlot, 2),
            _ => null,
        };
    }

    void ShowOilHud()
    {
        if (OilHud is not { } hud || _oil is null)
            return;
        _oil.Mode = _oilHudTool == 0 ? OilMode.Pour : OilMode.Hand;
        if (_oil.OilKinds > 0)
            _oil.OilKind = Math.Min(OilHudDrip[_oilHudOil], _oil.OilKinds - 1);
        _oilHudGroup = 0;
        hud.Show();
        ShowCameraHud();
        ShowSystemHud();
    }

    void HideOilHud() => OilHud?.Hide();

    void TickOilHud(double seconds) => OilHud?.Tick(seconds);

    void PressOilHud(string key)
    {
        if (_oil is null || IsBusy || OilHud is not { } hud)
            return;
        switch (key)
        {
            case "OilIcon01" or "OilIcon02" or "OilIcon03":
                int group = key[^1] - '0';
                _oilHudGroup = _oilHudGroup == group ? 0 : group;
                hud.Play(instant: false);
                return;
            case "OilIcon04":
                Confirm(QuestionEndOil, "confirm.endOil", () =>
                {
                    StopOil(restore: true);
                    ShowMenu();
                }, ShowOilMenu);
                return;
        }
        if (Array.IndexOf(OilHudOils, key) is var oil and >= 0)
            ChooseHudOil(oil);
        else if (Array.IndexOf(OilHudTools, key) is var tool and >= 0)
            ChooseHudTool(tool);
        else if (Array.IndexOf(OilHudPoses, key) is var pose and >= 0)
        {
            _oilHudGroup = 0;
            hud.Play(instant: false);
            if (_girl?.Girl.OilPoses.FirstOrDefault(p => p.Number == pose + 1) is { } chosen && chosen.Number != _oil.Pose.Number)
                _ = StartOilAsync(chosen);
        }
    }

    void ChooseHudOil(int slot)
    {
        _oilHudOil = (slot + OilHudOils.Length) % OilHudOils.Length;
        if (_oil is { OilKinds: > 0 } oil)
            oil.OilKind = Math.Min(OilHudDrip[_oilHudOil], oil.OilKinds - 1);
        _oilHudGroup = 0;
        OilHud?.Play(instant: false);
    }

    /// <summary>The bottle pours, the others rub (brush and sponge have no model of their own; they rub like the hand).</summary>
    void ChooseHudTool(int slot)
    {
        _oilHudTool = (slot + OilHudTools.Length) % OilHudTools.Length;
        if (_oil is { } oil)
            oil.Mode = _oilHudTool == 0 ? OilMode.Pour : OilMode.Hand;
        _oilHudGroup = 0;
        OilHud?.Play(instant: false);
    }

    /// <summary>A menu closed with its × (the side panel offers it again).</summary>
    public bool IsHudClosed => OilHud is { IsClosed: true } || HHud is { IsClosed: true } || DateHud is { IsClosed: true }
        || CameraHud is { IsClosed: true };

    /// <summary>The menu of the current scene: oil, H or the date's.</summary>
    DsHud? MainHud => _oil is not null ? OilHud : _h is not null ? HHud : InGame ? DateHud : null;

    /// <summary>Tab, or the side panel button: brings closed menus back, or else hides the current ones.</summary>
    [RelayCommand]
    public void ToggleHud()
    {
        var main = MainHud;
        if (IsHudClosed)
        {
            foreach (var hud in new[] { main, CameraHud })
                if (hud is { IsClosed: true })
                    hud.Reopen();
        }
        else
        {
            main?.ToggleClosed();
            CameraHud?.ToggleClosed();
        }
        OnPropertyChanged(nameof(IsHudClosed));
    }
}
