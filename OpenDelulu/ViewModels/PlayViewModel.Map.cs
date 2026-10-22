using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Engine.Stages;
using OpenDelulu.Services;

namespace OpenDelulu.ViewModels;

/// <summary>A girl's face on the island map (03_chrsel): centred at the exe's position for her.</summary>
public sealed partial class MapGirl(GirlChoice choice, double x, double y, Bitmap? icon, Bitmap? heart, Bitmap? profile) : ObservableObject
{
    public GirlChoice Choice { get; } = choice;
    /// <summary>Top-left of the 80×80 icon in the 1024×768 map.</summary>
    public double X { get; } = x - 40;
    public double Y { get; } = y - 40;
    public Bitmap? Icon { get; } = icon;
    /// <summary>Intimacy heart (st00_06_04_0L).</summary>
    public Bitmap? Heart { get; } = heart;
    /// <summary>Name, sizes, likes and dislikes (st05_05_NN).</summary>
    public Bitmap? Profile { get; } = profile;
    [ObservableProperty] public partial bool IsHovered { get; set; }
}

/// <summary>A place on the island map (04_mapsel): its outlined picture, shown when hovered, and its thumbnail.</summary>
public sealed partial class MapStage(int stageId, double x, double y, Bitmap? highlight, Bitmap? thumbnail) : ObservableObject
{
    public int StageId { get; } = stageId;
    public double X { get; } = x;
    public double Y { get; } = y;
    public Bitmap? Highlight { get; } = highlight;
    public Bitmap? Thumbnail { get; } = thumbnail;
    [ObservableProperty] public partial bool IsHovered { get; set; }
}

/// <summary>A time of day button (st00_06_02_0N).</summary>
public sealed partial class MapTime(int index, double x, Bitmap? icon) : ObservableObject
{
    public int Index { get; } = index;
    public double X { get; } = x;
    public Bitmap? Icon { get; } = icon;
    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>
/// The original's island map in two steps: pick a girl (st05_01_01..05 by time of day, her face where she is), then a
/// place (st05_01_06..10, the place lights up under the pointer with its thumbnail). Positions are the exe's
/// (0x473505 girls, 0x476B12 places). Without the game's 2D textures the plain lists are shown instead.
/// </summary>
public sealed partial class PlayViewModel
{
    // Girl faces st05_02_01..08 belong to girls 1..6, 11, 12; the exe's centres for them.
    static readonly int[] MapGirlIds = [1, 2, 3, 4, 5, 6, 11, 12];
    static readonly (double X, double Y)[] MapGirlCentres =
        [(176, 496), (296, 272), (784, 224), (824, 512), (616, 392), (464, 320), (496, 456), (736, 312)];
    // Places st05_03_01..09 (stages 1..9): top-left in the map.
    static readonly (double X, double Y)[] MapStagePositions =
        [(440, 280), (640, 96), (760, 296), (80, 72), (40, 344), (544, 40), (536, 104), (536, 160), (536, 216)];

    UiTextures? _ui;
    UiTextures Ui => _ui ??= new UiTextures(_fs);

    /// <summary>True when the game's map textures are there (otherwise the lists on the left are used).</summary>
    public bool HasOriginalMap => Ui.Get("st05_01_01_00_00.bmp") is not null;

    /// <summary>The map covers the 3D view while not in the game (and the textures are there).</summary>
    public bool ShowMap => HasOriginalMap && !InGame && !IsBusy && !IsLobbyVisible && !IsTitleVisible;

    partial void OnInGameChanged(bool value) => OnPropertyChanged(nameof(ShowMap));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowMap));

    /// <summary>False: choosing a girl; true: choosing where to go with her.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MapBackground))]
    public partial bool IsChoosingStage { get; set; }

    [ObservableProperty] public partial MapGirl? HoveredGirl { get; set; }
    [ObservableProperty] public partial MapStage? HoveredStage { get; set; }

    public Bitmap? MapBackground => Ui.Get($"st05_01_{(IsChoosingStage ? 6 : 1) + Math.Clamp(SelectedTimeIndex, 0, 4):00}_00_00.bmp");

    /// <summary>The ring around the hovered girl (st05_02_09).</summary>
    public Bitmap? MapRing => Ui.Get("st05_02_09_00_00.tga");

    IReadOnlyList<MapGirl>? _mapGirls;
    IReadOnlyList<MapStage>? _mapStages;

    /// <summary>The girls on the map (rebuilt when their levels change).</summary>
    public IReadOnlyList<MapGirl> MapGirls => _mapGirls ??= BuildMapGirls();

    /// <summary>The places, with thumbnails for the chosen time of day.</summary>
    public IReadOnlyList<MapStage> MapStages => _mapStages ??= BuildMapStages();

    void RefreshMapGirls()
    {
        _mapGirls = null;
        HoveredGirl = null;
        OnPropertyChanged(nameof(MapGirls));
    }

    IReadOnlyList<MapTime>? _mapTimes;
    public IReadOnlyList<MapTime> MapTimes => _mapTimes ??= Enumerable.Range(0, 5)
        .Select(i => new MapTime(i, 16 + i * 84, Ui.Get($"st00_06_02_{i + 1:00}_00.tga")) { IsSelected = i == SelectedTimeIndex })
        .ToList();

    /// <summary>The "Cancel" button of the place step (st03_04).</summary>
    public Bitmap? MapCancel => Ui.Get("st03_04_00_00_00.tga");

    List<MapGirl> BuildMapGirls()
    {
        var list = new List<MapGirl>();
        for (int i = 0; i < MapGirlIds.Length; i++)
        {
            if (Girls.FirstOrDefault(g => g.Id == MapGirlIds[i]) is not { } choice)
                continue;
            var (x, y) = MapGirlCentres[i];
            list.Add(new MapGirl(choice, x, y, Ui.Get($"st05_02_{i + 1:00}_00_00.tga"),
                Ui.Get($"st00_06_04_{Math.Clamp(choice.Intimacy, 1, 5):00}_00.tga"), Ui.Get($"st05_05_{i + 1:00}_00_00.tga")));
        }
        return list;
    }

    List<MapStage> BuildMapStages()
    {
        var list = new List<MapStage>();
        int part = Time switch
        {
            StageTime.Evening => 2,
            StageTime.Night or StageTime.LateNight => 3,
            _ => 1,
        };
        foreach (var stage in _stages.Stages)
        {
            int n = stage.Id;
            if (n < 1 || n > MapStagePositions.Length)
                continue;
            var (x, y) = MapStagePositions[n - 1];
            // The lobby has one picture for every time of day.
            var thumbnail = Ui.Get($"st05_06_{n:00}_{part:00}_00.bmp") ?? Ui.Get($"st05_06_{n:00}_01_00.bmp");
            list.Add(new MapStage(n, x, y, Ui.Get($"st05_03_{n:00}_00_00.tga"), thumbnail));
        }
        return list;
    }

    partial void OnSelectedTimeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(MapBackground));
        _mapStages = null;
        HoveredStage = null;
        OnPropertyChanged(nameof(MapStages));
        foreach (var time in MapTimes)
            time.IsSelected = time.Index == value;
    }

    public void HoverGirl(MapGirl? girl)
    {
        if (HoveredGirl is { } old)
            old.IsHovered = false;
        HoveredGirl = girl;
        if (girl is not null)
            girl.IsHovered = true;
    }

    public void HoverStage(MapStage? stage)
    {
        if (HoveredStage is { } old)
            old.IsHovered = false;
        HoveredStage = stage;
        if (stage is not null)
            stage.IsHovered = true;
    }

    /// <summary>Clicking a girl: she comes along, now pick the place.</summary>
    public void ChooseMapGirl(MapGirl girl)
    {
        if (IsBusy || InGame)
            return;
        SelectedGirl = girl.Choice;
        HoverGirl(null);
        IsChoosingStage = true;
    }

    /// <summary>Clicking a place: go there with her.</summary>
    public void ChooseMapStage(MapStage stage)
    {
        if (IsBusy || InGame)
            return;
        int index = _stages.Stages.ToList().FindIndex(s => s.Id == stage.StageId);
        if (index < 0)
            return;
        SelectedStageIndex = index;
        HoverStage(null);
        IsChoosingStage = false;
        EnterCommand.Execute(null);
    }

    public void ChooseMapTime(MapTime time) => SelectedTimeIndex = time.Index;

    public void CancelMapStage()
    {
        HoverStage(null);
        IsChoosingStage = false;
    }
}
