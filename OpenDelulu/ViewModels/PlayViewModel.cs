using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenDelulu.Engine;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Engine.Game;
using OpenDelulu.Engine.Script;
using OpenDelulu.Engine.Stages;
using OpenDelulu.Rendering;
using OpenDelulu.Services;

namespace OpenDelulu.ViewModels;

public sealed record GirlChoice(int Id, string Name, int Intimacy)
{
    public string Display => $"{Name} · thân mật {Intimacy}";
}

/// <summary>
/// The basic game loop: pick a girl, a stage and the time of day; she waits at a date point of the stage and
/// the event script for her intimacy level plays (first meeting, then greetings). A menu then offers talking,
/// moving to another spot, the lead-in to the next level, or saying goodbye. Progress is saved as JSON.
/// </summary>
public sealed partial class PlayViewModel : ViewModelBase
{
    /// <summary>Where progress is saved (tests point it elsewhere).</summary>
    public static string ProgressPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenDelulu", "progress.json");

    readonly GameFileSystem _fs;
    readonly CharacterAssembler _assembler;
    readonly StageDatabase _stages;
    readonly ScriptLibrary _library;
    readonly GameProgress _progress;
    Character? _girl;
    Stage? _stage;
    int _point;

    public PlayViewModel(GameFileSystem fs, CharacterDatabase characters, StageDatabase stages, ScriptLibrary library)
    {
        _fs = fs;
        _assembler = new CharacterAssembler(fs, characters);
        _stages = stages;
        _library = library;
        _progress = GameProgress.Load(ProgressPath);
        Dialogue = new DialogueViewModel(fs, library)
        {
            Speaker3D = () => _girl is { } girl && Scene is { } scene ? (girl, scene) : null,
        };
        StageNames = stages.Stages.Select(s => $"Sân khấu {s.Id}").ToList();
        RefreshGirls(characters.Girls.Select(g => g.Id).Where(id => GameEvents.Levels(library, id).Count > 0).ToList());
        SelectedGirl = Girls.FirstOrDefault();
        if (AppHost.StartupPlay is var (girl, stage, time))
        {
            AppHost.StartupPlay = null;
            SelectedGirl = Girls.FirstOrDefault(g => g.Id == girl) ?? SelectedGirl;
            SelectedStageIndex = Math.Clamp(stage - 1, 0, Math.Max(0, StageNames.Count - 1));
            SelectedTimeIndex = Math.Clamp(time - 1, 0, Times.Length - 1);
            _ = EnterAsync();
        }
    }

    public DialogueViewModel Dialogue { get; }
    public List<GirlChoice> Girls { get; private set; } = [];
    public IReadOnlyList<string> StageNames { get; }
    public string[] Times { get; } = ["Sáng", "Trưa", "Chiều tối", "Đêm", "Khuya"];

    [ObservableProperty] public partial GirlChoice? SelectedGirl { get; set; }
    [ObservableProperty] public partial int SelectedStageIndex { get; set; }
    [ObservableProperty] public partial int SelectedTimeIndex { get; set; } = 1;
    [ObservableProperty] public partial ModelScene? Scene { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool InGame { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Chọn cô gái, sân khấu và thời gian rồi bấm Vào game.";

    StageTime Time => (StageTime)(SelectedTimeIndex + 1);
    bool IsNight => Time is StageTime.Night or StageTime.LateNight;

    void RefreshGirls(IReadOnlyList<int> ids)
    {
        int? keep = SelectedGirl?.Id;
        Girls = ids.Select(id => new GirlChoice(id, CharacterDatabase.NameOf(id), CurrentLevel(id))).ToList();
        OnPropertyChanged(nameof(Girls));
        SelectedGirl = Girls.FirstOrDefault(g => g.Id == keep) ?? Girls.FirstOrDefault();
    }

    /// <summary>The girl's saved level, raised to her first level with scripts (Fei starts at 5).</summary>
    int CurrentLevel(int girl)
    {
        var levels = GameEvents.Levels(_library, girl);
        var progress = _progress.For(girl);
        if (levels.Count > 0 && progress.Intimacy < levels[0])
            progress.Intimacy = levels[0];
        return progress.Intimacy;
    }

    [RelayCommand]
    async Task EnterAsync()
    {
        if (SelectedGirl is not { } choice || IsBusy || _stages.Stages.Count == 0)
            return;
        var stageInfo = _stages.Stages[Math.Clamp(SelectedStageIndex, 0, _stages.Stages.Count - 1)];
        if (stageInfo.At(Time) is not { } variant)
            return;
        IsBusy = true;
        Status = "Đang tải sân khấu…";
        Dialogue.Hide();
        try
        {
            var (stage, girl) = await Task.Run(() => (Stage.Load(_fs, variant), _assembler.Build(new CharacterSpec(choice.Id))));
            _stage = stage;
            _girl = girl;
            _point = 0;
            PlaceGirl();
            Scene = new ModelScene([(stage.Model, null), (girl.Model, girl.Animator)], subject: 1);
            Scene.SetLight(1, CharacterLight(stage, Time));
            InGame = true;
            Status = $"{choice.Name} · {StageNames[SelectedStageIndex]} · {Times[SelectedTimeIndex]}";
            PlayAmbience(stageInfo.Id);
            Meet();
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            Status = "Không tải được: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    void Leave()
    {
        Dialogue.Hide();
        AudioSystem.Shared.StopMusic();
        Scene = null;
        _girl = null;
        _stage = null;
        InGame = false;
        RefreshGirls(Girls.Select(g => g.Id).ToList());
        Status = "Chọn cô gái, sân khấu và thời gian rồi bấm Vào game.";
    }

    void PlaceGirl()
    {
        if (_girl is null || _stage is null)
            return;
        var points = _stage.PointGroup("Date");
        _girl.Animator.Placement = points.Count > 0 ? Upright(points[_point % points.Count].Transform) : Matrix4x4.Identity;
        Scene?.NotifyChanged();
    }

    /// <summary>Keeps position and heading of a stage point, drops any tilt or scale.</summary>
    static Matrix4x4 Upright(Matrix4x4 point)
    {
        float yaw = MathF.Atan2(point.M31, point.M33);
        return Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(point.Translation);
    }

    /// <summary>
    /// How the girl is lit on this stage: brightness by time of day, hue from the stage's ambient color (.xl),
    /// e.g. dark blue at night, orange in the evening. The stage itself has its lighting baked in.
    /// </summary>
    static Vector3 CharacterLight(Stage stage, StageTime time)
    {
        float brightness = time switch
        {
            StageTime.Morning => 0.95f,
            StageTime.Noon => 1f,
            StageTime.Evening => 0.85f,
            StageTime.Night => 0.6f,
            _ => 0.5f,
        };
        var ambient = stage.Lights?.SceneAmbient is { } a ? new Vector3(a.X, a.Y, a.Z) : Vector3.One;
        float peak = MathF.Max(ambient.X, MathF.Max(ambient.Y, ambient.Z));
        var hue = peak > 0.01f ? ambient / peak : Vector3.One;
        // Half the hue, half white: a tint, not a recolor.
        return Vector3.Lerp(Vector3.One, hue, time is StageTime.Noon or StageTime.Morning ? 0.15f : 0.5f) * brightness;
    }

    void PlayAmbience(int stage)
    {
        // es01..08 are the beach ambiences in sb3_0600; fall back to silence when missing.
        string file = $"es{Math.Clamp(stage, 1, 8):00}_00_00_00_00.ogg";
        try
        {
            if (_fs.ExistsReferenced("sb3_0600.pp", file))
                AudioSystem.Shared.PlayMusic(_fs, "sb3_0600.pp", file);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
        }
    }

    // ------------------------------------------------------------------ events

    GirlProgress Progress => _progress.For(_girl!.Girl.Id);

    void Meet()
    {
        var progress = Progress;
        var kind = !progress.MetAtLevel ? EventKind.FirstMeeting : IsNight ? EventKind.GreetingNight : EventKind.Greeting;
        RunEvent(kind, () =>
        {
            if (kind == EventKind.FirstMeeting)
            {
                progress.MetAtLevel = true;
                _progress.Save(ProgressPath);
            }
            ShowMenu();
        });
    }

    void RunEvent(EventKind kind, Action then)
    {
        var girl = _girl!.Girl.Id;
        if (GameEvents.Find(_library, girl, Progress.Intimacy, kind) is { } script)
        {
            Dialogue.Intimacy = Progress.Intimacy;
            Dialogue.Run(script, then);
        }
        else
            then();
    }

    void ShowMenu()
    {
        if (_girl is null)
            return;
        var progress = Progress;
        int girl = _girl.Girl.Id;
        var options = new List<(string, Action)>
        {
            ("Nói chuyện", () => RunEvent(IsNight ? EventKind.GreetingNight : EventKind.Greeting, ShowMenu)),
            ("Đi chỗ khác", () => { _point++; PlaceGirl(); ShowMenu(); }),
        };
        int maxLevel = GameEvents.Levels(_library, girl).DefaultIfEmpty(1).Max();
        if (progress.Intimacy < maxLevel && GameEvents.Find(_library, girl, progress.Intimacy, EventKind.LeadIn) is not null)
            options.Add(($"Tiến tới thân mật {progress.Intimacy + 1}", () => RunEvent(EventKind.LeadIn, () =>
            {
                // The H scene that follows the lead-in is not implemented yet; the level still goes up.
                progress.Intimacy++;
                progress.MetAtLevel = false;
                _progress.Save(ProgressPath);
                Meet();
            })));
        options.Add(("Tạm biệt", () => RunEvent(EventKind.Parting, Leave)));
        Dialogue.ShowMenu($"{CharacterDatabase.NameOf(girl)} · thân mật {progress.Intimacy}. Làm gì tiếp?", options);
    }
}
