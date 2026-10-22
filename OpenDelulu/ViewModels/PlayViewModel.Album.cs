using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenDelulu.Engine.Game;

namespace OpenDelulu.ViewModels;

/// <summary>A picture slot of the album page; locked ones are covered by st03_03_00 ("It doesn't pass").</summary>
public sealed record AlbumSlot(int Index, double X, double Y, bool IsLocked, Bitmap? Cover);

/// <summary>
/// The original CG album (54_eventcg, st03): eight tabs (seven girls and "Ecchi"), a 992×648 page per tab with the
/// thumbnails painted in, a 3×3 grid of 192×144 slots and Cancel. Clicking an unlocked slot shows the picture full
/// screen (55_eventcgdraw); a click goes back. Layout and tab order are the exe's (sprite tables at 0x4C91D2,
/// hit areas at 0x4CA966, flags read at 0x4CBD90). Pictures unlocked in the original game's saves count too.
/// </summary>
public sealed partial class PlayViewModel
{
    /// <summary>Girl of each tab; the eighth tab (Ecchi) holds one H picture per girl in <see cref="AlbumHGirls"/>.</summary>
    static readonly int[] AlbumGirls = [1, 3, 4, 5, 6, 2, 11];
    static readonly int[] AlbumHGirls = OriginalSaves.HPictureGirls;
    const int AlbumTabs = 8, PicturesPerGirl = 4;
    const double AlbumTabLeft = 16, AlbumTabTop = 48, AlbumTabWidth = 96, AlbumTabHeight = 48;

    public bool HasOriginalAlbum => Ui.Get("st03_02_01_02_00.tga") is not null;
    public Bitmap? AlbumBackground => Ui.Get("st03_01_00_00_00.bmp");
    public Bitmap? AlbumCancel => Ui.Get("st03_04_00_00_00.tga");
    public Bitmap? AlbumLockedPicture => Ui.Get("st03_03_00_00_00.bmp");

    [ObservableProperty] public partial bool IsAlbumOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlbumPage))]
    public partial int AlbumTab { get; set; }

    /// <summary>Tab under the pointer (its st03_02_GG_00 picture is drawn over the page), or -1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlbumHoverTabImage), nameof(AlbumHoverTabLeft))]
    public partial int AlbumHoverTab { get; set; } = -1;

    /// <summary>The picture shown full screen, or null while the page is shown.</summary>
    [ObservableProperty] public partial Bitmap? AlbumPicture { get; set; }

    public ObservableCollection<AlbumSlot> AlbumSlots { get; } = [];

    /// <summary>Pictures unlocked in the original saves, read when the album opens.</summary>
    Dictionary<int, HashSet<int>> _originalAlbum = [];

    public Bitmap? AlbumPage => Ui.Get($"st03_02_{AlbumTab + 1:00}_02_00.tga");
    public Bitmap? AlbumHoverTabImage => AlbumHoverTab < 0 ? null : Ui.Get($"st03_02_{AlbumHoverTab + 1:00}_00_00.tga");
    public double AlbumHoverTabLeft => AlbumTabLeft + Math.Max(0, AlbumHoverTab) * AlbumTabWidth;

    [RelayCommand]
    void OpenAlbum()
    {
        if (InGame || IsBusy)
            return;
        AlbumTab = 0;
        AlbumPicture = null;
        _originalAlbum = OriginalSaves.AlbumPictures(_fs.Install);
        FillAlbum();
        IsAlbumOpen = true;
    }

    public void CloseAlbum()
    {
        ReturnToTitleAfterAlbum();
        IsAlbumOpen = false;
        AlbumPicture = null;
        AlbumHoverTab = -1;
        AlbumSlots.Clear();
    }

    /// <summary>The tab at a point of the 1024×768 album screen, or -1.</summary>
    public static int AlbumTabAt(double x, double y)
    {
        if (y < AlbumTabTop || y >= AlbumTabTop + AlbumTabHeight || x < AlbumTabLeft)
            return -1;
        int tab = (int)((x - AlbumTabLeft) / AlbumTabWidth);
        return tab < AlbumTabs ? tab : -1;
    }

    public void ChooseAlbumTab(int tab)
    {
        if (tab < 0 || tab >= AlbumTabs)
            return;
        AlbumTab = tab;
        FillAlbum();
    }

    public void ChooseAlbumSlot(AlbumSlot slot)
    {
        if (!slot.IsLocked)
            AlbumPicture = Ui.Get(AlbumPictureFile(AlbumTab, slot.Index));
    }

    public void CloseAlbumPicture() => AlbumPicture = null;

    /// <summary>st03_03_GG_NN: pictures 1..4 of the tab's girl, or the H picture (05) of the Ecchi tab's slot.</summary>
    static string AlbumPictureFile(int tab, int slot) => tab < AlbumGirls.Length
        ? $"st03_03_{AlbumGirls[tab]:00}_{slot + 1:00}_00.bmp"
        : $"st03_03_{AlbumHGirls[slot]:00}_05_00.bmp";

    bool IsAlbumPictureUnlocked(int tab, int slot)
    {
        var (girl, picture) = tab < AlbumGirls.Length ? (AlbumGirls[tab], slot + 1) : (AlbumHGirls[slot], 5);
        return _progress.Girls.TryGetValue(girl, out var progress) && progress.AlbumPictures.Contains(picture)
            || _originalAlbum.TryGetValue(girl, out var original) && original.Contains(picture);
    }

    void FillAlbum()
    {
        AlbumSlots.Clear();
        int count = AlbumTab < AlbumGirls.Length ? PicturesPerGirl : AlbumHGirls.Length;
        for (int i = 0; i < count; i++)
        {
            bool locked = !IsAlbumPictureUnlocked(AlbumTab, i);
            AlbumSlots.Add(new AlbumSlot(i, 328 + (i % 3) * 224, 120 + (i / 3) * 192, locked, locked ? AlbumLockedPicture : null));
        }
    }
}
