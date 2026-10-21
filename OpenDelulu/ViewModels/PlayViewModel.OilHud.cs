using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenDelulu.Engine.Hud;
using OpenDelulu.Engine.Oil;
using OpenDelulu.Services;

namespace OpenDelulu.ViewModels;

/// <summary>One quad of an original ds menu as drawn: picture, hover fill, place on the 1024×768 screen.</summary>
public sealed partial class HudQuad(string frame) : ObservableObject
{
    public string Frame { get; } = frame;
    [ObservableProperty] public partial Bitmap? Image { get; set; }
    [ObservableProperty] public partial Bitmap? HoverImage { get; set; }
    [ObservableProperty] public partial double X { get; set; }
    [ObservableProperty] public partial double Y { get; set; }
    [ObservableProperty] public partial double W { get; set; }
    [ObservableProperty] public partial double H { get; set; }
    [ObservableProperty] public partial bool IsVisible { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoverOpacity))]
    public partial bool IsHover { get; set; }

    /// <summary>The light blue m00_sentaku fill over the hovered quad.</summary>
    public double HoverOpacity => IsHover && HoverImage is not null ? 0.5 : 0;

    /// <summary>Check marks and other decorations do not take the pointer.</summary>
    public bool IsHitTestable { get; init; } = true;
}

/// <summary>
/// The original oil menu (ds01_04): move handle, minimise and close buttons, then four 96×96 icons: oil (the chosen
/// one of 7: sun block, three tan oils, three whiteners), tool (bottle, hand, brush, sponge), pose (stand, sit, lie
/// down, all fours) and Exit. An icon unfolds its list (clips オイル/アイテム/ポーズ表示); the list opens right/left
/// and down/up (clip variants A..D) depending on where the menu was dragged. Labels and icons come from
/// ds01_04_GG_II_01/_02 in sb3_0300.pp; the chosen entry of each list gets the m00_check mark.
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
    const double OilHudWidth = 96 + 200, OilHudHeight = 24 + 4 * 96;

    DsMenu? _oilHud;
    bool _oilHudLoadFailed;
    int _oilHudGroup, _oilHudOil = 1, _oilHudTool = 1;
    bool _oilHudMinimized;
    char _oilHudVariant = 'A';
    readonly Dictionary<string, HudQuad> _oilHudQuads = [];

    public ObservableCollection<HudQuad> OilHudQuads { get; } = [];

    /// <summary>The menu is shown (an oil scene runs, the original menu loaded and it was not closed).</summary>
    [ObservableProperty] public partial bool IsOilHudVisible { get; set; }

    /// <summary>Closed with its × button; Tab or the side panel button brings it back.</summary>
    [ObservableProperty] public partial bool IsOilHudClosed { get; set; }

    /// <summary>Top-left of the menu on the 1024×768 screen (moved with the handle).</summary>
    public double OilHudX { get; private set; }
    public double OilHudY { get; private set; }

    int OilHudPoseSlot => _oil is { } oil ? Math.Max(0, oil.Pose.Number - 1) : 0;

    /// <summary>Loads ds01_04 once (on the oil scene's loading thread); false when the game lacks it.</summary>
    bool PrepareOilHud()
    {
        if (_oilHud is not null || _oilHudLoadFailed)
            return _oilHud is not null;
        try
        {
            _oilHud = DsMenu.Load(_fs, "ds01_04_00_00_00");
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException)
        {
            _oilHudLoadFailed = true;
        }
        return _oilHud is not null;
    }

    void ShowOilHud()
    {
        if (_oilHud is not { } menu || _oil is null)
            return;
        if (_oilHudQuads.Count == 0)
        {
            foreach (var quad in menu.Quads)
            {
                var item = new HudQuad(quad.Frame) { IsHitTestable = !quad.Frame.EndsWith("_CK", StringComparison.Ordinal) };
                _oilHudQuads[quad.Frame] = item;
                OilHudQuads.Add(item);
            }
        }
        _oil.Mode = _oilHudTool == 0 ? OilMode.Pour : OilMode.Hand;
        if (_oil.OilKinds > 0)
            _oil.OilKind = Math.Min(OilHudDrip[_oilHudOil], _oil.OilKinds - 1);
        _oilHudGroup = 0;
        PlayOilHudClip(instant: true);
        IsOilHudVisible = !IsOilHudClosed;
    }

    void HideOilHud()
    {
        IsOilHudVisible = false;
        IsOilHudClosed = false;
    }

    void PlayOilHudClip(bool instant)
    {
        if (_oilHud is not { } menu)
            return;
        string state = _oilHudMinimized ? "シーン最小化" : _oilHudGroup switch
        {
            1 => "オイル表示",
            2 => "アイテム表示",
            3 => "ポーズ表示",
            _ => "シーン表示",
        };
        menu.Play(state + _oilHudVariant, instant);
        RefreshOilHud();
    }

    /// <summary>Called by the oil timer: plays the folding animation on.</summary>
    void TickOilHud(double seconds)
    {
        if (_oilHud is { IsAnimating: true } menu && IsOilHudVisible)
        {
            menu.Advance(seconds);
            RefreshOilHud();
        }
    }

    readonly Dictionary<string, Bitmap?> _hudBitmaps = [];

    Bitmap? HudTexture(string name)
    {
        if (_hudBitmaps.TryGetValue(name, out var cached))
            return cached;
        Bitmap? bitmap = null;
        try
        {
            if (_oilHud?.Texture(name) is { } image)
                bitmap = UiTextures.ToBitmap(image);
        }
        catch (InvalidOperationException)
        {
            // No bitmap support (headless view-model tests): the menu still works, without pictures.
        }
        return _hudBitmaps[name] = bitmap;
    }

    Bitmap? HudItemTexture(int group, int slot, int kind) => Ui.Get("sb3_0300.pp", $"ds01_04_{group:00}_{slot + 1:00}_{kind:00}.bmp");

    void RefreshOilHud()
    {
        if (_oilHud is not { } menu)
            return;
        foreach (var layout in menu.Layout())
        {
            if (!_oilHudQuads.TryGetValue(layout.Quad.Frame, out var item))
                continue;
            item.X = OilHudX + layout.X;
            item.Y = OilHudY + layout.Y;
            item.W = layout.Width;
            item.H = layout.Height;
            string name = layout.Quad.Frame.StartsWith("o01_P_", StringComparison.Ordinal) ? layout.Quad.Frame[6..] : layout.Quad.Frame;
            bool check = name.EndsWith("_CK", StringComparison.Ordinal);
            string key = check ? name[..^3] : name;
            var textures = layout.Quad.Textures;
            string? hover = textures.Count > 1 && textures[1].Length > 0 ? textures[1] : null;
            item.HoverImage ??= hover is null ? null : HudTexture(hover);

            int oil = Array.IndexOf(OilHudOils, key), tool = Array.IndexOf(OilHudTools, key), pose = Array.IndexOf(OilHudPoses, key);
            (int Group, int Slot, int Chosen) entry = oil >= 0 ? (1, oil, _oilHudOil) : tool >= 0 ? (2, tool, _oilHudTool)
                : pose >= 0 ? (3, pose, OilHudPoseSlot) : (0, -1, -1);
            if (check)
            {
                item.Image ??= textures.Count > 0 ? HudTexture(textures[0]) : null;
                item.IsVisible = layout.IsVisible && entry.Slot >= 0 && entry.Slot == entry.Chosen;
                continue;
            }
            item.Image = key switch
            {
                _ when entry.Group > 0 => item.Image ?? HudItemTexture(entry.Group, entry.Slot, 1),
                "OilIcon01" => HudItemTexture(1, _oilHudOil, 2),
                "OilIcon02" => HudItemTexture(2, _oilHudTool, 2),
                "OilIcon03" => HudItemTexture(3, OilHudPoseSlot, 2),
                // OilMin shows "minimise" (m00_min) or, when minimised, "restore" (m00_max, its third texture).
                "OilMin" => HudTexture(_oilHudMinimized && textures.Count > 2 ? textures[2] : textures[0]),
                _ => item.Image ?? (textures.Count > 0 ? HudTexture(textures[0]) : null),
            };
            item.IsVisible = layout.IsVisible;
        }
    }

    /// <summary>A click on a quad of the oil menu.</summary>
    public void PressOilHud(HudQuad quad)
    {
        if (_oil is null || IsBusy)
            return;
        string name = quad.Frame.StartsWith("o01_P_", StringComparison.Ordinal) ? quad.Frame[6..] : quad.Frame;
        switch (name)
        {
            case "OilMin":
                _oilHudMinimized = !_oilHudMinimized;
                _oilHudGroup = 0;
                PlayOilHudClip(instant: false);
                return;
            case "OilClose":
                IsOilHudClosed = true;
                IsOilHudVisible = false;
                return;
            case "OilIcon01" or "OilIcon02" or "OilIcon03":
                int group = name[^1] - '0';
                _oilHudGroup = _oilHudGroup == group ? 0 : group;
                PlayOilHudClip(instant: false);
                return;
            case "OilIcon04":
                Confirm(QuestionEndOil, "confirm.endOil", () =>
                {
                    StopOil(restore: true);
                    ShowMenu();
                }, ShowOilMenu);
                return;
        }
        if (Array.IndexOf(OilHudOils, name) is var oil and >= 0)
            ChooseHudOil(oil);
        else if (Array.IndexOf(OilHudTools, name) is var tool and >= 0)
            ChooseHudTool(tool);
        else if (Array.IndexOf(OilHudPoses, name) is var pose and >= 0)
        {
            _oilHudGroup = 0;
            PlayOilHudClip(instant: false);
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
        PlayOilHudClip(instant: false);
    }

    /// <summary>The bottle pours, the others rub (brush and sponge have no model of their own; they rub like the hand).</summary>
    void ChooseHudTool(int slot)
    {
        _oilHudTool = (slot + OilHudTools.Length) % OilHudTools.Length;
        if (_oil is { } oil)
            oil.Mode = _oilHudTool == 0 ? OilMode.Pour : OilMode.Hand;
        _oilHudGroup = 0;
        PlayOilHudClip(instant: false);
    }

    /// <summary>Drags the menu by its handle (a delta on the 1024×768 screen); the list direction follows the place.</summary>
    public void MoveOilHud(double dx, double dy)
    {
        OilHudX = Math.Clamp(OilHudX + dx, 0, 1024 - 96);
        OilHudY = Math.Clamp(OilHudY + dy, 0, 768 - 24);
        bool right = OilHudX + OilHudWidth <= 1024, down = OilHudY + OilHudHeight / 2 <= 384;
        char variant = (right, down) switch { (true, true) => 'A', (false, true) => 'B', (true, false) => 'C', _ => 'D' };
        if (variant != _oilHudVariant)
        {
            _oilHudVariant = variant;
            PlayOilHudClip(instant: true);
        }
        else
            RefreshOilHud();
    }

    /// <summary>Tab, or the side panel button, after the × closed it.</summary>
    [RelayCommand]
    public void ReopenOilHud()
    {
        if (_oilHud is null || _oil is null)
            return;
        IsOilHudClosed = false;
        IsOilHudVisible = true;
        RefreshOilHud();
    }
}
