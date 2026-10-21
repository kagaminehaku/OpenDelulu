using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenDelulu.Engine;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Engine.Script;
using OpenDelulu.Localization;
using OpenDelulu.Rendering;
using OpenDelulu.Services;

namespace OpenDelulu.ViewModels;

public sealed record ChoiceOption(int Index, string Text);

/// <summary>
/// The dialogue box: runs a script line by line (voice + lip sync on the girl on screen), shows its menus,
/// and can also show menus of the game itself (<see cref="ShowMenu"/>).
/// </summary>
public sealed partial class DialogueViewModel(GameFileSystem fs, ScriptLibrary library) : ViewModelBase
{
    /// <summary>The CONFIG options; "Menu Transparency" fades the dialogue window.</summary>
    public GameConfig Config => GameConfig.Current;

    ScriptRunner? _runner;
    ScriptInfo? _script;
    IReadOnlyList<Action>? _menuActions;
    Action? _finished;

    public ObservableCollection<ChoiceOption> Choices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Frame), nameof(FrameMargin), nameof(TextMargin))]
    public partial string Speaker { get; set; } = "";

    /// <summary>Where the window sits in the 1024x232 bar: right of the portrait with a speaker, full width without.</summary>
    public Avalonia.Thickness FrameMargin => Speaker.Length > 0 ? new(192, 0, 0, 0) : new(0);

    /// <summary>The text inside the window's box (st06_01_02: box 8..783 × 92..206; st06_01_01: 48..974 × 91..206).</summary>
    public Avalonia.Thickness TextMargin => Speaker.Length > 0 ? new(228, 104, 96, 28) : new(84, 104, 96, 28);

    /// <summary>Who speaks, as in the original's portraits st06_02_NN (0 = the player, girl ids, 20 = front desk); -1 = nobody.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Portrait))]
    public partial int SpeakerId { get; set; } = -1;

    // ---- The original dialogue box (60_adv): water window with a name tab, portraits, blue choice buttons.
    UiTextures? _ui;
    UiTextures Ui => _ui ??= new UiTextures(fs);

    /// <summary>The speaker's portrait, or null (system messages and menus).</summary>
    public Bitmap? Portrait => SpeakerId < 0 ? null : Ui.Get($"st06_02_{(SpeakerId is 20 ? 99 : SpeakerId):00}_00_00.tga");

    /// <summary>The window: with the name tab when someone speaks; a plain dark box without the game's textures.</summary>
    public IBrush Frame => Ui.Get(Speaker.Length > 0 ? "st06_01_02_00_00.tga" : "st06_01_01_00_00.tga") is { } window
        ? new ImageBrush(window) { Stretch = Stretch.Fill }
        : new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x18, 0x20));

    public IBrush ChoiceBackground => Ui.Get("st06_01_04_00_00.tga") is { } button
        ? new ImageBrush(button) { Stretch = Stretch.Fill }
        : new SolidColorBrush(Color.FromArgb(0xFF, 0x35, 0x5C, 0xD8));

    /// <summary>The little dolphin of the "next" button.</summary>
    public Bitmap? NextIcon => Ui.Get("st06_01_03_00_00.tga");

    /// <summary>Speaker ids of the scripts' Japanese names.</summary>
    static int SpeakerIdOf(string japanese) => japanese switch
    {
        "俺" => 0,
        "愛美" => 1,
        "麻矢" => 2,
        "イスカ" => 3,
        "イーオ" => 4,
        "麗子" => 5,
        "マリア" => 6,
        "バエル" => 11,
        "フェイ" => 12,
        "フロント" => 20,
        _ => -1,
    };
    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial bool CanAdvance { get; set; }
    [ObservableProperty] public partial bool HasChoices { get; set; }
    [ObservableProperty] public partial bool IsVisible { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";

    /// <summary>The girl whose lips move for her lines (voice files peNN_… with NN = her id), and her scene.</summary>
    public Func<(Character Girl, ModelScene Scene)?>? Speaker3D { get; set; }

    /// <summary>Value for Switch commands (the girl's intimacy level).</summary>
    public int Intimacy { get; set; } = 1;

    /// <summary>Plays a script; <paramref name="finished"/> runs when it ends.</summary>
    public void Run(ScriptInfo script, Action? finished = null)
    {
        AudioSystem.Shared.Mixer.StopBus(AudioBus.Voice, 0.1);
        _script = script;
        _finished = finished;
        _menuActions = null;
        _runner = new ScriptRunner(library.Load) { QuerySubject = _ => Intimacy };
        try
        {
            _runner.Start(script.Name);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            Status = ex.Message;
            End();
            return;
        }
        Status = "";
        IsVisible = true;
        Advance();
    }

    /// <summary>Shows a game menu in the box (no script running).</summary>
    public void ShowMenu(string text, IReadOnlyList<(string Label, Action Action)> options)
    {
        _runner = null;
        Speaker = "";
        SpeakerId = -1;
        Text = text;
        CanAdvance = false;
        Choices.Clear();
        for (int i = 0; i < options.Count; i++)
            Choices.Add(new ChoiceOption(i, options[i].Label));
        _menuActions = [.. System.Linq.Enumerable.Select(options, o => o.Action)];
        HasChoices = options.Count > 0;
        IsVisible = true;
    }

    public void Hide()
    {
        IsVisible = false;
        _runner = null;
        _menuActions = null;
        AudioSystem.Shared.Mixer.StopBus(AudioBus.Voice, 0.1);
    }

    [RelayCommand]
    void Next() => Advance();

    [RelayCommand]
    void Choose(ChoiceOption option)
    {
        if (_menuActions is { } actions)
        {
            _menuActions = null;
            Choices.Clear();
            HasChoices = false;
            actions[option.Index]();
            return;
        }
        if (_runner is null)
            return;
        _runner.Choose(option.Index);
        Advance();
    }

    void Advance()
    {
        if (_runner is null)
            return;
        Choices.Clear();
        HasChoices = false;
        ScriptEvent next;
        try
        {
            next = _runner.Next();
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or InvalidOperationException)
        {
            Status = ex.Message;
            End();
            return;
        }

        switch (next)
        {
            case ScriptMessage message:
                Speaker = GameText.Speaker(message.Speaker);
                SpeakerId = SpeakerIdOf(message.Speaker);
                Text = message.Text;
                CanAdvance = true;
                PlayVoice(message.Voice);
                break;
            case ScriptChoice choice:
                for (int i = 0; i < choice.Options.Count; i++)
                    Choices.Add(new ChoiceOption(i, choice.Options[i]));
                HasChoices = true;
                CanAdvance = false;
                break;
            default:
                End();
                break;
        }
    }

    void End()
    {
        _runner = null;
        CanAdvance = false;
        Speaker = "";
        Text = T("dialogue.end");
        var finished = _finished;
        _finished = null;
        finished?.Invoke();
    }

    void PlayVoice(string? voice)
    {
        AudioSystem.Shared.Mixer.StopBus(AudioBus.Voice, 0.05);
        if (voice is null || _script is null)
            return;
        try
        {
            var handle = AudioSystem.Shared.Play(fs.ReadReferenced(_script.Archive, voice), AudioBus.Voice);
            // The girl on screen moves her lips only for her own lines (pe02_… = girl 02).
            if (Speaker3D?.Invoke() is var (girl, scene) && ScriptLibrary.VoiceGirl(voice) == girl.Girl.Id)
            {
                girl.Animator.Speech = new LipSync(handle, null);
                scene.NotifyChanged();
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            Status = F("audio.playError", voice, ex.Message);
        }
    }
}
