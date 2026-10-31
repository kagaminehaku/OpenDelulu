using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using OpenDelulu.Engine;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Engine.Game;
using OpenDelulu.Engine.H;
using OpenDelulu.Engine.Hud;
using OpenDelulu.Engine.Oil;
using OpenDelulu.Engine.Script;
using OpenDelulu.Engine.Stages;
using OpenDelulu.Engine.Video;
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
    GameProgress _progress;
    Character? _girl;
    Stage? _stage;
    int _point;
    (RenderModel Model, CharacterAnimator? Animator)? _prop;
    DateActivity? _activity;
    VoiceLibrary? _voices;
    OilSession? _oil;
    int _propObject = -1;
    readonly DispatcherTimer _oilTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly Stopwatch _oilClock = new();
    readonly ISceneTouchHandler _oilTouch;
    long _statusHoldUntil;

    public PlayViewModel(GameFileSystem fs, CharacterDatabase characters, StageDatabase stages, ScriptLibrary library, VoiceLibrary? voices = null)
    {
        _fs = fs;
        _voices = voices;
        _oilTouch = new OilTouch(this);
        _oilTimer.Tick += (_, _) => OilTick();
        _assembler = new CharacterAssembler(fs, characters);
        _stages = stages;
        _library = library;
        _progress = GameProgress.Load(ProgressPath);
        Services.GameConfig.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Services.GameConfig.EraseObstacles) && Scene is { } scene)
                scene.EraseObstacles = Services.GameConfig.Current.EraseObstacles;
            else if (e.PropertyName == nameof(Services.GameConfig.Crosshair) && Scene is not null)
                BuildScene();
            else if (e.PropertyName is nameof(Services.GameConfig.BodyOpacity) or nameof(Services.GameConfig.DickOpacity)
                     or nameof(Services.GameConfig.HandOpacity) or nameof(Services.GameConfig.Clothes))
                ApplyConfigOpacity();
        };
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
        else
            _ = StartAsync();
        _playClock.Start();
        HookMenuSounds();
    }

    /// <summary>
    /// Like the exe's boot: the logo scene (50_logo) plays data/sb3_0001.mpg (studio logo and opening), the opening scene
    /// (58_opening) data/sb3_0002.mpg when installed, then the title. Each can be skipped; --no-movies skips them.
    /// </summary>
    async Task StartAsync()
    {
        await PlayMovieAsync("sb3_0001.mpg");
        await PlayMovieAsync("sb3_0002.mpg");
        ShowTicket();
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatusPanel))]
    public partial bool InGame { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = T("play.prompt");

    // ------------------------------------------------------------------ movies

    /// <summary>The movie on screen (frames alternate between two bitmaps so the image refreshes), or null.</summary>
    [ObservableProperty] public partial Bitmap? MovieFrame { get; set; }
    [ObservableProperty] public partial bool IsMoviePlaying { get; set; }

    MoviePlayer? _movie;
    readonly WriteableBitmap?[] _movieBitmaps = new WriteableBitmap?[2];
    int _movieFlip;
    TaskCompletionSource? _movieDone;
    DispatcherTimer? _movieTimer;
    readonly Stopwatch _movieClock = new();

    /// <summary>
    /// The stage introduction of the original's date scene (05_date): sb3_0011..0025.mpg, three per stage 1..5 for day,
    /// evening and night (the exe's table lists morning/noon, evening, night/late night per stage). Null for other stages.
    /// Like the exe it plays the first time the stage is visited at a time of day.
    /// </summary>
    static string? StageMovie(int stage, StageTime time)
    {
        if (stage is < 1 or > 5)
            return null;
        int part = time switch
        {
            StageTime.Evening => 1,
            StageTime.Night or StageTime.LateNight => 2,
            _ => 0,
        };
        return $"sb3_{11 + (stage - 1) * 3 + part:0000}.mpg";
    }

    /// <summary>Plays a movie over the 3D view until it ends or is skipped (click, Space, Enter).</summary>
    Task PlayMovieAsync(string file)
    {
        if (!AppHost.PlayMovies || _movie is not null)
            return Task.CompletedTask;
        MoviePlayer? movie;
        try
        {
            movie = MoviePlayer.Open(_fs.Install, file, AudioSystem.Shared);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
            or NotSupportedException or EndOfStreamException)
        {
            return Task.CompletedTask; // a damaged movie must not keep the player out of the game
        }
        if (movie is null)
            return Task.CompletedTask;
        _movie = movie;
        for (int i = 0; i < 2; i++)
            _movieBitmaps[i] = new WriteableBitmap(new PixelSize(movie.Width, movie.Height), new Avalonia.Vector(96, 96),
                PixelFormat.Rgba8888, AlphaFormat.Opaque);
        _movieDone = new TaskCompletionSource();
        _movieTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => MovieTick());
        _movieClock.Restart();
        _movieTimer.Start();
        IsMoviePlaying = true;
        return _movieDone.Task;
    }

    void MovieTick()
    {
        if (_movie is not { } movie)
            return;
        if (movie.FrameAt(_movieClock.Elapsed.TotalSeconds) is { } rgba && _movieBitmaps[_movieFlip] is { } bitmap)
        {
            using (var locked = bitmap.Lock())
            {
                for (int y = 0; y < movie.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(rgba, y * movie.Width * 4, locked.Address + y * locked.RowBytes, movie.Width * 4);
            }
            MovieFrame = bitmap;
            _movieFlip ^= 1;
        }
        if (movie.Finished)
            SkipMovie();
    }

    /// <summary>Ends the movie on screen (also when it finishes).</summary>
    public void SkipMovie()
    {
        if (_movie is not { } movie)
            return;
        _movieTimer?.Stop();
        _movie = null;
        movie.Dispose();
        IsMoviePlaying = false;
        MovieFrame = null;
        _movieDone?.TrySetResult();
    }

    /// <summary>Set while rubbing oil: presses on the viewer touch her instead of turning the camera.</summary>
    [ObservableProperty] public partial ISceneTouchHandler? TouchHandler { get; set; }

    StageTime Time => (StageTime)(SelectedTimeIndex + 1);
    bool IsNight => Time is StageTime.Night or StageTime.LateNight;

    void RefreshGirls(IReadOnlyList<int> ids)
    {
        int? keep = SelectedGirl?.Id;
        Girls = ids.Select(id => new GirlChoice(id, CharacterDatabase.NameOf(id), CurrentLevel(id))).ToList();
        OnPropertyChanged(nameof(Girls));
        RefreshMapGirls();
        SelectedGirl =Girls.FirstOrDefault(g => g.Id == keep) ?? Girls.FirstOrDefault();
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
        OilPose? startupOil = null;
        int? startupH = null;
        try
        {
            int? swimsuit = _progress.For(choice.Id).SwimsuitRow;
            // The stage loads while its introduction movie plays (as the original's date scene does).
            var loading = Task.Run(() => (Stage.Load(_fs, variant, stageInfo.Obstacles), _assembler.Build(SpecFor(choice.Id, swimsuit))));
            // Only on the first visit of the stage at that time of day (item list save +0xBDE, 9 stages × 5 times).
            int seen = (stageInfo.Id - 1) * 5 + SelectedTimeIndex;
            if (!_progress.StagesSeen.Contains(seen) && _memoryReplay is null && !_photoShoot)
            {
                _progress.StagesSeen.Add(seen);
                if (StageMovie(stageInfo.Id, Time) is { } movie)
                    await PlayMovieAsync(movie);
            }
            var (stage, girl) = await loading;
            _stage = stage;
            _girl = girl;
            _point = 0;
            if (_memoryReplay is null && !_photoShoot)
                MeetGirl(choice.Id);
            _prop = null;
            _activity = null;
            PlaceGirl();
            BuildScene();
            InGame = true;
            if (AppHost.StartupOil is int oilPose)
            {
                AppHost.StartupOil = null;
                startupOil = girl.Girl.OilPoses.FirstOrDefault(p => p.Number == oilPose);
            }
            if (AppHost.StartupActivity is int startup && girl.Girl.DateActivities.FirstOrDefault(a => a.Number == startup) is { } first)
            {
                AppHost.StartupActivity = null;
                StartActivity(first);
            }
            Status = F("play.where", choice.Name, StageNames[SelectedStageIndex], Times[SelectedTimeIndex]);
            UpdateMusic();
            // A memory replay starts its date activity at once, without the greeting.
            if (_photoShoot)
                BeginPhotoShoot();
            else if (_memoryReplay == 0)
                startupH = 0;
            else if (_memoryReplay is int replay && girl.Girl.DateActivities.FirstOrDefault(a => a.Number == replay) is { } remembered)
                StartActivity(remembered);
            else if (AppHost.StartupH is int hPosition)
            {
                AppHost.StartupH = null;
                startupH = Math.Clamp(hPosition - 1, 0, HHudPositions.Length - 1);
            }
            else if (startupOil is null)
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
        if (startupOil is not null)
            await StartOilAsync(startupOil);
        else if (startupH is int position)
            await StartHAsync(position, levelUp: false);
    }

    [RelayCommand]
    void Leave()
    {
        StopOil(restore: false);
        EndCouple();
        _hLevelUp = false;
        HideDateHud();
        HideSystemHud();
        Dialogue.Hide();
        Scene = null;
        _girl = null;
        _stage = null;
        _prop = null;
        _activity = null;
        InGame = false;
        RefreshStatus();
        RefreshGirls(Girls.Select(g => g.Id).ToList());
        Status = T("play.prompt");
        if (_photoReturn is bool backToSelect)
        {
            // After a photo shoot: its selection screen, or the title.
            _photoReturn = null;
            if (backToSelect)
            {
                _photoSelect = true;
                OpenMemory();
            }
            else
                ShowTitle();
            return;
        }
        if (_memoryReplay is not null)
        {
            // Back to the memory screen after a replay.
            _memoryReplay = null;
            OpenMemory();
            return;
        }
        ShowLobby(backFromIsland: true);
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
        _propIndex = -1;
        if (_prop is { } prop)
        {
            _propIndex = objects.Count;
            objects.Add(prop);
        }
        _manObject = _liquidObject = _dropsObject = _squirtObject = -1;
        if (_couple is { } couple)
        {
            _manObject = objects.Count;
            objects.Add((couple.Man.Model, couple.Man.Animator));
            if (couple.Liquid is { } liquid)
            {
                _liquidObject = objects.Count;
                objects.Add((liquid.Model, liquid.Animator));
            }
            if (couple.Drops is { } drops)
            {
                _dropsObject = objects.Count;
                objects.Add((drops.Model, drops.Animator));
            }
            if (couple is HSession { Squirt: { } squirt } && SquirtParticles() is { } spray)
            {
                _squirtObject = objects.Count;
                objects.Add((squirt.Model, squirt.Animator));
                objects.Add((spray.Model, null));
            }
        }
        _propObject = -1;
        // The stage's keyframed sprites (.ptcl): sun glow, splashes, steam, lamp glows.
        int particleObject = -1;
        if (StageParticlesFor(_stage) is { } particles)
        {
            particleObject = objects.Count;
            objects.Add((particles.Model, null));
        }
        if (_oil is { } oilProps)
        {
            // Hands and oil drips, shown only while used.
            _propObject = objects.Count;
            foreach (var (model, animator) in oilProps.Props)
                objects.Add((model, animator));
        }
        // CONFIG's crosshair: the cm00 marker at the camera's target.
        int markerObject = -1;
        if (Services.GameConfig.Current.Crosshair && CameraMarker() is { } marker)
        {
            markerObject = objects.Count;
            objects.Add((marker, new CharacterAnimator(marker)));
        }
        var scene = new ModelScene(objects, subject: 1) { Glare = StageGlareFor(_stage) };
        if (markerObject >= 0)
            scene.SetCameraMarker(markerObject);
        scene.SetObstacles(ObstaclesFor(_stage));
        RefreshStatus();
        scene.EraseObstacles = Services.GameConfig.Current.EraseObstacles;
        if (_squirtObject >= 0 && _couple is HSession { Squirt: { } squirtFrames } && SquirtParticles() is { } spraySprites)
        {
            // The spray hangs on ek06's animated frame, placed at her crotch when her climax starts.
            scene.SetObjectParticles(_squirtObject + 1, spraySprites, name =>
                squirtFrames.Model.Rig.Find(name) is var frame and >= 0 ? squirtFrames.Animator.WorldOf(frame) : null);
        }
        if (particleObject >= 0 && StageParticlesFor(_stage) is { } sprites)
        {
            var stage = _stage;
            var bind = new Matrix4x4[stage.Model.Rig.Count];
            stage.Model.Rig.ComputeWorld(stage.Model.Rig.BindLocal, bind);
            scene.SetObjectParticles(particleObject, sprites, name =>
            {
                int frame = stage.Model.Rig.Find(name);
                if (frame < 0)
                    return null;
                return stage.Animator is { } animator ? animator.WorldOf(frame) : bind[frame];
            });
        }
        if (_oil is { } oil)
        {
            // The whole body: from the side and above when she lies down (poses 3 and 4), three-quarter otherwise.
            bool lying = oil.Pose.Number >= 3;
            scene.FrameLivePose = true;
            scene.CameraYawOffset = lying ? MathF.PI / 2 : MathF.PI / 4;
            scene.CameraPitch = lying ? 0.75f : 0.3f;
            scene.CameraZoom = lying ? 0.75f : 0.9f;
        }
        else if (_couple is not null)
        {
            // Both of them, from the side and a little above.
            scene.FrameLivePose = true;
            scene.CameraYawOffset = MathF.PI / 2;
            scene.CameraPitch = 0.45f;
            scene.CameraZoom = 0.8f;
        }
        Scene = scene;
        // Direct3D 9 lighting as in the original: the stage by its own .xl, the girl and the props by her bo00_NN.xl.
        var stageLights = _stage.Lights is { } xl ? LightSet.FromXl(xl) : null;
        var girlLights = CharacterLights(_girl.Girl, Time);
        Scene.SetObjectLights(0, stageLights);
        for (int i = 1; i < objects.Count; i++)
        {
            if (girlLights is not null)
                Scene.SetObjectLights(i, girlLights);
            else
                Scene.SetLight(i, CharacterLight(_stage, Time));
        }
        ApplyLook();
        SyncHand();
        ApplyConfigOpacity();
        SyncLiquid();
        ShowStain();
        if (_propIndex >= 0 && !IsItemShown)
            Scene.SetObjectVisible(_propIndex, false);
    }

    /// <summary>The date item's scene object (-1 none); the system menu's Item button shows / hides it.</summary>
    int _propIndex = -1;

    public bool IsItemShown { get; private set; } = true;

    int _manObject = -1, _liquidObject = -1, _dropsObject = -1, _squirtObject = -1;

    /// <summary>The H climax spray's particles (ld00 row 35: ek06 .ptcl), read once.</summary>
    StageParticles? SquirtParticles()
    {
        const string key = "sb3_0100.pp/ek06_00_00_00_00.ptcl";
        if (!_particleFiles.TryGetValue(key, out var particles))
        {
            try { particles = StageParticles.Load(OpenDelulu.Formats.Scene.PtclFile.Read(_fs.ReadReferenced("sb3_0100.pp", "ek06_00_00_00_00.ptcl")), "ek06_00_00_00_00.ptcl"); }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException) { particles = null; }
            _particleFiles[key] = particles;
        }
        return particles;
    }

    /// <summary>Shows the H scene's fluid while the scene wants it.</summary>
    void SyncLiquid()
    {
        if (Scene is not { } scene || _couple is not { } couple)
            return;
        if (_dropsObject >= 0)
            scene.SetObjectVisible(_dropsObject, couple.DropsVisible);
        if (_squirtObject >= 0 && couple is HSession h)
            scene.SetObjectVisible(_squirtObject + 1, h.SquirtVisible);
        if (_liquidObject < 0)
            return;
        scene.SetObjectVisible(_liquidObject, couple.LiquidVisible);
        if (couple.Liquid is { } liquid)
            foreach (var mesh in liquid.Model.Meshes)
                scene.SetObjectOpacity(_liquidObject, m => ReferenceEquals(m, mesh), couple.LiquidOpacity(mesh));
    }

    /// <summary>
    /// CONFIG's transparencies: the man's body and his member (Ecchi page; the member is mb00's o02_P_…omoza meshes) and
    /// the oil hands (Game page).
    /// </summary>
    void ApplyConfigOpacity()
    {
        if (Scene is not { } scene)
            return;
        var config = Services.GameConfig.Current;
        if (_manObject >= 0)
        {
            static bool Member(RenderMesh m) => m.FrameName.Contains("omoza", StringComparison.Ordinal);
            scene.SetObjectOpacity(_manObject, Member, config.DickOpacity / 100f);
            scene.SetObjectOpacity(_manObject, m => !Member(m), config.BodyOpacity / 100f);
        }
        if (_propObject >= 0 && _oil is { } oil)
            for (int i = 0; i < oil.HandCount; i++)
                scene.SetObjectOpacity(_propObject + i, _ => true, config.HandOpacity / 100f);
        ApplyClothes();
    }

    /// <summary>
    /// CONFIG's Ecchi "Clothes": off, the swimsuit's meshes (those with her suit texture) are hidden in the H, fera and
    /// fetish scenes; the body under them is whole.
    /// </summary>
    void ApplyClothes()
    {
        if (Scene is not { } scene || _girl is not { } girl)
            return;
        bool hide = _couple is not null && !Services.GameConfig.Current.Clothes;
        var suitTextures = girl.SwimsuitImages.Select(image => girl.Model.Textures.ToList().IndexOf(image)).Where(i => i >= 0).ToHashSet();
        var meshes = scene.Meshes;
        for (int m = 0; m < meshes.Count; m++)
            if (girl.Model.Meshes.Contains(meshes[m]) && suitTextures.Contains(meshes[m].TextureIndex))
                scene.SetVisible(m, !hide && meshes[m].Visible);
    }

    // ------------------------------------------------------------------ sun oil

    /// <summary>Puts her in an oil pose at the stage's nearest oil point; drags on the viewer then rub oil in.</summary>
    async Task StartOilAsync(OilPose pose)
    {
        if (_girl is not { } girl || _stage is null || IsBusy)
            return;
        IsBusy = true;
        HideDateHud();
        DsMenu? hudMenu = null;
        Dialogue.ShowMenu(T("oil.loading"), []);
        StopOil(restore: false);
        try
        {
            int level = Progress.Intimacy;
            var session = await Task.Run(() =>
            {
                _voices ??= new VoiceLibrary(_fs);
                hudMenu = LoadOilHudMenu();
                return OilSession.Start(_fs, _assembler, girl, pose, level, _voices, AudioSystem.Shared);
            });
            if (!ReferenceEquals(girl, _girl))
                return;
            CreateOilHud(hudMenu);
            session.Attach();
            session.Refused += _ => ShowStatus(F("oil.refused", CharacterDatabase.NameOf(girl.Girl.Id)), seconds: 3);
            session.ActionsOffered += OfferActions;
            session.SoundEffect += Sfx;
            _oil = session;
            UpdateMusic();
            _activity = null;
            _prop = null;
            PlaceForOil();
            BuildScene();
            TouchHandler = _oilTouch;
            _oilClock.Restart();
            _tanGain = 0;
            _oilTimer.Start();
            ShowStatus(F("oil.status", 0), seconds: 0);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException)
        {
            Status = F("oil.loadError", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
        if (_oil is not null)
            ShowOilMenu();
        else
            ShowMenu();
    }

    /// <summary>Ends the oil scene; with <paramref name="restore"/> she goes back to standing at her date point.</summary>
    void StopOil(bool restore)
    {
        if (_oil is not { } oil)
            return;
        _oilTimer.Stop();
        CancelOilActions();
        if (_memoryReplay is null)
            UnlockRandomAccessories();
        HideOilHud();
        TouchHandler = null;
        oil.SoundEffect -= Sfx;
        oil.End();
        oil.Detach();
        _oil = null;
        UpdateMusic();
        bool tanned = ApplyTan(oil);
        _statusHoldUntil = 0;
        oil.Girl.Animator.Face?.SetExpression(0, 0);
        if (!restore || _girl is not { } girl)
            return;
        if (girl.Girl.BodyAnimations.FirstOrDefault() is { } idle)
            girl.Animator.Body = _assembler.LoadBodyAnimation(girl.Model, idle);
        girl.Animator.Physics?.Reset();
        PlaceGirl();
        BuildScene();
        Status = F("play.where", SelectedGirl?.Name ?? "", StageNames[SelectedStageIndex], Times[SelectedTimeIndex]);
        if (tanned)
            _ = RebuildGirlAsync();
    }

    /// <summary>The tan the oil scene has added (the whitening creams: taken off) so far.</summary>
    double _tanGain;

    /// <summary>
    /// The tan gauge of the exe (sb_SunOilBase, 0x41F950): while the oil scene runs it gains ms × the oil's rate (lg00
    /// row 11 columns 2..8: sun block 0, the tan oils 0.5 / 1 / 2, the whitening creams 0.5 / 1 / 2) up to column 0
    /// (120000); out of the scene it is kept as 0..100. The exe tans (or whitens back to her own skin) where the oil is
    /// painted; the tan here being one level, it goes with how much of her skin is oiled.
    /// </summary>
    void Tan(Engine.Oil.OilSession oil, double seconds)
    {
        int kind = OilHud is not null ? _oilHudOil : oil.OilKind switch { 2 => 2, 1 => 5, _ => 0 };
        var (rates, max) = TanRates();
        if (kind <= 0 || kind >= rates.Length || max <= 0)
            return;
        double gain = seconds * 1000 * rates[kind] / max * oil.Coating.Coverage;
        _tanGain += kind <= 3 ? gain : -gain;
    }

    (double[] Rates, double Max)? _tanRates;

    (double[] Rates, double Max) TanRates()
    {
        if (_tanRates is { } known)
            return known;
        (double[], double) rates = ([0, 0.5, 1, 2, 0.5, 1, 2], 120000);
        try
        {
            var row = _fs.ReadTable(_assembler.Database.TableArchive, "lg00_00_00_00_00.lst").Rows.FirstOrDefault(r => r.Length > 9 && r[0] == "11");
            if (row is not null && double.TryParse(row[1], System.Globalization.CultureInfo.InvariantCulture, out double max))
                rates = (Enumerable.Range(3, 7).Select(c => double.TryParse(row[c], System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0).ToArray(), max);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
        }
        _tanRates = rates;
        return rates;
    }

    /// <summary>
    /// After an oil scene: the tan it gave (<see cref="Tan(Engine.Oil.OilSession, double)"/>) is kept, with the lines of
    /// the swimsuit she wears.
    /// </summary>
    bool ApplyTan(Engine.Oil.OilSession oil)
    {
        if (_girl is null)
            return false;
        double change = _tanGain;
        _tanGain = 0;
        var progress = Progress;
        double tan = Math.Clamp(progress.Tan + change, 0, 1);
        if (Math.Abs(tan - progress.Tan) < 0.01)
            return false;
        if (change > 0)
            progress.TanSwimsuitRow = progress.SwimsuitRow ?? _girl.Girl.Swimsuits.FirstOrDefault()?.Row;
        progress.Tan = tan;
        SaveProgress();
        return true;
    }

    /// <summary>The stage's oil point nearest to where she stands (moNN.xx has ten o01_N_Point_OilNN).</summary>
    void PlaceForOil()
    {
        if (_girl is null || _stage is null)
            return;
        var points = _stage.PointGroup("Oil");
        if (points.Count == 0)
        {
            PlaceGirl();
            return;
        }
        var here = _girl.Animator.Placement.Translation;
        var nearest = points.MinBy(p => Vector3.DistanceSquared(p.Transform.Translation, here));
        _girl.Animator.Placement = Upright(nearest.Transform);
        Scene?.NotifyChanged();
    }

    static string PoseName(int number) =>
        Translator.Instance[$"oil.pose{number}"] is var name && name != $"oil.pose{number}" ? name : Translator.Format("oil.poseN", number);

    void ShowOilPoses()
    {
        if (_girl is null)
            return;
        var options = _girl.Girl.OilPoses
            .Select(p => (PoseName(p.Number), (Action)(() => _ = StartOilAsync(p))))
            .ToList();
        options.Add((T("date.back"), () => { if (_oil is null) ShowMenu(); else ShowOilMenu(); }));
        Dialogue.ShowMenu(F("oil.choosePose", CharacterDatabase.NameOf(_girl.Girl.Id)), options);
    }

    void ShowOilMenu()
    {
        if (_girl is null || _oil is null)
        {
            ShowMenu();
            return;
        }
        if (OilHud is not null)
        {
            // The original menu (ds01_04) replaces the text menu.
            Dialogue.Hide();
            ShowOilHud();
            return;
        }
        var oil = _oil;
        bool pour = oil.Mode == OilMode.Pour;
        var options = new List<(string, Action)>
        {
            (pour ? T("oil.handMode") : T("oil.pourMode"), () => SetOilMode(pour ? OilMode.Hand : OilMode.Pour)),
        };
        if (pour && oil.OilKinds > 1)
            options.Add((F("oil.kind", oil.OilKind + 1, oil.OilKinds), () => CycleOil(1)));
        options.AddRange(new List<(string, Action)>
        {
            (T("oil.changePose"), ShowOilPoses),
            (TouchHandler is null ? T("oil.rubMode") : T("oil.cameraMode"), () =>
            {
                TouchHandler = TouchHandler is null ? _oilTouch : null;
                ShowOilMenu();
            }),
            (T("oil.finish"), () => Confirm(QuestionEndOil, "confirm.endOil", () =>
            {
                StopOil(restore: true);
                ShowMenu();
            }, ShowOilMenu)),
        });
        Dialogue.ShowMenu(F(pour ? "oil.menuPour" : "oil.menu", CharacterDatabase.NameOf(_girl.Girl.Id)), options);
    }

    void SetOilMode(OilMode mode)
    {
        if (_oil is null)
            return;
        _oil.Mode = mode;
        ShowOilMenu();
    }

    void CycleOil(int step)
    {
        if (_oil is not { OilKinds: > 0 } oil)
            return;
        oil.OilKind = (oil.OilKind + step + oil.OilKinds) % oil.OilKinds;
        ShowOilMenu();
    }

    /// <summary>The original's oil keys: Z pour, X hand, A / S previous / next oil. True when used.</summary>
    public bool HandleOilKey(Avalonia.Input.Key key)
    {
        if (key == Avalonia.Input.Key.Tab && InGame)
        {
            ToggleHud();
            return true;
        }
        if (_oil is null)
            return false;
        if (OilHud is not null)
        {
            // The original's keys as printed on the menu: A / S oil, Z / X tool; Tab brings a closed menu back.
            switch (key)
            {
                case Avalonia.Input.Key.A or Avalonia.Input.Key.S:
                    ChooseHudOil(_oilHudOil + (key == Avalonia.Input.Key.A ? -1 : 1));
                    return true;
                case Avalonia.Input.Key.Z or Avalonia.Input.Key.X:
                    ChooseHudTool(_oilHudTool + (key == Avalonia.Input.Key.Z ? -1 : 1));
                    return true;
                default:
                    return false;
            }
        }
        switch (key)
        {
            case Avalonia.Input.Key.Z:
                SetOilMode(OilMode.Pour);
                return true;
            case Avalonia.Input.Key.X:
                SetOilMode(OilMode.Hand);
                return true;
            case Avalonia.Input.Key.A:
                CycleOil(-1);
                return true;
            case Avalonia.Input.Key.S:
                CycleOil(1);
                return true;
            default:
                return false;
        }
    }

    void OilTick()
    {
        if (_oil is not { } oil)
            return;
        double seconds = Math.Min(0.1, _oilClock.Elapsed.TotalSeconds);
        _oilClock.Restart();
        oil.Update(seconds);
        Wet(oil.Coating.Coverage);
        Tan(oil, seconds);
        TickOilHud(seconds);
        SyncHand();
        if (Environment.TickCount64 >= _statusHoldUntil)
            Status = F("oil.status", (int)MathF.Round(oil.Coating.Coverage * 100));
    }

    /// <summary>Shows a message in the status line and keeps it there for a while (the oil coverage waits).</summary>
    void ShowStatus(string text, double seconds)
    {
        Status = text;
        _statusHoldUntil = Environment.TickCount64 + (long)(seconds * 1000);
    }

    void SyncHand()
    {
        if (Scene is not { } scene || _propObject < 0 || _oil is not { } oil)
            return;
        for (int i = 0; i < oil.Props.Count; i++)
            scene.SetObjectVisible(_propObject + i, oil.IsPropVisible(i));
    }

    /// <summary>Passes the viewer's touches to the oil scene.</summary>
    sealed class OilTouch(PlayViewModel owner) : ISceneTouchHandler
    {
        public bool Begin(Ray ray, Vector3 up)
        {
            bool taken = owner._oil?.Begin(ray, up) == true;
            owner.SyncHand();
            return taken;
        }

        public void Move(Ray ray, Vector3 up)
        {
            owner._oil?.Move(ray, up);
            owner.SyncHand();
        }

        public void End()
        {
            owner._oil?.End();
            owner.SyncHand();
        }
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
        // One of the points the exe allows for the activity, at random (those the stage really has).
        var names = _stage.PointGroup("Date").Select(p => p.Name).ToList();
        var candidates = Unlocks.DatePointsOf(_stage.Variant.Stage, activity.Number)
            .Select(n => names.IndexOf($"o01_N_Point_Date{n:00}")).Where(i => i >= 0).ToList();
        _point = candidates.Count > 0 ? candidates[Random.Shared.Next(candidates.Count)] : activity.Number - 1;
        _activity = activity;
        _girl.Animator.Physics?.Reset();
        PlaceGirl();
        BuildScene();
    }


    void ShowSwimsuits()
    {
        if (_girl is null)
            return;
        HideDateHud();
        if (HasOriginalWardrobe)
        {
            OpenWardrobe();
            return;
        }
        var options = _girl.Girl.Swimsuits
            .Select(s => (s.Name, (Action)(() => _ = ChangeSwimsuitAsync(s.Row))))
            .ToList();
        options.Add((T("date.back"), ShowMenu));
        Dialogue.ShowMenu(F("swimsuit.choose", CharacterDatabase.NameOf(_girl.Girl.Id)), options);
    }

    /// <summary>Rebuilds the girl in another swimsuit, keeping where she is and what she does.</summary>
    /// <param name="showMenu">False while trying swimsuits on in the original screen (it stays open).</param>
    async Task ChangeSwimsuitAsync(int row, bool showMenu = true)
    {
        if (_girl is not { } current || IsBusy)
            return;
        IsBusy = true;
        if (showMenu)
            Dialogue.ShowMenu(T("swimsuit.changing"), []);
        try
        {
            var girl = await Task.Run(() => _assembler.Build(SpecFor(current.Girl.Id, row)));
            _girl = girl;
            Progress.SwimsuitRow = row;
            SaveProgress();
            if (_activity is { } activity)
                StartActivity(activity);
            else
            {
                PlaceGirl();
                BuildScene();
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            Status = F("swimsuit.error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
        if (showMenu)
            ShowMenu();
    }

    static string ActivityName(int number) =>
        Translator.Instance[$"date.activity{number}"] is var name && name != $"date.activity{number}" ? name : Translator.Format("date.activityN", number);

    void ShowActivities()
    {
        if (_girl is null)
            return;
        HideDateHud();
        // Like the exe, only the activities her level allows on this stage; "her choice" draws one the exe's way.
        var pool = _stage is { } stage ? Unlocks.DateActivityPool(stage.Variant.Stage, Progress.Intimacy) : null;
        var allowed = _girl.Girl.DateActivities.Where(a => pool is null || pool.Contains(a.Number)).ToList();
        var options = allowed
            .Select(a => (ActivityName(a.Number), (Action)(() => { StartActivity(a); ShowMenu(); })))
            .ToList();
        if (pool is { Count: > 0 } && allowed.Count > 0)
            options.Insert(0, (T("date.herChoice"), () =>
            {
                var picks = pool.Where(n => allowed.Any(a => a.Number == n)).ToList();
                int number = picks[Random.Shared.Next(picks.Count)];
                StartActivity(allowed.First(a => a.Number == number));
                ShowMenu();
            }));
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
    readonly Dictionary<string, LightSet?> _lightFiles = [];
    readonly Dictionary<string, StageParticles?> _particleFiles = [];

    /// <summary>
    /// The stage objects (object 0 of the scene) that may be hidden in front of the camera now: the ld02_02 flag of the
    /// oil scene or of the date point she stands at. The exe indexes the date flags with a byte of its date state that
    /// is not decoded; the point number stands in for it.
    /// </summary>
    IEnumerable<(int[] Meshes, Vector2[] Footprint)> ObstaclesFor(Stage stage)
    {
        var rig = stage.Model.Rig;
        foreach (var (obstacle, footprint) in stage.Obstacles)
        {
            bool allowed = _oil is not null ? obstacle.Oil[0] : obstacle.Date[Math.Clamp(_point, 0, obstacle.Date.Length - 1)];
            int frame = rig.Find(obstacle.Frame);
            if (!allowed || frame < 0)
                continue;
            var meshes = new List<int>();
            for (int m = 0; m < stage.Model.Meshes.Count; m++)
            {
                int f = stage.Model.Meshes[m].FrameIndex;
                while (f >= 0 && f != frame)
                    f = rig.Parents[f];
                if (f == frame)
                    meshes.Add(m);
            }
            if (meshes.Count > 0)
                yield return (meshes.ToArray(), footprint);
        }
    }

    RenderModel? _cameraMarker;
    bool _cameraMarkerMissing;

    /// <summary>cm00 (ld00 row 33), the camera target marker, loaded once.</summary>
    RenderModel? CameraMarker()
    {
        if (_cameraMarker is null && !_cameraMarkerMissing)
        {
            try
            {
                var xx = _fs.ReadModel("sb3_0100.pp", "cm00_00_00_00_00.xx");
                _cameraMarker = RenderModel.Build("cm00", [new RenderPart("cm00", xx, xx.Root)]);
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException)
            {
                _cameraMarkerMissing = true;
            }
        }
        return _cameraMarker;
    }

    /// <summary>The stage variant's .glare, or null when it has none (night beach, pool...).</summary>
    StageGlare? StageGlareFor(Stage stage)
    {
        if (stage.Variant.Glare is not { } asset)
            return null;
        try { return StageGlare.FromFile(OpenDelulu.Formats.Scene.GlareFile.Read(_fs.ReadReferenced(asset.Archive, asset.File))); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FileNotFoundException) { return null; }
    }

    /// <summary>The stage variant's .ptcl sprites (cached), or null when it has none.</summary>
    StageParticles? StageParticlesFor(Stage stage)
    {
        if (stage.Variant.Particles is not { } asset)
            return null;
        string key = asset.Archive + "/" + asset.File;
        if (!_particleFiles.TryGetValue(key, out var particles))
        {
            try { particles = StageParticles.Load(OpenDelulu.Formats.Scene.PtclFile.Read(_fs.ReadReferenced(asset.Archive, asset.File)), asset.File); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { particles = null; }
            _particleFiles[key] = particles;
        }
        return particles;
    }

    /// <summary>The girl's lights for a time of day (ld01 rows 41..45), or null when the table has none.</summary>
    LightSet? CharacterLights(GirlInfo girl, StageTime time)
    {
        int index = Math.Clamp((int)time - 1, 0, 4);
        if (index >= girl.Lights.Count || girl.Lights[index] is not { } asset)
            return null;
        string key = asset.Archive + "/" + asset.File;
        if (!_lightFiles.TryGetValue(key, out var lights))
        {
            try { lights = LightSet.FromXl(OpenDelulu.Formats.Scene.XlFile.Read(_fs.ReadReferenced(asset.Archive, asset.File))); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { lights = null; }
            _lightFiles[key] = lights;
        }
        return lights;
    }

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

    partial void OnIsTitleVisibleChanged(bool value) => UpdateMusic();
    partial void OnIsLobbyVisibleChanged(bool value) => UpdateMusic();
    partial void OnIsAlbumOpenChanged(bool value) => UpdateMusic();
    partial void OnIsMemoryOpenChanged(bool value) => UpdateMusic();
    partial void OnIsTicketVisibleChanged(bool value) => UpdateMusic();

    /// <summary>The screen being shown, for its music: the title, album and lobby over the island; else the stage.</summary>
    MusicScene CurrentMusicScene =>
        IsTicketVisible ? MusicScene.Ticket
        : IsTitleVisible ? MusicScene.Title
        : IsAlbumOpen ? MusicScene.EventCg
        : IsMemoryOpen ? MusicScene.Memories
        : IsLobbyVisible ? MusicScene.Lobby
        : _stage is null ? MusicScene.Map
        : _oil is not null ? MusicScene.Oil
        : _couple is FeraSession ? MusicScene.Fera
        : _couple is HapSession ? MusicScene.Hap
        : _couple is not null ? MusicScene.H
        : MusicScene.Date;

    /// <summary>
    /// Plays the music and ambience the exe chooses for the current screen (<see cref="SceneMusic"/>); a track that is
    /// already playing keeps playing.
    /// </summary>
    void UpdateMusic()
    {
        var scene = CurrentMusicScene;
        int stage = _stage?.Variant.Stage ?? SelectedStageIndex + 1;
        var time = _stage?.Variant.Time ?? Time;
        var (music, ambience) = SceneMusic.For(scene, stage, time);
        var audio = AudioSystem.Shared;
        try
        {
            if (music > 0 && _fs.ExistsReferenced(SceneMusic.Archive, SceneMusic.MusicFile(music)))
                audio.PlayMusic(_fs, SceneMusic.Archive, SceneMusic.MusicFile(music));
            else
                audio.StopMusic();
            if (ambience > 0 && _fs.ExistsReferenced(SceneMusic.Archive, SceneMusic.AmbienceFile(ambience)))
                audio.PlayAmbient(_fs, SceneMusic.Archive, SceneMusic.AmbienceFile(ambience));
            else
                audio.StopAmbient();
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
        }
    }

    // ------------------------------------------------------------------ events

    GirlProgress Progress => _progress.For(_girl!.Girl.Id);

    /// <summary>
    /// What to build for a girl: her swimsuit and the accessories she wears, less those CONFIG hides (accessories,
    /// pareo).
    /// </summary>
    CharacterSpec SpecFor(int girlId, int? swimsuitRow)
    {
        var config = Services.GameConfig.Current;
        var worn = _progress.For(girlId).Look.Accessories
            .Where(a => _assembler.Accessories.Slot(a.Key) is { } slot && (slot.IsPareo ? config.Pareo : config.Accessories))
            .ToDictionary(a => a.Key, a => a.Value);
        var progress = _progress.For(girlId);
        return new CharacterSpec(girlId, SwimsuitRow: swimsuitRow, Accessories: worn, Tan: progress.Tan, TanSwimsuitRow: progress.TanSwimsuitRow);
    }

    void Meet()
    {
        var progress = Progress;
        var kind = !progress.MetAtLevel ? EventKind.FirstMeeting : IsNight ? EventKind.GreetingNight : EventKind.Greeting;
        RunEvent(kind, () =>
        {
            if (kind == EventKind.FirstMeeting)
            {
                progress.MetAtLevel = true;
                SaveProgress();
            }
            ShowMenu();
        });
    }

    void RunEvent(EventKind kind, Action then)
    {
        HideDateHud();
        var girl = _girl!.Girl.Id;
        if (GameEvents.Find(_library, girl, Progress.Intimacy, kind) is { } script)
        {
            // Talking: face to face, close enough to see her expressions; back to the scene's view afterwards.
            if (AutoCamera)
                Scene?.RequestCamera(new CameraView(Pitch: 0.05f, Zoom: 0.42f));
            // 首と目で見る: while talking she turns her head to the player too.
            if (_girl?.Animator.Neck is { } neck)
                neck.Active = true;
            Dialogue.Intimacy = Progress.Intimacy;
            Dialogue.Run(script, () =>
            {
                if (_girl?.Animator.Neck is { } n)
                    n.Active = false;
                if (AutoCamera)
                    Scene?.RequestCamera(null);
                then();
            });
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
        };
        // The original date menu (ds01_03) has clothes, oil, H and goodbye; the text menu keeps the rest.
        var hud = EnsureDateHud();
        if (hud is null)
        {
            options.Add((T("play.oil"), ShowOilPoses));
            options.Add((T("play.changeSwimsuit"), ShowSwimsuits));
        }
        options.Add((T("play.move"), () => { _point++; PlaceGirl(); ShowMenu(); }));
        if (hud is null)
        {
            if (CanLeadIn)
                options.Add((F("play.nextLevel", progress.Intimacy + 1), LeadIn));
            options.Add((T("play.goodbye"), Goodbye));
        }
        Dialogue.ShowMenu(F("play.menu", CharacterDatabase.NameOf(girl), progress.Intimacy), options);
        hud?.Show();
        ShowCameraHud();
        ShowSystemHud();
    }

    /// <summary>Her level has a lead-in to the next one (an H scene from level 2 on).</summary>
    bool CanLeadIn
    {
        get
        {
            if (_girl is null)
                return false;
            int girl = _girl.Girl.Id;
            int maxLevel = GameEvents.Levels(_library, girl).DefaultIfEmpty(1).Max();
            // Her love has to be full first (it starts again from 0 at each level, as the original saves show); games
            // from before the unlocks keep the old free lead-in.
            bool loveFull = _progress.Swimsuits is null || Progress.Love >= 255;
            return loveFull && Progress.Intimacy < maxLevel && GameEvents.Find(_library, girl, Progress.Intimacy, EventKind.LeadIn) is not null;
        }
    }

    void LeadIn()
    {
        var progress = Progress;
        RunEvent(EventKind.LeadIn, () =>
        {
            // From level 2 on the lead-in leads to the H scene; the level goes up when it ends.
            if (progress.Intimacy >= 2 && _stage?.PointGroup("H").Count > 0)
            {
                _ = StartHAsync(0, levelUp: true);
                return;
            }
            progress.LevelUp();
            ApplyRank();
            SaveProgress();
            Meet();
        });
    }

    void Goodbye() => Confirm(QuestionEndDate, "confirm.endDate", () => RunEvent(EventKind.Parting, () => { DateDone(); SaveProgress(); Leave(); }), ShowMenu);
}
