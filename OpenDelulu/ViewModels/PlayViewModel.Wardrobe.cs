using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Engine.Characters;

namespace OpenDelulu.ViewModels;

/// <summary>A swimsuit card (232×120, lbNN column 61) in the wardrobe grid.</summary>
public sealed record SwimsuitCard(SwimsuitInfo Swimsuit, Bitmap? Image, double X, double Y, bool IsWorn);

/// <summary>
/// The original swimsuit screen (06_kigae): the orange "Suits" panel st02_05_01 with its category tabs (1 Piece,
/// Separates, Bikini, Special = lbNN column 59), ten cards per page and page arrows, on the right of the view while she
/// stands on the left. A card puts the swimsuit on at once; OK keeps it, Cancel puts the first one back.
/// </summary>
public sealed partial class PlayViewModel
{
    const int CardsPerPage = 10;
    // Panel position in the 1024×768 screen and the card grid inside it.
    const double PanelX = 496, PanelY = 40;
    static readonly double[] CardColumns = [PanelX + 32, PanelX + 256];
    const double CardTop = PanelY + 64, CardHeight = 120;
    // Category tabs on the panel's left edge (y ranges in the screen).
    static readonly (double Top, double Bottom)[] CategoryTabs = [(100, 218), (222, 338), (342, 458), (462, 580)];

    int? _wardrobeStart;

    public bool HasOriginalWardrobe => Ui.Get("st02_05_01_00_00.tga") is not null;
    public Bitmap? WardrobeOk => Ui.Get("st02_03_02_00_00.tga");
    public Bitmap? WardrobeCancel => Ui.Get("st02_04_02_00_00.tga");

    [ObservableProperty] public partial bool IsWardrobeOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCategoryTab), nameof(SelectedCategoryTop))]
    public partial int WardrobeCategory { get; set; } = 1;

    [ObservableProperty] public partial int WardrobePage { get; set; }
    [ObservableProperty] public partial string WardrobePageText { get; set; } = "";

    public ObservableCollection<SwimsuitCard> SwimsuitCards { get; } = [];

    /// <summary>The highlighted tab picture (st02_06_03 1 Piece, 07 Separates, 08 Bikini, 10 Special).</summary>
    public Bitmap? SelectedCategoryTab => Ui.Get($"st02_{WardrobeCategory switch { 1 => 6, 2 => 7, 3 => 8, _ => 10 }:00}_03_00_00.tga");
    public double SelectedCategoryTop => CategoryTabs[Math.Clamp(WardrobeCategory, 1, 4) - 1].Top - 9;

    IReadOnlyList<SwimsuitInfo> CategorySwimsuits =>
        _girl?.Girl.Swimsuits.Where(s => s.Category == WardrobeCategory || (WardrobeCategory == 4 && s.Category is < 1 or > 4)).ToList() ?? [];

    void OpenWardrobe()
    {
        if (_girl is null)
            return;
        _wardrobeStart = Progress.SwimsuitRow;
        _lookStart = Progress.Look.Clone();
        ColorPart = 0;
        ShowWardrobePage(0);
        var worn = _girl.Girl.Swimsuits.FirstOrDefault(s => s.Row == Progress.SwimsuitRow) ?? _girl.Girl.Swimsuits.FirstOrDefault();
        WardrobeCategory = worn?.Category is >= 1 and <= 4 ? worn.Category : 1;
        var list = CategorySwimsuits.ToList();
        WardrobePage = worn is null ? 0 : Math.Max(0, list.FindIndex(s => s.Row == worn.Row)) / CardsPerPage;
        Dialogue.Hide();
        IsWardrobeOpen = true;
        FillCards();
    }

    void FillCards()
    {
        SwimsuitCards.Clear();
        var list = CategorySwimsuits;
        int pages = Math.Max(1, (list.Count + CardsPerPage - 1) / CardsPerPage);
        WardrobePage = Math.Clamp(WardrobePage, 0, pages - 1);
        WardrobePageText = $"{WardrobePage + 1}/{pages}";
        int worn = Progress.SwimsuitRow ?? _girl?.Girl.Swimsuits.FirstOrDefault()?.Row ?? -1;
        foreach (var (swimsuit, i) in list.Skip(WardrobePage * CardsPerPage).Take(CardsPerPage).Select((s, i) => (s, i)))
        {
            var image = swimsuit.Icon is { } icon ? Ui.Get(icon.Archive, icon.File) : null;
            SwimsuitCards.Add(new SwimsuitCard(swimsuit, image, CardColumns[i % 2], CardTop + (i / 2) * CardHeight, swimsuit.Row == worn));
        }
    }

    /// <summary>Clicks on the panel's category tabs (by height on its left edge).</summary>
    public void ChooseWardrobeCategoryAt(double y)
    {
        int index = Array.FindIndex(CategoryTabs, t => y >= t.Top && y <= t.Bottom);
        if (index < 0)
            return;
        WardrobeCategory = index + 1;
        WardrobePage = 0;
        FillCards();
    }

    public void TurnWardrobePage(int step)
    {
        WardrobePage += step;
        FillCards();
    }

    public async void ChooseSwimsuitCard(SwimsuitCard card)
    {
        if (IsBusy)
            return;
        await ChangeSwimsuitAsync(card.Swimsuit.Row, showMenu: false);
        FillCards();
    }

    public void CloseWardrobe(bool keep)
    {
        IsWardrobeOpen = false;
        SwimsuitCards.Clear();
        if (keep)
            _progress.Save(ProgressPath);
        else if (_lookStart is not null)
        {
            Progress.Look = _lookStart;
            ApplyLook();
        }
        _lookStart = null;
        if (!keep && _wardrobeStart != Progress.SwimsuitRow && _girl is not null)
        {
            int row = _wardrobeStart ?? _girl.Girl.Swimsuits.FirstOrDefault()?.Row ?? 0;
            _ = ChangeSwimsuitAsync(row);
            return;
        }
        ShowMenu();
    }
}
