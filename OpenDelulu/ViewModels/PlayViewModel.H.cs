using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Engine;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Engine.H;
using OpenDelulu.Engine.Hud;
using OpenDelulu.Rendering;

namespace OpenDelulu.ViewModels;

/// <summary>
/// The H scene (08_h), the fera scene (09_fera) and the Plus fetish scenes (10_hap), with their original menus:
/// <list type="bullet">
/// <item>ds01_05 (H): Insert (vagina, anus, pull out), Cum (outside, inside), Position (eight: the four hwBB_PP files ×
/// their A/B positions), Fetish (fera, paizuri, footjob, 69) and Exit (question 8, "End the Ecchi Session?"). A press on
/// the scene strokes while she is inserted (held: strokes go on); at う！待機 a press finishes as Cum says.</item>
/// <item>ds01_06 (fera): Part (whole, glans, underside, tip, balls, let her choose), hand or mouth, spit out or swallow,
/// Exit (question 9) back to the H scene. Holding a press makes her go strong.</item>
/// <item>ds01_07 (fetish): Tit Job, Foot Job, 69 and やめる (back to the H scene); a press on the scene goes to the next
/// pace, he comes by himself.</item>
/// </list>
/// The menus' A..D variants only open the lists towards another corner; the scenes use A, top left like the oil menu.
/// </summary>
public sealed partial class PlayViewModel
{
    static readonly string[] HHudInsert = ["Asoko", "Osiri", "Nuku"];
    static readonly string[] HHudEjaculation = ["Soto", "Naka"];
    static readonly string[] HHudPositions = ["Seijo", "Kijo", "Kouhai", "Haimen", "Tati", "Gyakueki", "Dan", "Eki"];
    static readonly string[] HHudFetishes = ["Fera", "Paizuri", "Asikoki", "Sixnine"];
    static readonly string[] FeraHudParts = ["Zentai", "Kame", "Ura", "Saki", "Tama", "Omakase"];
    static readonly string[] FeraHudHands = ["Te", "Kuti"];
    static readonly string[] FeraHudEnds = ["Dasasu", "Nomasu"];

    CoupleSession? _couple;
    bool _hLevelUp;
    int _hHudGroup, _hPosition, _hHole = 1;
    bool _hInside;
    int _feraPart = -1;
    bool _feraMouth, _feraSwallow = true;
    DsMenu? _hMenu, _feraMenu, _hapMenu;
    bool _hMenusLoaded;
    readonly DispatcherTimer _hTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly Stopwatch _hClock = new();
    ISceneTouchHandler? _hTouch;

    [ObservableProperty] public partial DsHud? HHud { get; private set; }
    [ObservableProperty] public partial DsHud? FeraHud { get; private set; }
    [ObservableProperty] public partial DsHud? HapHud { get; private set; }

    /// <summary>The H / fera / fetish scene going on, or null.</summary>
    public CoupleSession? Couple => _couple;

    public HSession? H => _couple as HSession;

    /// <summary>The menu of the scene going on.</summary>
    DsHud? CoupleHud => _couple switch
    {
        HSession => HHud,
        FeraSession => FeraHud,
        HapSession => HapHud,
        _ => null,
    };

    /// <summary>Starts the H scene in position <paramref name="position"/> (0..7, the menu's order); with
    /// <paramref name="levelUp"/> (after a lead-in) her level goes up when the H ends.</summary>
    Task StartHAsync(int position, bool levelUp)
    {
        _hLevelUp |= levelUp;
        int level = _memoryReplay == 0 ? _memoryHLevel : _girl is null ? 1 : Progress.Intimacy;
        return StartCoupleAsync(girl => HSession.Start(_fs, _assembler, girl, position / 2 + 1, level, _voices, AudioSystem.Shared),
            () => _hPosition = position);
    }

    Task StartFeraAsync()
    {
        int level = Progress.Intimacy;
        _feraPart = -1;
        return StartCoupleAsync(girl => FeraSession.Start(_fs, _assembler, girl, level, _voices, AudioSystem.Shared), null);
    }

    Task StartHapAsync(int kind)
    {
        int level = Progress.Intimacy;
        return StartCoupleAsync(girl => HapSession.Start(_fs, _assembler, girl, kind, level, _voices, AudioSystem.Shared), null);
    }

    /// <summary>
    /// Loads a scene (on a worker thread) and puts her and the man on the stage's H point nearest to her; the scene's
    /// menu and the touches replace the date's.
    /// </summary>
    async Task StartCoupleAsync(Func<Character, CoupleSession> load, Action? started)
    {
        if (_girl is not { } girl || _stage is null || IsBusy)
            return;
        IsBusy = true;
        HideDateHud();
        StopOil(restore: false);
        Dialogue.ShowMenu(T("oil.loading"), []);
        bool had = _couple is not null;
        try
        {
            var session = await Task.Run(() =>
            {
                _voices ??= new VoiceLibrary(_fs);
                LoadCoupleMenus();
                return load(girl);
            });
            if (!ReferenceEquals(girl, _girl))
                return;
            var placement = _couple is not null ? _couple.Girl.Animator.Placement : HPlacement();
            EndCouple();
            CreateCoupleHuds();
            started?.Invoke();
            _couple = session;
            session.Attach(placement);
            _activity = null;
            _prop = null;
            UpdateMusic();
            BuildScene();
            _hTouch ??= new CoupleTouch(this);
            TouchHandler = _hTouch;
            _hTimer.Tick -= OnCoupleTick;
            _hTimer.Tick += OnCoupleTick;
            _hClock.Restart();
            _hTimer.Start();
            _statusHoldUntil = 0;
            Dialogue.Hide();
            ShowCoupleHud();
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException)
        {
            ShowStatus(F("h.loadError", ex.Message), seconds: 4);
            if (had)
                ShowCoupleHud();
            else
                ShowMenu();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>ds01_05..07, once (on the loading thread).</summary>
    void LoadCoupleMenus()
    {
        if (_hMenusLoaded)
            return;
        _hMenusLoaded = true;
        DsMenu? Load(string name)
        {
            try { return DsMenu.Load(_fs, name); }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException) { return null; }
        }
        _hMenu = Load("ds01_05_00_00_00");
        _feraMenu = Load("ds01_06_00_00_00");
        _hapMenu = Load("ds01_07_00_00_00");
    }

    /// <summary>The stage's H point nearest to where she stands (Point_H01..10), upright.</summary>
    Matrix4x4 HPlacement()
    {
        var here = _girl?.Animator.Placement.Translation ?? Vector3.Zero;
        var points = _stage?.PointGroup("H") ?? [];
        return points.Count == 0 ? _girl?.Animator.Placement ?? Matrix4x4.Identity
            : Upright(points.MinBy(p => Vector3.DistanceSquared(p.Transform.Translation, here)).Transform);
    }

    /// <summary>Stops the scene going on, if any (menu, touches, timer); she keeps her pose.</summary>
    void EndCouple()
    {
        if (_couple is not { } couple)
            return;
        _hTimer.Stop();
        CoupleHud?.Hide();
        TouchHandler = null;
        couple.Detach();
        _couple = null;
    }

    /// <summary>Ends the H scene: she stands at her date point again; after a lead-in her level goes up.</summary>
    void StopH()
    {
        if (_couple is null)
            return;
        if (_memoryReplay == 0)
        {
            // An H memory: back to the memory screen.
            Leave();
            return;
        }
        GainLove(30);
        UnlockRandomAccessories();
        EndCouple();
        UpdateMusic();
        _statusHoldUntil = 0;
        if (_girl is { } girl)
        {
            if (girl.Girl.BodyAnimations.FirstOrDefault() is { } idle)
                girl.Animator.Body = _assembler.LoadBodyAnimation(girl.Model, idle);
            girl.Animator.Physics?.Reset();
        }
        PlaceGirl();
        BuildScene();
        Status = F("play.where", SelectedGirl?.Name ?? "", StageNames[SelectedStageIndex], Times[SelectedTimeIndex]);
        if (_hLevelUp)
        {
            _hLevelUp = false;
            Progress.LevelUp();
            ApplyRank();
            SaveProgress();
            Meet();
        }
        else
            ShowMenu();
    }

    void OnCoupleTick(object? sender, EventArgs e)
    {
        if (_couple is not { } couple)
            return;
        double seconds = Math.Min(0.1, _hClock.Elapsed.TotalSeconds);
        _hClock.Restart();
        couple.Update(seconds);
        if (couple is HSession)
            Wet(couple.Excitement);
        SyncLiquid();
        CoupleHud?.Tick(seconds);
        if (Environment.TickCount64 < _statusHoldUntil)
            return;
        // CONFIG's Help: the hints of what to press (the exe's help labels the touch actions in these scenes).
        string hint = !Services.GameConfig.Current.ShowHelp ? "" : couple switch
        {
            HSession { State: HState.Standby } => T("h.hintStandby"),
            HSession { State: HState.Inserted or HState.Moving } => T("h.hintInserted"),
            HSession { State: HState.Ready } => T("h.hintReady"),
            FeraSession { Working: false } => T("h.hintFera"),
            FeraSession => T("h.hintFeraWorking"),
            HapSession { Pace: < 2 } => T("h.hintHap"),
            _ => "",
        };
        Status = F("h.status", (int)Math.Round(couple.Excitement * 100), hint).TrimEnd(' ', '—');
    }

    // ------------------------------------------------------------------ menus

    void CreateCoupleHuds()
    {
        DsHud Make(DsMenu menu, int icons, Func<string> state, string minimized, Func<string, bool> isChecked, Func<string, Bitmap?> image,
            Action<string> pressed)
        {
            var hud = new DsHud(menu, 96 + 200, 24 + icons * 96)
            {
                State = state,
                MinimizedState = minimized,
                IsChecked = isChecked,
                ImageFor = image,
            };
            hud.Pressed += pressed;
            hud.Move(0, 24); // below the system menu bar
            hud.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DsHud.IsClosed))
                    OnPropertyChanged(nameof(IsHudClosed));
            };
            return hud;
        }
        if (HHud is null && _hMenu is { } h)
            HHud = Make(h, 5, () => (_hHudGroup switch { 1 => "挿入表示", 2 => "射精表示", 3 => "体位変更表示", 4 => "ハプニング表示", _ => "シーン表示" }) + "A",
                "シーン最小化A",
                key => Array.IndexOf(HHudInsert, key) is var i and >= 0 ? i == HHudInsertSlot
                    : Array.IndexOf(HHudEjaculation, key) is var j and >= 0 ? j == (_hInside ? 1 : 0)
                    : Array.IndexOf(HHudPositions, key) is var p and >= 0 && p == _hPosition,
                HHudImage, PressHHud);
        if (FeraHud is null && _feraMenu is { } f)
            FeraHud = Make(f, 4, () => (_hHudGroup switch { 1 => "部位表示", 2 => "手、口表示", 3 => "射精表示", _ => "シーン表示" }) + "A",
                "シーン最小化A",
                key => Array.IndexOf(FeraHudParts, key) is var i and >= 0 ? i == _feraPart
                    : Array.IndexOf(FeraHudHands, key) is var j and >= 0 ? j == (_feraMouth ? 1 : 0)
                    : Array.IndexOf(FeraHudEnds, key) is var k and >= 0 && k == (_feraSwallow ? 1 : 0),
                FeraHudImage, PressFeraHud);
        if (HapHud is null && _hapMenu is { } p)
            HapHud = Make(p, 4, () => "シーン表示", "シーン最小化", _ => false, _ => null, PressHapHud);
    }

    int HHudInsertSlot => H?.Inserted is { } p ? p[1] - '1' : 2;

    Bitmap? HHudImage(string key)
    {
        if (Array.IndexOf(HHudInsert, key) is var insert and >= 0)
            return DsItemTexture("ds01_05", 1, insert, 1);
        if (Array.IndexOf(HHudEjaculation, key) is var ejaculation and >= 0)
            return DsItemTexture("ds01_05", 2, ejaculation, 1);
        if (Array.IndexOf(HHudPositions, key) is var position and >= 0)
            return DsItemTexture("ds01_05", 3, position, 1);
        return key switch
        {
            "HIcon01" => DsItemTexture("ds01_05", 1, HHudInsertSlot, 2),
            "HIcon03" => DsItemTexture("ds01_05", 3, _hPosition, 2),
            _ => null,
        };
    }

    Bitmap? FeraHudImage(string key)
    {
        if (Array.IndexOf(FeraHudParts, key) is var part and >= 0)
            return DsItemTexture("ds01_06", 1, part, 1);
        if (Array.IndexOf(FeraHudHands, key) is var hand and >= 0)
            return DsItemTexture("ds01_06", 2, hand, 1);
        if (Array.IndexOf(FeraHudEnds, key) is var end and >= 0)
            return DsItemTexture("ds01_06", 3, end, 1);
        // Icons: the chosen item's picture (_02), or ds01_06_GG_00_01 before a choice.
        return key switch
        {
            "FIcon01" => DsItemTexture("ds01_06", 1, _feraPart, _feraPart < 0 ? 1 : 2),
            "FIcon02" => DsItemTexture("ds01_06", 2, _feraMouth ? 1 : 0, 2),
            _ => null,
        };
    }

    void ShowCoupleHud()
    {
        if (CoupleHud is not { } hud)
            return;
        _hHudGroup = 0;
        hud.Show();
        ShowCameraHud();
        ShowSystemHud();
    }

    void RefreshCoupleHud()
    {
        _hHudGroup = 0;
        CoupleHud?.Play(instant: false);
    }

    /// <summary>An icon unfolds its list; the same icon folds it.</summary>
    bool ToggleGroup(string key, string prefix)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal) || CoupleHud is not { } hud)
            return false;
        int group = key[^1] - '0';
        _hHudGroup = _hHudGroup == group ? 0 : group;
        hud.Play(instant: false);
        return true;
    }

    void PressHHud(string key)
    {
        if (H is not { } h || IsBusy)
            return;
        if (key != "HIcon05" && ToggleGroup(key, "HIcon"))
            return;
        switch (key)
        {
            case "HIcon05":
                Confirm(QuestionEndH, "confirm.endH", StopH, ShowCoupleHud);
                return;
            case "Asoko" or "Osiri":
                _hHole = key == "Asoko" ? 1 : 2;
                h.Insert(HLetter, _hHole);
                RefreshCoupleHud();
                return;
            case "Nuku":
                h.PullOut();
                RefreshCoupleHud();
                return;
            case "Soto" or "Naka":
                _hInside = key == "Naka";
                if (h.State == HState.Ready)
                    FinishH();
                RefreshCoupleHud();
                return;
        }
        if (Array.IndexOf(HHudPositions, key) is var position and >= 0)
        {
            RefreshCoupleHud();
            ChooseHPosition(position);
        }
        else if (Array.IndexOf(HHudFetishes, key) is var fetish and >= 0)
        {
            RefreshCoupleHud();
            if (fetish == 0)
                _ = StartFeraAsync();
            else if (_girl is { } girl && HapSession.IsKindInstalled(_fs, _assembler, girl.Girl.Id, fetish))
                _ = StartHapAsync(fetish);
            else
                ShowStatus(T("h.needsPlus"), seconds: 3);
        }
    }

    char HLetter => _hPosition % 2 == 0 ? 'A' : 'B';

    /// <summary>Another position: in the same file the scene's own change clips, otherwise the other file's scene.</summary>
    void ChooseHPosition(int position)
    {
        if (H is not { } h || position == _hPosition)
            return;
        if (position / 2 + 1 == h.File)
        {
            _hPosition = position;
            if (h.Inserted is not null)
                h.Insert(HLetter, _hHole);
            HHud?.Play(instant: false);
            return;
        }
        _ = StartHAsync(position, levelUp: false);
    }

    void FinishH()
    {
        if (H is not { } h)
            return;
        if (_hInside)
            h.ComeInside();
        else
            h.ComeOutside(HSession.OutsideTargets[Random.Shared.Next(HSession.OutsideTargets.Length)]);
    }

    void PressFeraHud(string key)
    {
        if (_couple is not FeraSession fera || IsBusy)
            return;
        if (key != "FIcon04" && ToggleGroup(key, "FIcon"))
            return;
        if (key == "FIcon04")
        {
            Confirm(QuestionEndFera, "confirm.endFera", () => _ = StartHAsync(_hPosition, levelUp: false), ShowCoupleHud);
            return;
        }
        if (Array.IndexOf(FeraHudParts, key) is var part and >= 0)
            _feraPart = part;
        else if (Array.IndexOf(FeraHudHands, key) is var hand and >= 0)
            _feraMouth = hand == 1;
        else if (Array.IndexOf(FeraHudEnds, key) is var end and >= 0)
            fera.Swallow = _feraSwallow = end == 1;
        if (_feraPart >= 0 && Array.IndexOf(FeraHudEnds, key) < 0)
            fera.Choose((FeraPart)_feraPart, _feraMouth);
        RefreshCoupleHud();
    }

    /// <summary>ds01_07: HaIcon01..03 switch between paizuri, footjob and 69; HaIcon04 (やめる) goes back to the H scene.</summary>
    void PressHapHud(string key)
    {
        if (_couple is not HapSession hap || IsBusy)
            return;
        if (key == "HaIcon04")
        {
            Confirm(QuestionEndH, "confirm.endH", () => _ = StartHAsync(_hPosition, levelUp: false), ShowCoupleHud);
            return;
        }
        if (key.StartsWith("HaIcon", StringComparison.Ordinal) && key[^1] - '0' is var kind and >= 1 and <= 3 && kind != hap.Kind)
        {
            if (_girl is { } girl && HapSession.IsKindInstalled(_fs, _assembler, girl.Girl.Id, kind))
                _ = StartHapAsync(kind);
            else
                ShowStatus(T("h.needsPlus"), seconds: 3);
        }
    }

    /// <summary>
    /// A press on the scene: in H it strokes while she is inserted (held, they go on) and at う！待機 it finishes; in fera
    /// holding it makes her go strong; in a fetish scene it goes to the next pace.
    /// </summary>
    sealed class CoupleTouch(PlayViewModel owner) : ISceneTouchHandler
    {
        public bool Begin(Ray ray, Vector3 up)
        {
            switch (owner._couple)
            {
                case HSession { State: HState.Inserted or HState.Moving } h:
                    h.Continuous = true;
                    h.Stroke();
                    return true;
                case HSession { State: HState.Ready }:
                    owner.FinishH();
                    return true;
                case FeraSession { Working: true } fera:
                    fera.Strong = true;
                    return true;
                case HapSession hap:
                    return hap.Faster();
                default:
                    return false;
            }
        }

        public void Move(Ray ray, Vector3 up)
        {
        }

        public void End()
        {
            switch (owner._couple)
            {
                case HSession h:
                    h.Continuous = false;
                    break;
                case FeraSession fera:
                    fera.Strong = false;
                    break;
            }
        }
    }
}
