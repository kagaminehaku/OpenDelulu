using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Engine.Script;

namespace OpenDelulu.ViewModels;

/// <summary>A button of the lobby (248×72 at x 728).</summary>
public sealed record LobbyButton(string Key, Bitmap? Image, double Y);

/// <summary>
/// The hotel lobby (61_lobby): the front desk picture st08_01_01 and six buttons st08_01_NN at (728, 24 + 88·i):
/// Rest a While, Sleep, Save, Return to Title Screen, Go to Island, ??? (the front desk's intimacy change, c99_02_01 on the
/// Bonus Disc, else c99_02_00). The front desk greets on the first visit (c99_00_00), back from the island (c99_00_01)
/// and in the morning (c99_00_02); Save shows s99_00_00.
/// </summary>
public sealed partial class PlayViewModel
{
    static readonly (string Key, int Texture)[] LobbyLayout =
        [("Rest", 2), ("Sleep", 3), ("Save", 7), ("Title", 5), ("Island", 4), ("Intimacy", 6)];

    List<LobbyButton>? _lobbyButtons;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMap))]
    public partial bool IsLobbyVisible { get; set; }

    /// <summary>A front desk script is running (the buttons wait).</summary>
    [ObservableProperty] public partial bool IsLobbyTalking { get; set; }

    public bool HasOriginalLobby => Ui.Get("st08_01_01_00_00.bmp") is not null;
    public Bitmap? LobbyBackground => Ui.Get("st08_01_01_00_00.bmp");

    public IReadOnlyList<LobbyButton> LobbyButtons => _lobbyButtons ??= BuildLobbyButtons();

    List<LobbyButton> BuildLobbyButtons()
    {
        var buttons = new List<LobbyButton>();
        for (int i = 0; i < LobbyLayout.Length; i++)
            buttons.Add(new LobbyButton(LobbyLayout[i].Key, Ui.Get($"st08_01_{LobbyLayout[i].Texture:00}_00_00.tga"), 24 + 88 * i));
        return buttons;
    }

    /// <summary>Shows the lobby with the front desk's greeting (welcome on the first visit, else "welcome back").</summary>
    public void ShowLobby(bool backFromIsland = false)
    {
        if (!HasOriginalLobby || InGame)
            return;
        HideTitle();
        IsLobbyVisible = true;
        if (!_progress.LobbyVisited)
        {
            _progress.LobbyVisited = true;
            SaveProgress();
            RunLobbyScript("c99_00_00");
        }
        else if (backFromIsland)
            RunLobbyScript("c99_00_01");
        else
            Dialogue.Hide();
    }

    void RunLobbyScript(string name, Action? then = null, Func<int, int>? query = null)
    {
        if (_library.Find(name) is not { } script)
        {
            then?.Invoke();
            return;
        }
        IsLobbyTalking = true;
        Dialogue.Run(script, () =>
        {
            IsLobbyTalking = false;
            Dialogue.Hide();
            then?.Invoke();
        }, query);
    }

    /// <summary>Back to the lobby from the island map (side panel button).</summary>
    [RelayCommand]
    void BackToLobby() => ShowLobby();

    public void PressLobby(LobbyButton button)
    {
        if (IsLobbyTalking || IsBusy || IsConfirming)
            return;
        Sfx(SoundEffects.Decide);
        switch (button.Key)
        {
            case "Rest":
                // An hour or two pass: the next time of day (late night needs a sleep).
                if (SelectedTimeIndex < Times.Length - 1)
                    SelectedTimeIndex++;
                Status = F("lobby.time", Times[SelectedTimeIndex]);
                break;
            case "Sleep":
                SelectedTimeIndex = 0;
                _progress.Day++;
                Status = F("lobby.time", Times[SelectedTimeIndex]);
                RunLobbyScript("c99_00_02");
                break;
            case "Save":
                SaveProgress();
                RunLobbyScript("s99_00_00");
                break;
            case "Title":
                Confirm(QuestionTitle, "confirm.title", () =>
                {
                    IsLobbyVisible = false;
                    ShowTitle();
                });
                break;
            case "Island":
                IsLobbyVisible = false;
                break;
            case "Intimacy":
                string name = _library.Find("c99_02_01") is not null ? "c99_02_01" : "c99_02_00";
                RunLobbyScript(name, ApplyLobbyIntimacy, girl => _progress.Girls.TryGetValue(girl, out var p) ? p.Intimacy : 1);
                break;
        }
    }

    /// <summary>After c99_02: variable 20 is the girl, 30 her new level (0 = cancelled).</summary>
    void ApplyLobbyIntimacy()
    {
        var vars = Dialogue.LastVariables;
        if (!vars.TryGetValue(20, out int girl) || !vars.TryGetValue(30, out int level) || girl <= 0 || level is < 1 or > 5)
            return;
        var progress = _progress.For(girl);
        progress.Intimacy = level;
        progress.MetAtLevel = false;
        SaveProgress();
        RefreshGirls(Girls.ConvertAll(g => g.Id));
    }
}
