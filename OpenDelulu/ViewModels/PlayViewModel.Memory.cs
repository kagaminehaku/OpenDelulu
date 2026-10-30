using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Engine.Audio;

namespace OpenDelulu.ViewModels;

/// <summary>A picture of the memory screens (position and size in the 1024×768 screen).</summary>
public sealed record MemorySprite(double X, double Y, double Width, double Height, Bitmap? Image);

/// <summary>
/// The original "Love Memory" screen (56_memories, st09): five columns to choose a memory (Girl, Level, Ecchi
/// 1st time / usual, Place = stages 1..9, Time), a Date field opening the list of the 21 date activities
/// (おもいでデート選択, st09_10/st09_11), Playback and Return. A chosen date is played again on its stage without
/// touching the progress; an Ecchi choice plays the H scene at the chosen level. Layout from the exe's sprite tables (0x4C3xxx,
/// st09_00_02 at 0x5C92E4, the date list at 0x5C8A8C).
/// </summary>
public sealed partial class PlayViewModel
{
    const double MemoryColumnLeft = 52, MemoryColumnStep = 184, MemoryRowTop = 152, MemoryRowHeight = 56;
    // Girl column order and cards (st09_03_NN), as the exe lists them.
    static readonly (int Girl, int Card)[] MemoryGirls = [(1, 1), (3, 3), (4, 4), (5, 5), (6, 6), (2, 2), (11, 11)];
    static readonly int[] MemoryColumnCounts = [7, 5, 2, 9, 5];

    readonly int[] _memoryChoice = [-1, -1, -1, -1, -1];
    int _memoryDate = -1;
    // The memory played again: a date activity 1..21, or 0 for the H scene (at _memoryHLevel).
    int? _memoryReplay;
    int _memoryHLevel = 2;

    [ObservableProperty] public partial bool IsMemoryOpen { get; set; }
    [ObservableProperty] public partial bool IsMemoryDateList { get; set; }

    public ObservableCollection<MemorySprite> MemorySprites { get; } = [];

    public bool HasOriginalMemory => Ui.Get("st09_01_01_00_00.tga") is not null;

    /// <summary>Opens the memory screen (title → Omoide → OH).</summary>
    public void OpenMemory()
    {
        HideTitle();
        IsMemoryDateList = false;
        IsMemoryOpen = true;
        FillMemory();
    }

    void FillMemory()
    {
        MemorySprites.Clear();
        void Add(double x, double y, double w, double h, string file) => MemorySprites.Add(new MemorySprite(x, y, w, h, Ui.Get(file)));
        if (IsMemoryDateList)
        {
            Add(0, 0, 1024, 768, "st09_10_00_00_00.bmp");
            for (int i = 0; i < 21; i++)
                Add(104 + 280 * (i / 7), 168 + 56 * (i % 7), 272, 56, $"st09_11_{i + 1:00}_00_00.tga");
            Add(880, 688, 112, 48, "st09_08_02_00_00.tga");
            return;
        }
        bool withDate = !_photoSelect && Ui.Get("st09_00_02_00_00.bmp") is not null;
        Add(0, 0, 1024, 768, withDate ? "st09_00_02_00_00.bmp" : "st09_00_01_00_00.bmp");
        for (int c = 0; c < 5; c++)
        {
            double x = MemoryColumnLeft + MemoryColumnStep * c;
            Add(x, 144, 176, 520, "st09_01_01_00_00.tga");
            Add(x, 92, 176, 64, $"st09_02_{c + 1:00}_00_00.tga");
            for (int r = 0; r < MemoryColumnCounts[c]; r++)
            {
                string card = c switch
                {
                    0 => $"st09_03_{MemoryGirls[r].Card:00}_00_00.tga",
                    1 => $"st09_04_{r + 1:00}_00_00.tga",
                    2 => $"st09_05_{r + 1:00}_00_00.tga",
                    3 => $"st09_06_{r + 1:00}_00_00.tga",
                    _ => $"st09_07_{r + 1:00}_00_00.tga",
                };
                Add(x + 12, MemoryRowTop + MemoryRowHeight * r, 152, 56, card);
            }
            if (_memoryChoice[c] >= 0)
                Add(x + 12, MemoryRowTop + MemoryRowHeight * _memoryChoice[c], 152, 56, c == 0 ? "st09_09_01_00_00.tga" : "st09_09_02_00_00.tga");
        }
        if (withDate && _memoryDate >= 0)
            Add(452, 684, 272, 56, $"st09_11_{_memoryDate + 1:00}_00_00.tga");
        Add(768, 688, 112, 48, "st09_08_01_00_00.tga");
        Add(880, 688, 112, 48, "st09_08_02_00_00.tga");
    }

    /// <summary>A click on the memory screens (screen coordinates).</summary>
    public void PressMemory(double x, double y)
    {
        // Return cancels, Playback decides, the rest picks.
        Sfx(y >= 688 && y < 736 && x >= 880 && x < 992 ? SoundEffects.Cancel
            : y >= 688 && y < 736 && x >= 768 && x < 880 ? SoundEffects.Decide : SoundEffects.Pick);
        if (IsMemoryDateList)
        {
            if (x >= 880 && x < 992 && y >= 688 && y < 736)
            {
                IsMemoryDateList = false;
                FillMemory();
                return;
            }
            int column = (int)((x - 104) / 280), row = (int)((y - 168) / 56);
            if (x < 104 || column > 2 || x - 104 - 280 * column >= 272 || y < 168 || row > 6)
                return;
            _memoryDate = column * 7 + row;
            _memoryChoice[2] = -1;
            IsMemoryDateList = false;
            FillMemory();
            return;
        }
        if (y >= 688 && y < 736 && x >= 768 && x < 880)
        {
            if (_photoSelect)
                StartPhotoShoot();
            else
                PlayMemory();
            return;
        }
        if (y >= 688 && y < 736 && x >= 880 && x < 992)
        {
            CloseMemory();
            return;
        }
        if (y >= 684 && y < 740 && x >= 306 && x < 746 && !_photoSelect && Ui.Get("st09_00_02_00_00.bmp") is not null)
        {
            IsMemoryDateList = true;
            FillMemory();
            return;
        }
        int c = (int)((x - MemoryColumnLeft) / MemoryColumnStep);
        int r = (int)Math.Floor((y - MemoryRowTop) / MemoryRowHeight);
        if (x < MemoryColumnLeft || c > 4 || r < 0 || r >= MemoryColumnCounts[c])
            return;
        _memoryChoice[c] = r;
        if (c == 2)
            _memoryDate = -1; // an ecchi memory or a date, not both
        FillMemory();
    }

    void CloseMemory()
    {
        _photoSelect = false;
        IsMemoryOpen = false;
        IsMemoryDateList = false;
        MemorySprites.Clear();
        ShowTitle();
    }

    /// <summary>
    /// Playback: a chosen date is played again (the girl, stage and time chosen, her activity at once, no greeting), or
    /// with Ecchi chosen the H scene; its end comes back here.
    /// </summary>
    async void PlayMemory()
    {
        int girlIndex = _memoryChoice[0], place = _memoryChoice[3], time = _memoryChoice[4];
        if (girlIndex < 0 || place < 0 || time < 0 || IsBusy)
            return;
        if (_memoryDate < 0 && _memoryChoice[2] < 0)
            return;
        int girlId = MemoryGirls[girlIndex].Girl;
        if (Girls.FirstOrDefault(g => g.Id == girlId) is not { } girl || place >= _stages.Stages.Count)
            return;
        SelectedGirl = girl;
        SelectedStageIndex = place;
        SelectedTimeIndex = time;
        // A date activity, or (Ecchi: 1st time / usual) the H scene at the chosen level.
        _memoryReplay = _memoryDate >= 0 ? _memoryDate + 1 : 0;
        // "1st time" plays her first H (level 2, shy); "usual" the chosen level.
        _memoryHLevel = _memoryChoice[2] == 0 ? 2 : Math.Clamp(_memoryChoice[1] + 1, 2, 5);
        IsMemoryOpen = false;
        MemorySprites.Clear();
        await EnterAsync();
    }
}
