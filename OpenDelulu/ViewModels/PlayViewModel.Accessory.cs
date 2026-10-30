using OpenDelulu.Engine.Game;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Engine.Characters;

namespace OpenDelulu.ViewModels;

/// <summary>A card of the Accessory page: an item of a place, or its "Remove" card (<see cref="Item"/> null).</summary>
public sealed record AccessoryCard(int Place, int Column, AccessoryItem? Item, Bitmap? Image, Bitmap? Frame, double X, double Y,
    double Width, bool IsWorn)
{
    public string Name => Item?.Name ?? "";
    public bool ShowFrame => IsWorn && Frame is not null;
    public bool ShowBorder => IsWorn && Frame is null;
}

/// <summary>
/// The Accessory page of the swimsuit screen (st02_11_01 / st02_12_01): eleven place tabs (Head … Feet = lc01..lc11) on
/// the left edge; places of one side (head, face, neck, back, hips, pareo, feet) list ten 232×120 cards, the paired
/// places (ears, wrists, fingers, ankles) a left and a right column of five 208×120 cards. The first card removes the
/// item. Layout from the exe's sprite tables (0x4854BE single, 0x4870F2 paired), relative to (488, 24).
/// </summary>
public sealed partial class PlayViewModel
{
    // Place tabs: tops on the panel's left edge (screen y = 24 + top, 64 high).
    static readonly double[] AccessoryTabTops = [72, 128, 184, 240, 296, 344, 400, 456, 512, 568, 624];

    readonly int[] _accessoryPages = new int[2];

    /// <summary>0..10 = lc01..lc11.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessoryPartTab), nameof(AccessoryPartTop), nameof(IsPairedAccessory), nameof(WardrobePanel))]
    public partial int AccessoryTab { get; set; }

    [ObservableProperty] public partial string AccessoryPageText { get; set; } = "";
    [ObservableProperty] public partial string AccessoryLeftPageText { get; set; } = "";

    public ObservableCollection<AccessoryCard> AccessoryCards { get; } = [];

    public Bitmap? AccessoryPartTab => Ui.Get($"st02_{13 + AccessoryTab:00}_03_00_00.tga");
    public double AccessoryPartTop => KigaeY + AccessoryTabTops[Math.Clamp(AccessoryTab, 0, 10)];

    /// <summary>Places of the current tab (lc00 rows): one, or left and right.</summary>
    IReadOnlyList<AccessorySlot> AccessoryPlaces =>
        _assembler.Accessories.Slots.Where(s => s.Table == AccessoryTab + 1).OrderBy(s => s.Index).Take(2).ToList();

    public bool IsPairedAccessory => AccessoryPlaces.Count > 1;

    public bool IsSinglePageAccessory => IsAccessoryPage && !IsPairedAccessory;
    public bool IsPairedPageAccessory => IsAccessoryPage && IsPairedAccessory;

    /// <summary>The items that get a card: the named rows (the unnamed ones are colour variants of the row above).</summary>
    IReadOnlyList<AccessoryItem> AccessoryChoices =>
        _assembler.Accessories.Items(AccessoryTab + 1).Where(i => i.Name.Length > 0 && VariantsOf(i).Count > 0).ToList();

    void FillAccessoryCards()
    {
        AccessoryCards.Clear();
        OnPropertyChanged(nameof(IsSinglePageAccessory));
        OnPropertyChanged(nameof(IsPairedPageAccessory));
        if (_girl is null)
            return;
        var places = AccessoryPlaces;
        var choices = new List<AccessoryItem?> { null };
        choices.AddRange(AccessoryChoices);
        int table = AccessoryTab + 1;
        bool paired = places.Count > 1;
        int perColumn = paired ? 5 : 10;
        string sheet = $"st02_{52 + table:00}";
        var removeCard = Ui.Get($"{sheet}_01_01_00.tga");
        var frame = Ui.Get($"{sheet}_03_00_00.tga");
        for (int column = 0; column < places.Count; column++)
        {
            var place = places[column];
            int pages = Math.Max(1, (choices.Count + perColumn - 1) / perColumn);
            _accessoryPages[column] = Math.Clamp(_accessoryPages[column], 0, pages - 1);
            string text = $"{_accessoryPages[column] + 1}/{pages}";
            if (column == 0 && paired)
                AccessoryLeftPageText = text;
            else
                AccessoryPageText = text;
            Look.Accessories.TryGetValue(place.Index, out int wornRow);
            var worn = _assembler.Accessories.Find(table, wornRow);
            foreach (var (item, i) in choices.Skip(_accessoryPages[column] * perColumn).Take(perColumn).Select((c, i) => (c, i)))
            {
                double x = paired ? KigaeX + 40 + 240 * column : KigaeX + 40 + 216 * (i % 2);
                double y = KigaeY + 80 + 120 * (paired ? i : i / 2);
                var iconFile = column == 1 && item?.RightIcon is { } right ? right : item?.Icon;
                var image = item is null ? removeCard : iconFile is { } icon ? Ui.Get(icon.Archive, icon.File) : null;
                bool isWorn = item is null ? worn is null : worn is not null && CardOf(worn)?.Row == item.Row;
                AccessoryCards.Add(new AccessoryCard(place.Index, column, item, image, frame, x, y, paired ? 208 : 232, isWorn));
            }
        }
    }

    /// <summary>A click on the place tabs (screen y on the panel's left edge).</summary>
    public void ChooseAccessoryTabAt(double y)
    {
        int tab = Array.FindIndex(AccessoryTabTops, top => y >= KigaeY + top + 4 && y < KigaeY + top + 56);
        if (tab < 0)
            return;
        AccessoryTab = tab;
        _accessoryPages[0] = _accessoryPages[1] = 0;
        FillAccessoryCards();
    }

    /// <param name="column">0 = left (or the only list), 1 = right.</param>
    public void TurnAccessoryPage(int column, int step)
    {
        int index = IsPairedAccessory ? column : 0;
        _accessoryPages[index] += step;
        FillAccessoryCards();
    }

    /// <summary>
    /// Puts the item on (or takes it off). Clicking the worn item again steps through its colour variants, the unnamed
    /// rows after it in the table.
    /// </summary>
    public async void ChooseAccessoryCard(AccessoryCard card)
    {
        if (IsBusy || _girl is null)
            return;
        var accessories = Look.Accessories;
        if (card.Item is not { } item)
        {
            if (!accessories.Remove(card.Place))
                return;
        }
        else
        {
            var variants = VariantsOf(item);
            int row = item.Row;
            if (card.IsWorn && accessories.TryGetValue(card.Place, out int worn) && variants.Count > 1)
            {
                int at = variants.FindIndex(v => v.Row == worn);
                row = variants[(at + 1) % variants.Count].Row;
            }
            accessories[card.Place] = row;
        }
        await RebuildGirlAsync();
        FillAccessoryCards();
    }

    /// <summary>Builds the girl again with her current swimsuit and accessories (the wardrobe stays open).</summary>
    async System.Threading.Tasks.Task RebuildGirlAsync()
    {
        if (_girl is null)
            return;
        int row = Progress.SwimsuitRow ?? _girl.Girl.Swimsuits.FirstOrDefault()?.Row ?? 0;
        await ChangeSwimsuitAsync(row, showMenu: false);
    }

    /// <summary>The item and its colour variants: the unnamed rows after it, up to the next named one.</summary>
    List<AccessoryItem> VariantsOf(AccessoryItem item)
    {
        var rows = _assembler.Accessories.Items(item.Table);
        var list = new List<AccessoryItem> { item };
        list.AddRange(rows.SkipWhile(i => i.Row <= item.Row).TakeWhile(i => i.Name.Length == 0 && i.Group == item.Group));
        // Only the rows unlocked (and the ones she wears).
        IEnumerable<int> worn = _girl is null ? [] : Progress.Look.Accessories.Values;
        return list.Where(i => Unlocks.HasAccessory(_progress, i.Table, i.Row) || worn.Contains(i.Row)).ToList();
    }

    /// <summary>The named item whose card stands for a worn row (itself, or the item a colour variant belongs to).</summary>
    AccessoryItem? CardOf(AccessoryItem worn) =>
        _assembler.Accessories.Items(worn.Table).LastOrDefault(i => i.Name.Length > 0 && i.Group == worn.Group && i.Row <= worn.Row);

    static bool SameAccessories(IReadOnlyDictionary<int, int> a, IReadOnlyDictionary<int, int> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out int v) && v == p.Value);
}
