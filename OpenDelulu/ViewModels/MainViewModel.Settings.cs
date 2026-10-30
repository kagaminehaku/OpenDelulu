using System;
using System.IO;
using System.Linq;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Services;

namespace OpenDelulu.ViewModels;

/// <summary>Sound settings (like the game's config: master, music, ambience, effects, voices) and keyboard shortcuts.</summary>
public partial class MainViewModel
{
    [ObservableProperty] public partial int MasterVolume { get; set; } = 100;
    [ObservableProperty] public partial int MusicVolume { get; set; } = 80;
    [ObservableProperty] public partial int AmbientVolume { get; set; } = 80;
    [ObservableProperty] public partial int EffectsVolume { get; set; } = 100;
    [ObservableProperty] public partial int VoiceVolume { get; set; } = 100;

    /// <summary>All sound off for now (M key); not saved.</summary>
    [ObservableProperty] public partial bool IsMuted { get; set; }

    /// <summary>The emergency screen (緊急回避, exe module 51_emergency): data/dummy.bmp over everything, sound off.</summary>
    [ObservableProperty] public partial bool IsEmergency { get; set; }
    [ObservableProperty] public partial Bitmap? EmergencyImage { get; set; }

    void LoadSoundSettings()
    {
        // One GameConfig object for the whole run (views bind to it); the saved values are copied in.
        GameConfig.Current.CopyFrom(_settings.Config);
        _settings.Config = GameConfig.Current;
        GameConfig.Current.PropertyChanged += (_, _) => ApplyVolumes();
        _initializing = true;
        MasterVolume = Percent(_settings.MasterVolume, 100);
        MusicVolume = Percent(_settings.MusicVolume, 80);
        AmbientVolume = Percent(_settings.AmbientVolume, 80);
        EffectsVolume = Percent(_settings.EffectsVolume, 100);
        VoiceVolume = Percent(_settings.VoiceVolume, 100);
        _initializing = false;
        ApplyVolumes();
    }

    static int Percent(int? value, int fallback) => Math.Clamp(value ?? fallback, 0, 100);

    partial void OnMasterVolumeChanged(int value) => VolumeChanged();
    partial void OnMusicVolumeChanged(int value) => VolumeChanged();
    partial void OnAmbientVolumeChanged(int value) => VolumeChanged();
    partial void OnEffectsVolumeChanged(int value) => VolumeChanged();
    partial void OnVoiceVolumeChanged(int value) => VolumeChanged();
    partial void OnIsMutedChanged(bool value) => ApplyVolumes();
    partial void OnIsEmergencyChanged(bool value) => ApplyVolumes();

    /// <summary>The original CONFIG screen (once a game is loaded).</summary>
    [ObservableProperty] public partial ConfigViewModel? Config { get; set; }

    public void SaveSettings() => _settings.Save();

    [RelayCommand]
    void OpenConfig() => Config?.Open();

    void VolumeChanged()
    {
        if (_initializing)
            return;
        ApplyVolumes();
        _settings.MasterVolume = MasterVolume;
        _settings.MusicVolume = MusicVolume;
        _settings.AmbientVolume = AmbientVolume;
        _settings.EffectsVolume = EffectsVolume;
        _settings.VoiceVolume = VoiceVolume;
        _settings.Save();
    }

    void ApplyVolumes()
    {
        var mixer = AudioSystem.Shared.Mixer;
        // --mute (automated screenshots) wins over everything and is never saved.
        mixer.MasterVolume = AppHost.StartupMute || IsMuted || IsEmergency ? 0 : MasterVolume / 100f;
        var config = GameConfig.Current;
        mixer.SetBusVolume(AudioBus.Music, config.MuteMusic ? 0 : MusicVolume / 100f);
        mixer.SetBusVolume(AudioBus.Ambient, config.MuteAmbient ? 0 : AmbientVolume / 100f);
        mixer.SetBusVolume(AudioBus.Effects, config.MuteEffects ? 0 : EffectsVolume / 100f);
        mixer.SetBusVolume(AudioBus.Voice, config.MuteVoice ? 0 : VoiceVolume / 100f);
    }

    /// <summary>
    /// Keyboard shortcuts: Space/Enter next line, 1..9 pick a choice, M mute, Esc or Pause emergency screen (Esc like
    /// the original; any key returns). Returns true when the key was used.
    /// </summary>
    public bool HandleKey(Key key)
    {
        if (IsEmergency)
        {
            IsEmergency = false;
            return true;
        }
        switch (key)
        {
            case Key.Escape or Key.Pause:
                ShowEmergency();
                return true;
            case Key.M:
                IsMuted = !IsMuted;
                return true;
            case Key.F12 or Key.PrintScreen when SelectedTab == 0 && Play is { } shooter:
                shooter.Capture();
                return true;
        }

        if (SelectedTab == 0 && Play is { IsConfirming: true } asking && key is Key.Y or Key.Enter or Key.N)
        {
            asking.AnswerConfirm(key != Key.N);
            return true;
        }
        if (SelectedTab == 0 && Play is { IsMoviePlaying: true } play && key is Key.Space or Key.Enter)
        {
            play.SkipMovie();
            return true;
        }
        if (SelectedTab == 0 && Play?.HandleOilKey(key) == true)
            return true;
        var dialogue = SelectedTab switch
        {
            0 => Play?.Dialogue,
            2 => Scripts?.Dialogue,
            _ => null,
        };
        if (dialogue is not { IsVisible: true })
            return false;
        if (key is Key.Space or Key.Enter && dialogue.CanAdvance)
        {
            dialogue.NextCommand.Execute(null);
            return true;
        }
        int number = key is >= Key.D1 and <= Key.D9 ? key - Key.D1 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad1 : -1;
        if (number >= 0 && number < dialogue.Choices.Count)
        {
            dialogue.ChooseCommand.Execute(dialogue.Choices[number]);
            return true;
        }
        return false;
    }

    void ShowEmergency()
    {
        if (EmergencyImage is null && _fileSystem is { } fs)
        {
            // data/dummy.bmp ships next to the archives of each edition.
            foreach (var edition in fs.Install.Editions.Reverse())
            {
                string path = edition.DataFolder.TrimEnd('/', '\\') + "/dummy.bmp";
                if (!fs.Install.Storage.FileExists(path))
                    continue;
                try
                {
                    using var stream = fs.Install.Storage.OpenRead(path);
                    EmergencyImage = new Bitmap(stream);
                    break;
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
                {
                }
            }
        }
        IsEmergency = true;
    }
}
