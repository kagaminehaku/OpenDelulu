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
/// The H scene (08_h) and its original menu ds01_05: five 96×96 icons, Insert (vagina, anus, pull out), Ejaculation
/// (outside, inside), Position (eight: the four hwBB_PP files × their A/B positions), Happening (fera, paizuri, footjob,
/// 69: the 09_fera / 10_hap scenes) and Exit (question 8, "End the Ecchi Session?"). A press on the scene strokes while
/// she is inserted (held: strokes go on); when he is about to come a press finishes as the Ejaculation choice says. The
/// menu's A..D variants only open the lists towards another corner; the scene uses A, top left like the oil menu.
/// </summary>
public sealed partial class PlayViewModel
{
    static readonly string[] HHudInsert = ["Asoko", "Osiri", "Nuku"];
    static readonly string[] HHudEjaculation = ["Soto", "Naka"];
    static readonly string[] HHudPositions = ["Seijo", "Kijo", "Kouhai", "Haimen", "Tati", "Gyakueki", "Dan", "Eki"];
    static readonly string[] HHudHappenings = ["Fera", "Paizuri", "Asikoki", "Sixnine"];

    HSession? _h;
    bool _hLevelUp, _hHudLoadFailed;
    int _hHudGroup, _hPosition, _hHole = 1;
    bool _hInside;
    readonly DispatcherTimer _hTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly Stopwatch _hClock = new();
    ISceneTouchHandler? _hTouch;

    /// <summary>The original H menu, once loaded (null when the game lacks ds01_05).</summary>
    [ObservableProperty] public partial DsHud? HHud { get; private set; }

    /// <summary>The H scene going on, or null.</summary>
    public HSession? H => _h;

    /// <summary>
    /// Starts the H scene in position <paramref name="position"/> (0..7, the menu's order) at the H point nearest to her;
    /// with <paramref name="levelUp"/> (after a lead-in) her level goes up when it ends.
    /// </summary>
    async Task StartHAsync(int position, bool levelUp)
    {
        if (_girl is not { } girl || _stage is null || IsBusy)
            return;
        IsBusy = true;
        HideDateHud();
        StopOil(restore: false);
        Dialogue.ShowMenu(T("oil.loading"), []);
        var previous = _h;
        DsMenu? hudMenu = null;
        try
        {
            int level = Progress.Intimacy;
            int file = position / 2 + 1;
            var session = await Task.Run(() =>
            {
                _voices ??= new VoiceLibrary(_fs);
                if (HHud is null && !_hHudLoadFailed)
                {
                    try { hudMenu = DsMenu.Load(_fs, "ds01_05_00_00_00"); }
                    catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException) { _hHudLoadFailed = true; }
                }
                return HSession.Start(_fs, _assembler, girl, file, level, _voices, AudioSystem.Shared);
            });
            if (!ReferenceEquals(girl, _girl))
                return;
            previous?.Detach();
            CreateHHud(hudMenu);
            _hPosition = position;
            _hLevelUp |= levelUp;
            _h = session;
            session.Attach(HPlacement());
            _activity = null;
            _prop = null;
            UpdateMusic();
            BuildScene();
            _hTouch ??= new HTouch(this);
            TouchHandler = _hTouch;
            _hTimer.Tick -= OnHTick;
            _hTimer.Tick += OnHTick;
            _hClock.Restart();
            _hTimer.Start();
            Dialogue.Hide();
            ShowHHud();
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException)
        {
            Status = F("h.loadError", ex.Message);
            if (previous is null)
                ShowMenu();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The stage's H point nearest to where she stands (Point_H01..10), upright.</summary>
    Matrix4x4 HPlacement()
    {
        var here = _girl?.Animator.Placement.Translation ?? Vector3.Zero;
        var points = _stage?.PointGroup("H") ?? [];
        return points.Count == 0 ? _girl?.Animator.Placement ?? Matrix4x4.Identity
            : Upright(points.MinBy(p => Vector3.DistanceSquared(p.Transform.Translation, here)).Transform);
    }

    /// <summary>Ends the H scene: she stands at her date point again; after a lead-in her level goes up.</summary>
    void StopH()
    {
        if (_h is not { } h)
            return;
        _hTimer.Stop();
        HHud?.Hide();
        TouchHandler = null;
        h.Detach();
        _h = null;
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
            SaveProgress();
            Meet();
        }
        else
            ShowMenu();
    }

    void OnHTick(object? sender, EventArgs e)
    {
        if (_h is not { } h)
            return;
        double seconds = Math.Min(0.1, _hClock.Elapsed.TotalSeconds);
        _hClock.Restart();
        HHud?.Tick(seconds);
        if (Environment.TickCount64 < _statusHoldUntil)
            return;
        string hint = h.State switch
        {
            HState.Standby => T("h.hintStandby"),
            HState.Inserted or HState.Moving => T("h.hintInserted"),
            HState.Ready => T("h.hintReady"),
            _ => "",
        };
        Status = F("h.status", (int)Math.Round(h.Excitement * 100), hint);
    }

    // ------------------------------------------------------------------ menu

    void CreateHHud(DsMenu? menu)
    {
        if (menu is null || HHud is not null)
            return;
        var hud = new DsHud(menu, 96 + 200, 24 + 5 * 96)
        {
            State = () => (_hHudGroup switch { 1 => "挿入表示", 2 => "射精表示", 3 => "体位変更表示", 4 => "ハプニング表示", _ => "シーン表示" }) + "A",
            MinimizedState = "シーン最小化A",
            IsChecked = key => Array.IndexOf(HHudInsert, key) is var i and >= 0 ? i == HHudInsertSlot
                : Array.IndexOf(HHudEjaculation, key) is var j and >= 0 ? j == (_hInside ? 1 : 0)
                : Array.IndexOf(HHudPositions, key) is var p and >= 0 && p == _hPosition,
            ImageFor = HHudImage,
        };
        hud.Pressed += PressHHud;
        hud.Move(0, 24);
        hud.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DsHud.IsClosed))
                OnPropertyChanged(nameof(IsHudClosed));
        };
        HHud = hud;
    }

    int HHudInsertSlot => _h?.Inserted is { } p ? p[1] - '1' : 2;

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

    void ShowHHud()
    {
        if (HHud is not { } hud || _h is null)
            return;
        _hHudGroup = 0;
        hud.Show();
        ShowCameraHud();
        ShowSystemHud();
    }

    void RefreshHHud()
    {
        _hHudGroup = 0;
        HHud?.Play(instant: false);
    }

    void PressHHud(string key)
    {
        if (_h is not { } h || IsBusy || HHud is not { } hud)
            return;
        switch (key)
        {
            case "HIcon01" or "HIcon02" or "HIcon03" or "HIcon04":
                int group = key[^1] - '0';
                _hHudGroup = _hHudGroup == group ? 0 : group;
                hud.Play(instant: false);
                return;
            case "HIcon05":
                Confirm(QuestionEndH, "confirm.endH", StopH, ShowHHud);
                return;
            case "Asoko" or "Osiri":
                _hHole = key == "Asoko" ? 1 : 2;
                h.Insert(HLetter, _hHole);
                RefreshHHud();
                return;
            case "Nuku":
                h.PullOut();
                RefreshHHud();
                return;
            case "Soto" or "Naka":
                _hInside = key == "Naka";
                if (h.State == HState.Ready)
                    FinishH();
                RefreshHHud();
                return;
        }
        if (Array.IndexOf(HHudPositions, key) is var position and >= 0)
        {
            RefreshHHud();
            ChooseHPosition(position);
        }
        else if (Array.IndexOf(HHudHappenings, key) >= 0)
        {
            // 09_fera / 10_hap are not built yet.
            RefreshHHud();
            ShowStatus(T("play.notYet"), seconds: 3);
        }
    }

    char HLetter => _hPosition % 2 == 0 ? 'A' : 'B';

    /// <summary>Another position: in the same file the scene's own change clips, otherwise the other file's scene.</summary>
    void ChooseHPosition(int position)
    {
        if (_h is not { } h || position == _hPosition)
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
        if (_h is not { } h)
            return;
        if (_hInside)
            h.ComeInside();
        else
            h.ComeOutside(HSession.OutsideTargets[Random.Shared.Next(HSession.OutsideTargets.Length)]);
    }

    /// <summary>A press on the scene: strokes while inserted (held, they go on); at う！待機 it finishes.</summary>
    sealed class HTouch(PlayViewModel owner) : ISceneTouchHandler
    {
        public bool Begin(Ray ray, Vector3 up)
        {
            if (owner._h is not { } h)
                return false;
            switch (h.State)
            {
                case HState.Inserted or HState.Moving:
                    h.Continuous = true;
                    h.Stroke();
                    return true;
                case HState.Ready:
                    owner.FinishH();
                    return true;
                default:
                    return false;
            }
        }

        public void Move(Ray ray, Vector3 up)
        {
        }

        public void End()
        {
            if (owner._h is { } h)
                h.Continuous = false;
        }
    }
}
