using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenDelulu.Engine;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Engine.Script;
using OpenDelulu.Rendering;

namespace OpenDelulu.ViewModels;

public sealed record ChoiceOption(int Index, string Text);

/// <summary>
/// The dialogue box: runs a script line by line (voice + lip sync on the girl on screen), shows its menus,
/// and can also show menus of the game itself (<see cref="ShowMenu"/>).
/// </summary>
public sealed partial class DialogueViewModel(GameFileSystem fs, ScriptLibrary library) : ViewModelBase
{
    ScriptRunner? _runner;
    ScriptInfo? _script;
    IReadOnlyList<Action>? _menuActions;
    Action? _finished;

    public ObservableCollection<ChoiceOption> Choices { get; } = [];

    [ObservableProperty] public partial string Speaker { get; set; } = "";
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
                Speaker = ScriptLibrary.SpeakerName(message.Speaker);
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
        Text = "— Hết —";
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
            Status = $"Không phát được {voice}: {ex.Message}";
        }
    }
}
