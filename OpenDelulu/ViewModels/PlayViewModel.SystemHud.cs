using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Engine.Hud;

namespace OpenDelulu.ViewModels;

/// <summary>
/// The original system menu bar (ds01_01) across the top of the screen: Data (back to the title, to the ticket
/// counter, end the game), Config (the CONFIG screen's System / Game / H pages), Status (intimacy, items), Camera
/// (shows or hides the camera menu), the scene's menu (label ds01_01_05_NN: Action, Oil, Action, BJ) and × which slides
/// the bar away (メニュー閉じる). Pointing at the top edge of the view brings it back.
/// </summary>
public sealed partial class PlayViewModel
{
    /// <summary>Questions of the confirmation box (st00_03_NN).</summary>
    public const int QuestionTitle = 1, QuestionTicket = 2, QuestionEndGame = 3;

    bool _systemHudLoadFailed, _systemBarOpen;
    int _systemDrop;

    [ObservableProperty] public partial DsHud? SystemHud { get; private set; }

    /// <summary>The CONFIG screen should open on a page (0 System, 1 Game, 2 H).</summary>
    public event Action<int>? ConfigRequested;

    /// <summary>"End the game" was confirmed.</summary>
    public event Action? ExitRequested;

    DsHud? EnsureSystemHud()
    {
        if (SystemHud is not null || _systemHudLoadFailed)
            return SystemHud;
        try
        {
            var hud = new DsHud(DsMenu.Load(_fs, "ds01_01_00_00_00"), 1024, 96)
            {
                State = () => !_systemBarOpen ? "メニュー閉じる"
                    : _systemDrop switch { 1 => "データ表示", 2 => "コンフィグ表示", 3 => "ステータス表示", _ => "メニュー表示" },
                OwnerHandlesClose = true,
                ImageFor = key => key == "Menu05" ? Ui.Get("sb3_0300.pp", $"ds01_01_05_{(_oil is not null ? 2 : 1):00}_00.bmp") : null,
            };
            hud.Pressed += PressSystemHud;
            SystemHud = hud;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException)
        {
            _systemHudLoadFailed = true;
        }
        return SystemHud;
    }

    /// <summary>Puts the (folded) bar on screen during dates and oil.</summary>
    void ShowSystemHud()
    {
        if (EnsureSystemHud() is not { } hud)
            return;
        if (hud.IsVisible)
        {
            hud.Refresh();
            return;
        }
        _systemBarOpen = false;
        _systemDrop = 0;
        hud.Show();
    }

    void HideSystemHud() => SystemHud?.Hide();

    /// <summary>The pointer reached the top edge of the view: the bar slides in.</summary>
    public void PointAtTopEdge()
    {
        if (SystemHud is not { IsVisible: true } hud || _systemBarOpen)
            return;
        _systemBarOpen = true;
        _systemDrop = 0;
        hud.Play(instant: false);
    }

    void CloseSystemDrop()
    {
        _systemDrop = 0;
        SystemHud?.Play(instant: false);
    }

    void PressSystemHud(string key)
    {
        if (SystemHud is not { } hud || IsBusy)
            return;
        switch (key)
        {
            case "Close":
                _systemBarOpen = false;
                _systemDrop = 0;
                hud.Play(instant: false);
                return;
            case "Menu01" or "Menu02" or "Menu03":
                int drop = key[^1] - '0';
                _systemDrop = _systemDrop == drop ? 0 : drop;
                hud.Play(instant: false);
                return;
            case "Menu04":
                CloseSystemDrop();
                CameraHud?.ToggleClosed();
                OnPropertyChanged(nameof(IsHudClosed));
                return;
            case "Menu05":
                CloseSystemDrop();
                MainHud?.ToggleClosed();
                OnPropertyChanged(nameof(IsHudClosed));
                return;
            case "Sistem" or "Game" or "H":
                CloseSystemDrop();
                ConfigRequested?.Invoke(key switch { "Sistem" => 0, "Game" => 1, _ => 2 });
                return;
            case "Sinmitu":
                // The intimacy status panel (57_status) on and off.
                CloseSystemDrop();
                IsStatusPanelShown = !IsStatusPanelShown;
                return;
            case "Item":
                // A toggle hit (type 0x10, id 32000) like Sinmitu: the date item she holds shown or hidden; the counts of
                // what is unlocked are told too.
                CloseSystemDrop();
                IsItemShown = !IsItemShown;
                if (Scene is { } scene && _propIndex >= 0)
                    scene.SetObjectVisible(_propIndex, IsItemShown);
                ShowStatus(F("play.items", _progress.Swimsuits?.Count ?? _girl?.Girl.Swimsuits.Count ?? 0,
                    _progress.Accessories?.Count ?? 0), seconds: 4);
                return;
            case "Title" or "Ticket":
                // No ticket counter yet: it goes back to choosing a girl.
                CloseSystemDrop();
                if (!IsConfirming)
                    Confirm(key == "Title" ? QuestionTitle : QuestionTicket, key == "Title" ? "confirm.title" : "confirm.ticket",
                        () =>
                        {
                            Leave();
                            if (key == "Title")
                                ShowTitle();
                        }, ReturnToScene);
                return;
            case "End":
                CloseSystemDrop();
                if (!IsConfirming)
                    Confirm(QuestionEndGame, "confirm.endGame", () => ExitRequested?.Invoke(), ReturnToScene);
                return;
        }
    }

    /// <summary>Back to whatever menu the scene was showing after a declined question.</summary>
    void ReturnToScene()
    {
        if (_oil is not null)
            ShowOilMenu();
        else if (InGame)
            ShowMenu();
    }
}
