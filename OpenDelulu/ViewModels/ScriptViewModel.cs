using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenDelulu.Engine;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Engine.Script;
using OpenDelulu.Localization;
using OpenDelulu.Rendering;
using OpenDelulu.Services;

namespace OpenDelulu.ViewModels;

/// <summary>
/// Plays any event script on its own: the girl it is about stands in front of the camera, lines show in the
/// dialogue box with their voice and lip sync, menus become buttons.
/// </summary>
public sealed partial class ScriptViewModel : ViewModelBase
{
    readonly ScriptLibrary _library;
    readonly CharacterAssembler _assembler;
    readonly HashSet<int> _girls;
    Character? _girl;
    int _girlVersion;

    public ScriptViewModel(GameFileSystem fs, CharacterDatabase database, ScriptLibrary library)
    {
        _library = library;
        _assembler = new CharacterAssembler(fs, database);
        _girls = database.Girls.Select(g => g.Id).ToHashSet();
        Dialogue = new DialogueViewModel(fs, library)
        {
            Speaker3D = () => _girl is { } girl && Scene is { } scene ? (girl, scene) : null,
        };
        Scripts = [.. library.Scripts.Select(s => new ScriptItem(s))];
        SelectedScript = (AppHost.StartupScript is { } name ? Scripts.FirstOrDefault(s => s.Info.Name == name) : null) ?? Scripts.FirstOrDefault();
        if (AppHost.StartupScript is not null)
        {
            AppHost.StartupScript = null;
            _ = StartAsync();
        }
    }

    public DialogueViewModel Dialogue { get; }
    public IReadOnlyList<ScriptItem> Scripts { get; }
    public LocalizedText[] IntimacyLevels { get; } = [.. Enumerable.Range(1, 5).Select(level => new LocalizedText(() => F("scripts.levelN", level)))];

    [ObservableProperty] public partial ScriptItem? SelectedScript { get; set; }
    /// <summary>Intimacy used by Switch commands (0-based index of <see cref="IntimacyLevels"/>).</summary>
    [ObservableProperty] public partial int IntimacyIndex { get; set; }
    [ObservableProperty] public partial ModelScene? Scene { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";

    [RelayCommand]
    async Task StartAsync()
    {
        if (SelectedScript?.Info is not { } script)
            return;
        if (script.GirlId is int girl)
            await ShowGirlAsync(girl);
        Dialogue.Intimacy = IntimacyIndex + 1;
        Dialogue.Run(script);
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
            Status = F("scripts.girlError", girlId, ex.Message);
        }
    }
}

/// <summary>A script in the picker: file name and translated title.</summary>
public sealed class ScriptItem : ViewModelBase
{
    public ScriptItem(ScriptInfo info) => Info = info;

    public ScriptInfo Info { get; }

    public string Display => $"{Info.Name}  {GameText.ScriptTitle(Info.Title)}";

    protected override void OnLanguageChanged() => OnPropertyChanged(nameof(Display));
}
