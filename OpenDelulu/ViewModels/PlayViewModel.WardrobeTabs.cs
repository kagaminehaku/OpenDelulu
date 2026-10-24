using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Engine.Game;

namespace OpenDelulu.ViewModels;

/// <summary>A row of the Favorite page: its digits (yyyy/mm/dd hh:mm) and whether it is the chosen one.</summary>
public sealed record FavoriteRow(int Index, double Y, IReadOnlyList<(double X, Bitmap? Digit)> Digits, bool IsSelected)
{
    public IEnumerable<FavoriteDigit> DigitImages => Digits.Select(d => new FavoriteDigit(d.X, Y + 8, d.Digit));
}

public sealed record FavoriteDigit(double X, double Y, Bitmap? Image);

/// <summary>A colour preset of the Hair page (the ten painted swatches).</summary>
public sealed record HairPreset(int Index, double X, double Y, int R, int G, int B);

/// <summary>
/// The other pages of the original swimsuit screen (06_kigae): Accessory (st02_11_01 with body-part tabs; accessories
/// come with their own step), Color (st02_24_01: the nails of the hands and feet and the eyes, each with colour, gloss and
/// transparency), Hair (st02_27_01_00_01: colour and ten swatches) and Favorite (st02_28_01: twelve dated sets with
/// Delete / Save / Load). Layout from the exe's sprite tables (0x488F92, 0x489E82, 0x48BA5C), relative to (488, 24).
/// </summary>
public sealed partial class PlayViewModel
{
    const double KigaeX = 488, KigaeY = 24;
    // Page tabs painted across the top of every panel (screen x ranges, y 40..76).
    static readonly (double Left, double Right)[] WardrobePageTabs = [(514, 600), (602, 722), (724, 804), (806, 884), (886, 1002)];
    static readonly string[] ColorPartTabs = ["st02_25_03_00_00.tga", "st02_26_03_00_00.tga", "st02_27_03_00_00.tga"];
    static readonly (int R, int G, int B)[] HairSwatches =
    [
        (230, 40, 40), (250, 230, 50), (60, 200, 60), (60, 220, 240), (50, 80, 230),
        (240, 90, 200), (150, 150, 165), (150, 100, 55), (255, 255, 255), (35, 35, 35),
    ];

    GirlLook? _lookStart;
    List<ConfigSlider>? _colorSliders, _hairSliders;

    /// <summary>0 Suits, 1 Accessory, 2 Color, 3 Hair, 4 Favorite.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WardrobePanel), nameof(IsSuitsPage), nameof(IsColorPage), nameof(IsHairPage),
        nameof(IsFavoritePage), nameof(IsAccessoryPage))]
    public partial int WardrobePageIndex { get; set; }

    /// <summary>0 hand nails, 1 foot nails, 2 eyes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorPartTab), nameof(ColorPartTop))]
    public partial int ColorPart { get; set; }

    [ObservableProperty] public partial int FavoriteSelected { get; set; } = -1;

    public bool IsSuitsPage => WardrobePageIndex == 0;
    public bool IsAccessoryPage => WardrobePageIndex == 1;
    public bool IsColorPage => WardrobePageIndex == 2;
    public bool IsHairPage => WardrobePageIndex == 3;
    public bool IsFavoritePage => WardrobePageIndex == 4;

    public Bitmap? WardrobePanel => Ui.Get(WardrobePageIndex switch
    {
        1 => IsPairedAccessory ? "st02_12_01_00_00.tga" : "st02_11_01_00_00.tga",
        2 => "st02_24_01_00_00.tga",
        3 => "st02_27_01_00_01.tga",
        4 => "st02_28_01_00_00.tga",
        _ => "st02_05_01_00_00.tga",
    });

    public Bitmap? ColorPartTab => Ui.Get(ColorPartTabs[Math.Clamp(ColorPart, 0, 2)]);
    public double ColorPartTop => KigaeY + 64 + 120 * ColorPart;

    public ObservableCollection<ConfigSlider> WardrobeSliders { get; } = [];
    public ObservableCollection<FavoriteRow> FavoriteRows { get; } = [];
    public IReadOnlyList<HairPreset> HairPresets { get; } = HairSwatches
        .Select((c, i) => new HairPreset(i, KigaeX + 8 + 70 + 80 * (i % 5) - 6, KigaeY + 16 + 548 + 58 * (i / 5) - 4, c.R, c.G, c.B)).ToList();
    public Bitmap? HairPresetFrame => Ui.Get("st02_90_01_00_00.tga");
    public Bitmap? FavoriteHighlight => Ui.Get("st02_46_02_00_00.tga");
    public Bitmap? FavoriteDelete => Ui.Get("st02_29_01_00_00.tga");
    public Bitmap? FavoriteSave => Ui.Get("st02_30_01_00_00.tga");
    public Bitmap? FavoriteLoad => Ui.Get("st02_31_01_00_00.tga");

    GirlLook Look => Progress.Look;

    /// <summary>A click on the page tabs at the top of the panel.</summary>
    public bool ChooseWardrobePageAt(double x, double y)
    {
        if (y < 40 || y > 76)
            return false;
        int page = Array.FindIndex(WardrobePageTabs, t => x >= t.Left && x <= t.Right);
        if (page < 0)
            return false;
        ShowWardrobePage(page);
        return true;
    }

    void ShowWardrobePage(int page)
    {
        WardrobePageIndex = page;
        WardrobeSliders.Clear();
        foreach (var slider in page switch { 2 => ColorSliders(), 3 => HairSliders(), _ => [] })
        {
            slider.Refresh();
            WardrobeSliders.Add(slider);
        }
        if (page == 4)
            FillFavorites();
        if (page == 1)
            FillAccessoryCards();
        else
            AccessoryCards.Clear();
    }

    public void ChooseColorPartAt(double y)
    {
        int part = (int)((y - (KigaeY + 64)) / 120);
        if (part is < 0 or > 2)
            return;
        ColorPart = part;
        foreach (var slider in WardrobeSliders)
            slider.Refresh();
    }

    (int[] Color, int[] Gloss, Func<int> GetOpacity, Action<int> SetOpacity) ColorTarget() => ColorPart switch
    {
        1 => (Look.Feet, Look.FeetGloss, () => Look.FeetOpacity, v => Look.FeetOpacity = v),
        2 => (Look.Eyes, Look.EyesGloss, () => Look.EyesOpacity, v => Look.EyesOpacity = v),
        _ => (Look.Hand, Look.HandGloss, () => Look.HandOpacity, v => Look.HandOpacity = v),
    };

    Bitmap?[] WardrobeDigits => Enumerable.Range(36, 10).Select(i => Ui.Get($"st02_{i:00}_01_00_00.tga")).ToArray();

    ConfigSlider WardrobeSlider(double y, int max, Func<int> get, Action<int> set) =>
        new(KigaeX + 92, KigaeY + y + 4, 324, KigaeX + 104, KigaeX + 404, KigaeY + y, max, KigaeY + y,
            Ui.Get("st02_35_01_00_00.tga"), WardrobeDigits, get, v => { set(v); ApplyLook(); }, digitsX: KigaeX + 432);

    List<ConfigSlider> ColorSliders() => _colorSliders ??=
    [
        .. new[] { 136, 200, 264 }.Select((y, c) => WardrobeSlider(y, 255, () => ColorTarget().Color[c], v => ColorTarget().Color[c] = v)),
        .. new[] { 384, 448, 512 }.Select((y, c) => WardrobeSlider(y, 255, () => ColorTarget().Gloss[c], v => ColorTarget().Gloss[c] = v)),
        WardrobeSlider(632, 100, () => ColorTarget().GetOpacity(), v => ColorTarget().SetOpacity(v)),
    ];

    List<ConfigSlider> HairSliders() => _hairSliders ??=
        [.. new[] { 384, 448, 512 }.Select((y, c) => WardrobeSlider(y, 255, () => Look.Hair[c], v => Look.Hair[c] = v))];

    public void ChooseHairPreset(HairPreset preset)
    {
        Look.Hair = [preset.R, preset.G, preset.B];
        foreach (var slider in WardrobeSliders)
            slider.Refresh();
        ApplyLook();
    }

    /// <summary>Nail, eye and hair colours on her model (texture × colour; gloss and transparency are only kept).</summary>
    void ApplyLook()
    {
        if (Scene is not { } scene || _girl is null)
            return;
        var look = Look;
        static Vector3? Tint(int[] rgb) => rgb.All(c => c >= 255) ? null : new Vector3(rgb[0], rgb[1], rgb[2]) / 255f;
        scene.SetSubjectTint(m => m.MaterialName == "m01_TumeH", Tint(look.Hand));
        scene.SetSubjectTint(m => m.MaterialName == "m01_TumeF", Tint(look.Feet));
        scene.SetSubjectTint(m => m.MaterialName == "m01_Eye", Tint(look.Eyes));
        scene.SetSubjectTint(m => m.PartName == "hair", Tint(look.Hair));
    }

    // ------------------------------------------------------------------ favourites

    void FillFavorites()
    {
        FavoriteRows.Clear();
        var favorites = Progress.Favorites;
        int[] columns = [228, 244, 260, 276, 304, 320, 344, 360, 392, 408, 440, 456];
        var digits = Enumerable.Range(80, 10).Select(i => Ui.Get($"st02_{i:00}_01_00_00.tga")).ToArray();
        for (int i = 0; i < 12; i++)
        {
            var set = i < favorites.Count ? favorites[i] : null;
            var shown = new List<(double, Bitmap?)>();
            if (set is not null)
            {
                string text = set.Saved.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);
                for (int d = 0; d < text.Length && d < columns.Length; d++)
                    shown.Add((KigaeX + columns[d], digits[text[d] - '0']));
            }
            FavoriteRows.Add(new FavoriteRow(i, KigaeY + 84 + 48 * i, shown, i == FavoriteSelected));
        }
    }

    public void ChooseFavoriteAt(double y)
    {
        int row = (int)((y - (KigaeY + 84)) / 48);
        if (row is < 0 or > 11)
            return;
        FavoriteSelected = row;
        FillFavorites();
    }

    public void SaveFavorite()
    {
        if (FavoriteSelected < 0 || _girl is null)
            return;
        var favorites = Progress.Favorites;
        while (favorites.Count < 12)
            favorites.Add(null);
        favorites[FavoriteSelected] = new FavoriteSet { Saved = DateTime.Now, SwimsuitRow = Progress.SwimsuitRow, Look = Look.Clone() };
        _progress.Save(ProgressPath);
        FillFavorites();
    }

    public async void LoadFavorite()
    {
        if (FavoriteSelected < 0 || FavoriteSelected >= Progress.Favorites.Count || Progress.Favorites[FavoriteSelected] is not { } set)
            return;
        bool accessoriesChanged = !SameAccessories(Progress.Look.Accessories, set.Look.Accessories);
        Progress.Look = set.Look.Clone();
        if (set.SwimsuitRow is { } row && row != Progress.SwimsuitRow)
            await ChangeSwimsuitAsync(row, showMenu: false);
        else if (accessoriesChanged)
            await RebuildGirlAsync();
        ApplyLook();
        FillCards();
    }

    public void DeleteFavorite()
    {
        if (FavoriteSelected < 0 || FavoriteSelected >= Progress.Favorites.Count)
            return;
        Progress.Favorites[FavoriteSelected] = null;
        _progress.Save(ProgressPath);
        FillFavorites();
    }
}
