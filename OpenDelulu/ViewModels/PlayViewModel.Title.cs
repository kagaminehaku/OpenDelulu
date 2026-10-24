using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Engine.Hud;
using OpenDelulu.Services;

namespace OpenDelulu.ViewModels;

/// <summary>
/// The original title screen (02_title, ds02: the Bonus Disc's when installed). It plays タイトル表示, then the chosen
/// entry pulses between two coconuts (…選択ループ). Main menu: しまにいく (to the island map), エクストラ (event CG = the
/// album, おもいで = memories), コンフィグ (System / Game / H pages of CONFIG), チケットカウンター, おわる (quit).
/// Choosing plays …選択 before acting; coming back from the album, memories or CONFIG plays …もどり.
/// </summary>
public sealed partial class PlayViewModel
{
    /// <summary>A title entry: its quad, its pulse clip, the clip played when chosen and the one played on return.</summary>
    sealed record TitleEntry(string Key, int Loop, int Choose, int Return = -1);

    static readonly TitleEntry[] TitleMain =
    [
        new("Sima", 2, 3, 4), new("Omoi", 5, 6), new("Con", 7, 8), new("Ticket", 9, 10), new("Owaru", 11, 12),
    ];
    static readonly TitleEntry[] TitleExtra = [new("CG", 13, 14, 15), new("OH", 16, 17, 18), new("Omodoru", 19, 20)];
    static readonly TitleEntry[] TitleConfig =
    [
        new("System", 21, 22, 23), new("Game", 24, 25, 26), new("CH", 27, 28, 29), new("Cmodoru", 30, 31),
    ];
    const int TitleIntro = 1;

    DsMenu? _title;
    bool _titleLoading, _titleLoadFailed;
    TitleEntry[] _titleMenu = TitleMain;
    TitleEntry? _titleCursor;
    Action? _afterTitleClip;
    readonly Dictionary<string, HudQuad> _titleQuads = [];
    readonly DispatcherTimer _titleTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly System.Diagnostics.Stopwatch _titleClock = new();

    public ObservableCollection<HudQuad> TitleQuads { get; } = [];

    /// <summary>The title screen covers the play area (before choosing a girl).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMap))]
    public partial bool IsTitleVisible { get; set; }

    /// <summary>Opens memories (the Scripts tab, 56_memories in part).</summary>
    public event Action? MemoriesRequested;

    /// <summary>Shows the title (loading ds02 the first time); false when the game has none.</summary>
    public async void ShowTitle()
    {
        if (_titleLoadFailed || _titleLoading || InGame)
            return;
        if (_title is null)
        {
            _titleLoading = true;
            try
            {
                _title = await Task.Run(() => DsMenu.Load(_fs, "ds02_00_00_00_00"));
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException)
            {
                _titleLoadFailed = true;
                return;
            }
            finally
            {
                _titleLoading = false;
            }
            BuildTitleQuads();
            if (InGame)
                return;
            _titleTimer.Tick += (_, _) => TickTitle();
        }
        IsLobbyVisible = false;
        IsLobbyTalking = false;
        Dialogue.Hide();
        _titleMenu = TitleMain;
        _titleCursor = TitleMain[0];
        PlayTitle(TitleIntro, () => PlayTitle(TitleMain[0].Loop, loop: true));
        IsTitleVisible = true;
        _titleClock.Restart();
        // Headless view-model tests have no UI thread; the title then just stays on its first frame.
        if (Dispatcher.UIThread.CheckAccess())
            _titleTimer.Start();
    }

    void HideTitle()
    {
        IsTitleVisible = false;
        _titleTimer.Stop();
    }

    void BuildTitleQuads()
    {
        foreach (var quad in _title!.Quads)
        {
            string key = quad.Frame.StartsWith("o01_P_", StringComparison.Ordinal) ? quad.Frame[6..] : quad.Frame;
            var item = new HudQuad(quad.Frame, key) { IsHitTestable = AllTitleEntries.Any(e => e.Key == key) };
            Bitmap? image = null;
            try
            {
                if (quad.Textures.Count > 0 && _title.Texture(quad.Textures[0]) is { } texture)
                    image = UiTextures.ToBitmap(texture);
            }
            catch (InvalidOperationException)
            {
                // No bitmap support (headless view-model tests).
            }
            item.Image = image;
            _titleQuads[key] = item;
            TitleQuads.Add(item);
        }
    }

    static IEnumerable<TitleEntry> AllTitleEntries => TitleMain.Concat(TitleExtra).Concat(TitleConfig);

    void PlayTitle(int clip, Action? then = null, bool loop = false)
    {
        if (_title is null)
            return;
        _title.Play(clip, loop: loop);
        _afterTitleClip = then;
        RefreshTitle();
    }

    void TickTitle()
    {
        if (_title is null || !IsTitleVisible)
            return;
        double seconds = Math.Min(0.1, _titleClock.Elapsed.TotalSeconds);
        _titleClock.Restart();
        if (_title.IsAnimating)
        {
            _title.Advance(seconds);
            RefreshTitle();
        }
        if (!_title.IsAnimating && _afterTitleClip is { } then)
        {
            _afterTitleClip = null;
            then();
        }
    }

    void RefreshTitle()
    {
        if (_title is null)
            return;
        foreach (var layout in _title.Layout())
        {
            string key = layout.Quad.Frame.StartsWith("o01_P_", StringComparison.Ordinal) ? layout.Quad.Frame[6..] : layout.Quad.Frame;
            if (!_titleQuads.TryGetValue(key, out var item))
                continue;
            item.X = layout.X;
            item.Y = layout.Y;
            item.W = layout.Width;
            item.H = layout.Height;
            item.IsVisible = layout.IsVisible;
        }
    }

    /// <summary>The pointer is over a title entry: it becomes the chosen one and pulses.</summary>
    public void HoverTitle(HudQuad quad)
    {
        if (_afterTitleClip is not null || _titleMenu.FirstOrDefault(e => e.Key == quad.Key) is not { } entry || entry == _titleCursor)
            return;
        _titleCursor = entry;
        PlayTitle(entry.Loop, loop: true);
    }

    public void PressTitle(HudQuad quad)
    {
        if (_afterTitleClip is not null || IsBusy || _titleMenu.FirstOrDefault(e => e.Key == quad.Key) is not { } entry)
            return;
        _titleCursor = entry;
        switch (entry.Key)
        {
            case "Sima":
                // Play Game: the hotel lobby (61_lobby), then the island.
                PlayTitle(entry.Choose, () =>
                {
                    HideTitle();
                    ShowLobby();
                });
                break;
            case "Omoi":
                PlayTitle(entry.Choose, () => EnterTitleMenu(TitleExtra));
                break;
            case "Con":
                PlayTitle(entry.Choose, () => EnterTitleMenu(TitleConfig));
                break;
            case "Omodoru" or "Cmodoru":
                PlayTitle(entry.Choose, () => EnterTitleMenu(TitleMain));
                break;
            case "Ticket":
                PlayTitle(entry.Choose, () => Confirm(QuestionTicket, "confirm.ticket", () =>
                {
                    SaveProgress();
                    ShowTicket();
                }, () => PlayTitle(entry.Loop, loop: true)));
                break;
            case "Owaru":
                PlayTitle(entry.Choose, () =>
                {
                    PlayTitle(entry.Loop, loop: true);
                    Confirm(QuestionEndGame, "confirm.endGame", () => ExitRequested?.Invoke());
                });
                break;
            case "CG":
                PlayTitle(entry.Choose, () =>
                {
                    _returnToTitle = entry;
                    OpenAlbum();
                });
                break;
            case "OH":
                PlayTitle(entry.Choose, () =>
                {
                    PlayTitle(entry.Return, () => PlayTitle(entry.Loop, loop: true));
                    if (HasOriginalMemory)
                        OpenMemory();
                    else
                        MemoriesRequested?.Invoke();
                });
                break;
            case "System" or "Game" or "CH":
                PlayTitle(entry.Choose, () =>
                {
                    PlayTitle(entry.Return, () => PlayTitle(entry.Loop, loop: true));
                    ConfigRequested?.Invoke(entry.Key switch { "System" => 0, "Game" => 1, _ => 2 });
                });
                break;
        }
    }

    TitleEntry? _returnToTitle;

    void EnterTitleMenu(TitleEntry[] menu)
    {
        _titleMenu = menu;
        _titleCursor = menu[0];
        PlayTitle(menu[0].Loop, loop: true);
    }

    /// <summary>The album closed: back to the title's Extra menu with the return clip.</summary>
    void ReturnToTitleAfterAlbum()
    {
        if (_returnToTitle is not { } entry || !IsTitleVisible)
            return;
        _returnToTitle = null;
        PlayTitle(entry.Return, () => PlayTitle(entry.Loop, loop: true));
    }
}
