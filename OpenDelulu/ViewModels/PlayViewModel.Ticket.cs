using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Engine.Game;

namespace OpenDelulu.ViewModels;

/// <summary>One picture of the ticket counter (position and size in the 1024×768 screen).</summary>
public sealed record TicketSprite(double X, double Y, double Width, double Height, Bitmap? Image);

/// <summary>
/// The ticket counter (01_ticket), the original's save screen: three tickets, each a game with its play time (hhh:mm),
/// days on the island, time of day and every girl's hearts ("Clear" once her H picture is open); an empty ticket shows
/// "New Game". Delete, OK and Quit at the bottom. Layout from the exe's sprite table (0x46A023).
/// </summary>
public sealed partial class PlayViewModel
{
    public const int TicketCount = 3;
    const double TicketTop = 96, TicketHeight = 192;

    readonly Stopwatch _playClock = new();

    [ObservableProperty] public partial bool IsTicketVisible { get; set; }

    /// <summary>0..2, or -1 before one is picked.</summary>
    [ObservableProperty] public partial int SelectedTicket { get; set; } = -1;

    public ObservableCollection<TicketSprite> TicketSprites { get; } = [];

    public bool HasOriginalTicket => Ui.Get("st04_01_01_00_00.bmp") is not null;
    public Bitmap? TicketBackground => Ui.Get("sb3x_1200.pp", "st04_01_02_00_00.bmp") ?? Ui.Get("st04_01_01_00_00.bmp");
    public Bitmap? TicketDelete => Ui.Get("st04_06_01_01_00.tga");
    public Bitmap? TicketOk => Ui.Get("st04_06_02_01_00.tga");
    public Bitmap? TicketQuit => Ui.Get("st04_06_03_00_00.tga");

    /// <summary>The save file of a ticket (next to <see cref="ProgressPath"/>'s folder).</summary>
    public static string TicketPath(int ticket) =>
        Path.Combine(Path.GetDirectoryName(ProgressPath) ?? ".", $"ticket{ticket + 1}.json");

    /// <summary>Opens the ticket counter (after the opening, or "Return To Ticket Counter" on the title).</summary>
    public void ShowTicket()
    {
        if (!HasOriginalTicket)
        {
            ShowTitle();
            return;
        }
        HideTitle();
        IsLobbyVisible = false;
        Dialogue.Hide();
        // Progress saved before tickets existed becomes the first ticket.
        string first = TicketPath(0);
        if (!File.Exists(first) && File.Exists(ProgressPath) && !ProgressPath.Equals(first, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Copy(ProgressPath, first); }
            catch (IOException) { }
        }
        IsTicketVisible = true;
        FillTickets();
    }

    void FillTickets()
    {
        TicketSprites.Clear();
        for (int t = 0; t < TicketCount; t++)
        {
            double top = 192 * t;
            if (LoadTicket(t) is not { } game)
            {
                TicketSprites.Add(new TicketSprite(64, TicketTop + top, 896, 192, Ui.Get("st04_05_00_00_00.tga")));
            }
            else
            {
                int minutes = (int)(game.PlaySeconds / 60);
                AddDigits(Math.Min(minutes / 60, 999), 3, [328, 352, 376], 104 + top);
                AddDigits(minutes % 60, 2, [408, 432], 104 + top);
                AddDigits(Math.Clamp(game.Day, 0, 999), 3, [816, 840, 864], 168 + top);
                TicketSprites.Add(new TicketSprite(848, 216 + top, 64, 40, Ui.Get($"st04_02_{Math.Clamp(game.TimeOfDay, 1, 5):00}_00_00.tga")));
                foreach (var (girl, g) in Girls.Take(7).Select((girl, g) => (girl, g)))
                {
                    var progress = game.Girls.GetValueOrDefault(girl.Id);
                    int level = progress?.Intimacy ?? 1;
                    double x = 224 + 80 * g;
                    if (level >= 5)
                        TicketSprites.Add(new TicketSprite(x + 8, 144 + top, 48, 48, Ui.Get("st04_04_02_00_00.tga")));
                    else
                        for (int i = 0; i < level - 1; i++)
                            TicketSprites.Add(new TicketSprite(x + 16 * i, 152 + top, 16, 24, Ui.Get("st04_04_01_00_00.tga")));
                    if (progress?.Completed == true || progress?.AlbumPictures.Contains(5) == true)
                        TicketSprites.Add(new TicketSprite(x, 248 + top, 56, 24, Ui.Get("st04_08_00_00_00.tga")));
                }
            }
            if (t == SelectedTicket)
                TicketSprites.Add(new TicketSprite(64, TicketTop + top, 896, 192, Ui.Get("st04_07_02_00_00.tga")));
        }

        void AddDigits(int value, int count, double[] xs, double y)
        {
            string text = value.ToString().PadLeft(count, '0');
            for (int i = 0; i < count; i++)
                TicketSprites.Add(new TicketSprite(xs[i], y, 24, 32, Ui.Get($"st04_03_{text[i] - '0' + 1:00}_00_00.tga")));
        }
    }

    /// <summary>A click on the tickets (screen y): picks one; a second click on it opens it.</summary>
    public void ChooseTicketAt(double y)
    {
        int ticket = (int)((y - TicketTop) / TicketHeight);
        if (ticket is < 0 or >= TicketCount)
            return;
        if (ticket == SelectedTicket)
        {
            OpenTicket();
            return;
        }
        SelectedTicket = ticket;
        FillTickets();
    }

    public const int QuestionIsThisOk = 4, QuestionUseTicket = 5;

    /// <summary>OK: asks "Use this ticket?", then plays it (a new game on an empty one) from the title.</summary>
    public void OpenTicket()
    {
        if (SelectedTicket < 0 || IsConfirming)
            return;
        Confirm(QuestionUseTicket, "confirm.useTicket", UseTicket);
    }

    void UseTicket()
    {
        SavePlayTime();
        ProgressPath = TicketPath(SelectedTicket);
        var progress = LoadTicket(SelectedTicket) ?? new GameProgress();
        // A new game starts with everything locked, like the exe; older Open Delulu games keep everything open.
        if (!File.Exists(ProgressPath) && progress.OriginalSlot == 0)
            Unlocks.Start(progress);
        progress.OriginalSlot = SelectedTicket + 1;
        LoadProgress(progress);
        IsTicketVisible = false;
        TicketSprites.Clear();
        ShowTitle();
    }

    /// <summary>Delete: asks, then throws the chosen ticket's game away.</summary>
    public void DeleteTicket()
    {
        if (SelectedTicket < 0 || LoadTicket(SelectedTicket) is null)
            return;
        int ticket = SelectedTicket;
        Confirm(QuestionIsThisOk, "confirm.deleteTicket", () =>
        {
            try { File.Delete(TicketPath(ticket)); }
            catch (IOException) { }
            // The original game's files of that ticket go too (the save folder is backed up first).
            if (WritesOriginalSaves)
            {
                try { OriginalSaves.Delete(_fs.Install, ticket + 1); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status = F("play.originalSaveError", ex.Message); }
            }
            FillTickets();
        });
    }

    /// <summary>
    /// The game of a ticket: Open Delulu's own file, or else the original game's save of the same ticket (imported), or
    /// null for an empty ticket.
    /// </summary>
    GameProgress? LoadTicket(int ticket)
    {
        string path = TicketPath(ticket);
        if (File.Exists(path))
            return GameProgress.Load(path);
        return WritesOriginalSaves ? OriginalSaves.Import(_fs.Install, ticket + 1) : null;
    }

    /// <summary>The default progress folder (AppData): tests point <see cref="ProgressPath"/> elsewhere.</summary>
    static readonly string DefaultProgressFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenDelulu");

    /// <summary>
    /// Tickets are read from and written to the original game's data/save as well; never for tests (their progress is
    /// elsewhere) unless they give their own save folder.
    /// </summary>
    static bool WritesOriginalSaves =>
        OriginalSaves.FolderOverride is not null
        || string.Equals(Path.GetDirectoryName(ProgressPath), DefaultProgressFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>Quit: asks, then closes the game.</summary>
    public void QuitFromTicket() => Confirm(QuestionEndGame, "confirm.endGame", () => ExitRequested?.Invoke());

    /// <summary>Switches to another game's progress (a ticket), keeping the screens consistent with it.</summary>
    void LoadProgress(GameProgress progress)
    {
        _progress = progress;
        SelectedTimeIndex = Math.Clamp(progress.TimeOfDay, 1, Times.Length) - 1;
        RefreshGirls(Girls.ConvertAll(g => g.Id));
        _playClock.Restart();
    }

    /// <summary>Saves the current game, with the time played since the last save.</summary>
    void SaveProgress()
    {
        SavePlayTime();
        _progress.Save(ProgressPath);
        if (WritesOriginalSaves && _progress.OriginalSlot is int slot and >= 1 and <= TicketCount)
        {
            try { OriginalSaves.Export(_fs.Install, slot, _progress); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Status = F("play.originalSaveError", ex.Message);
            }
        }
    }

    void SavePlayTime()
    {
        if (_playClock.IsRunning)
        {
            _progress.PlaySeconds += _playClock.Elapsed.TotalSeconds;
            _playClock.Restart();
        }
    }
}
