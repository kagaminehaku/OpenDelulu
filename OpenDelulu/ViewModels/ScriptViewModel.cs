using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenDelulu.Engine;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Engine.Script;
using OpenDelulu.Rendering;
using OpenDelulu.Services;

namespace OpenDelulu.ViewModels;

public sealed record ChoiceOption(int Index, string Text);

/// <summary>
/// Plays an event script: the girl it is about stands in front of the camera, lines show in a dialogue box
/// with their voice and lip sync, menus become buttons.
/// </summary>
public sealed partial class ScriptViewModel : ViewModelBase
{
    readonly GameFileSystem _fs;
    readonly ScriptLibrary _library;
    readonly CharacterAssembler _assembler;
    readonly HashSet<int> _girls;
    ScriptRunner? _runner;
    Character? _girl;
    int _girlVersion;

    public ScriptViewModel(GameFileSystem fs, CharacterDatabase database, ScriptLibrary library)
    {
        _fs = fs;
        _library = library;
        _assembler = new CharacterAssembler(fs, database);
        _girls = database.Girls.Select(g => g.Id).ToHashSet();
        Scripts = library.Scripts;
        SelectedScript = (AppHost.StartupScript is { } name ? library.Find(name) : null) ?? Scripts.FirstOrDefault();
        if (AppHost.StartupScript is not null)
        {
            AppHost.StartupScript = null;
            _ = StartAsync();
        }
    }

    public IReadOnlyList<ScriptInfo> Scripts { get; }
    public ObservableCollection<ChoiceOption> Choices { get; } = [];
    public string[] IntimacyLevels { get; } = ["Thân mật 1", "Thân mật 2", "Thân mật 3", "Thân mật 4", "Thân mật 5"];

    [ObservableProperty] public partial ScriptInfo? SelectedScript { get; set; }
    /// <summary>Intimacy used by Switch commands (0-based index of <see cref="IntimacyLevels"/>).</summary>
    [ObservableProperty] public partial int IntimacyIndex { get; set; }
    [ObservableProperty] public partial ModelScene? Scene { get; set; }
    [ObservableProperty] public partial string Speaker { get; set; } = "";
    [ObservableProperty] public partial string Text { get; set; } = "Chọn một kịch bản rồi bấm Bắt đầu.";
    [ObservableProperty] public partial bool CanAdvance { get; set; }
    [ObservableProperty] public partial bool HasChoices { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";

    [RelayCommand]
    async Task StartAsync()
    {
        if (SelectedScript is not { } script)
            return;
        AudioSystem.Shared.Mixer.StopBus(AudioBus.Voice, 0.1);
        _runner = new ScriptRunner(_library.Load) { QuerySubject = _ => IntimacyIndex + 1 };
        try
        {
            _runner.Start(script.Name);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            Status = ex.Message;
            return;
        }
        Status = "";
        if (script.GirlId is int girl)
            await ShowGirlAsync(girl);
        Advance();
    }

    [RelayCommand]
    void Next() => Advance();

    [RelayCommand]
    void Choose(ChoiceOption option)
    {
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
            CanAdvance = false;
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
                Speaker = "";
                Text = "— Hết —";
                CanAdvance = false;
                break;
        }
    }

    void PlayVoice(string? voice)
    {
        AudioSystem.Shared.Mixer.StopBus(AudioBus.Voice, 0.05);
        if (voice is null || SelectedScript is not { } script)
            return;
        try
        {
            var handle = AudioSystem.Shared.Play(_fs.ReadReferenced(script.Archive, voice), AudioBus.Voice);
            // The girl on screen moves her lips only for her own lines (pe02_… = girl 02).
            if (_girl is { } girl && ScriptLibrary.VoiceGirl(voice) == girl.Girl.Id)
            {
                girl.Animator.Speech = new LipSync(handle, null);
                Scene?.NotifyChanged();
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            Status = $"Không phát được {voice}: {ex.Message}";
        }
    }

    async Task ShowGirlAsync(int girlId)
    {
        if (_girl?.Girl.Id == girlId || !_girls.Contains(girlId))
            return;
        int version = ++_girlVersion;
        try
        {
            var character = await Task.Run(() => _assembler.Build(new CharacterSpec(girlId)));
            if (version != _girlVersion)
                return;
            _girl = character;
            Scene = new ModelScene(character.Model, character.Animator);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            Status = $"Không ghép được nhân vật {girlId:00}: {ex.Message}";
        }
    }
}
