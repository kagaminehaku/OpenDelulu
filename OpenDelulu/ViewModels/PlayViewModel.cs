using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenDelulu.Engine;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Engine.Game;
using OpenDelulu.Engine.Script;
using OpenDelulu.Engine.Stages;
using OpenDelulu.Localization;
using OpenDelulu.Rendering;
using OpenDelulu.Services;

namespace OpenDelulu.ViewModels;

public sealed record GirlChoice(int Id, string Name, int Intimacy)
{
    public string Display => Translator.Format("play.girlChoice", Name, Intimacy);
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
    (RenderModel Model, CharacterAnimator? Animator)? _prop;

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
        StageNames = stages.Stages.Select(s => new LocalizedText(() => F("play.stageN", s.Id))).ToList();
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
    public IReadOnlyList<LocalizedText> StageNames { get; }
    public LocalizedText[] Times { get; } = [new("time.morning"), new("time.noon"), new("time.evening"), new("time.night"), new("time.lateNight")];

    [ObservableProperty] public partial GirlChoice? SelectedGirl { get; set; }
    [ObservableProperty] public partial int SelectedStageIndex { get; set; }
    [ObservableProperty] public partial int SelectedTimeIndex { get; set; } = 1;
    [ObservableProperty] public partial ModelScene? Scene { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool InGame { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = T("play.prompt");

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
        Status = T("play.loading");
        Dialogue.Hide();
        try
        {
            var (stage, girl) = await Task.Run(() => (Stage.Load(_fs, variant), _assembler.Build(new CharacterSpec(choice.Id))));
            _stage = stage;
            _girl = girl;
            _point = 0;
            _prop = null;
            PlaceGirl();
            BuildScene();
            InGame = true;
            if (AppHost.StartupActivity is int startup && girl.Girl.DateActivities.FirstOrDefault(a => a.Number == startup) is { } first)
            {
                AppHost.StartupActivity = null;
                StartActivity(first);
            }
            Status = F("play.where", choice.Name, StageNames[SelectedStageIndex], Times[SelectedTimeIndex]);
            PlayAmbience(stageInfo.Id);
            Meet();
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            Status = F("play.loadError", ex.Message);
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
        _prop = null;
        InGame = false;
        RefreshGirls(Girls.Select(g => g.Id).ToList());
        Status = T("play.prompt");
    }

    void PlaceGirl()
    {
        if (_girl is null || _stage is null)
            return;
        var points = _stage.PointGroup("Date");
        var placement = points.Count > 0 ? Upright(points[_point % points.Count].Transform) : Matrix4x4.Identity;
        _girl.Animator.Placement = placement;
        if (_prop?.Animator is { } propAnimator)
            propAnimator.Placement = placement;
        Scene?.NotifyChanged();
    }

    /// <summary>Stage, girl and the current activity's prop; the camera frames the girl.</summary>
    void BuildScene()
    {
        if (_stage is null || _girl is null)
            return;
        var objects = new List<(RenderModel, CharacterAnimator?)> { (_stage.Model, _stage.Animator), (_girl.Model, _girl.Animator) };
        if (_prop is { } prop)
            objects.Add(prop);
        Scene = new ModelScene(objects, subject: 1);
        var light = CharacterLight(_stage, Time);
        for (int i = 1; i < objects.Count; i++)
            Scene.SetLight(i, light);
    }

    /// <summary>Date activity N plays at the stage's o01_N_Point_DateNN, with its prop.</summary>
    void StartActivity(DateActivity activity)
    {
        if (_girl is null || _stage is null)
            return;
        try
        {
            var prop = _assembler.StartActivity(_girl, activity);
            // A prop without its own animation still has to follow the point: give it a static animator.
            _prop = prop is { } p ? (p.Model, p.Animator ?? new CharacterAnimator(p.Model) { EyesFollowTarget = false }) : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException)
        {
            Status = F("date.loadError", ex.Message);
            return;
        }
        var points = _stage.PointGroup("Date");
        int index = points.ToList().FindIndex(p => p.Name == $"o01_N_Point_Date{DatePoint(activity.Number):00}");
        _point = index >= 0 ? index : activity.Number - 1;
        _girl.Animator.Physics?.Reset();
        PlaceGirl();
        BuildScene();
    }

    /// <summary>
    /// Stage point for a date activity. The exe picks points with an algorithm not decoded yet; the data shows that
    /// points 08..10 lie under water on every stage with a sea (negative heights), so the water activities
    /// (4 swimming, 7 float ring, 18 kicking) go there and trade places with activities 8..10; the rest keep their
    /// own number (3 sunbathing on the raised deck at point 03, 13 boat ride at the jetty point 13, …).
    /// </summary>
    static int DatePoint(int activity) => activity switch
    {
        4 => 8,
        7 => 9,
        18 => 10,
        8 => 4,
        9 => 7,
        10 => 18,
        _ => activity,
    };

    static string ActivityName(int number) =>
        Translator.Instance[$"date.activity{number}"] is var name && name != $"date.activity{number}" ? name : Translator.Format("date.activityN", number);

    void ShowActivities()
    {
        if (_girl is null)
            return;
        var options = _girl.Girl.DateActivities
            .Select(a => (ActivityName(a.Number), (Action)(() => { StartActivity(a); ShowMenu(); })))
            .ToList();
        options.Add((T("date.back"), ShowMenu));
        Dialogue.ShowMenu(F("date.choose", CharacterDatabase.NameOf(_girl.Girl.Id)), options);
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
            (T("play.talk"), () => RunEvent(IsNight ? EventKind.GreetingNight : EventKind.Greeting, ShowMenu)),
            (T("play.date"), ShowActivities),
            (T("play.move"), () => { _point++; PlaceGirl(); ShowMenu(); }),
        };
        int maxLevel = GameEvents.Levels(_library, girl).DefaultIfEmpty(1).Max();
        if (progress.Intimacy < maxLevel && GameEvents.Find(_library, girl, progress.Intimacy, EventKind.LeadIn) is not null)
            options.Add((F("play.nextLevel", progress.Intimacy + 1), () => RunEvent(EventKind.LeadIn, () =>
            {
                // The H scene that follows the lead-in is not implemented yet; the level still goes up.
                progress.Intimacy++;
                progress.MetAtLevel = false;
                _progress.Save(ProgressPath);
                Meet();
            })));
        options.Add((T("play.goodbye"), () => RunEvent(EventKind.Parting, Leave)));
        Dialogue.ShowMenu(F("play.menu", CharacterDatabase.NameOf(girl), progress.Intimacy), options);
    }
}
