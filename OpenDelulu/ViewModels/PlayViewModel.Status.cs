using System;
using System.Collections.Generic;

namespace OpenDelulu.ViewModels;

/// <summary>
/// The status panel (57_status) at the top left while on a stage: days on the island (three digits), the time of day,
/// the girl's level heart (st00_06_04_01..05) and the "Clear" star once her H picture is open. Layout from the exe's
/// sprite table (0x4012xx): panel (0, 32), digits (8 / 32 / 56, 56), time (8, 104), heart (0, 184), star (0, 232).
/// </summary>
public sealed partial class PlayViewModel
{
    public IReadOnlyList<MemorySprite> StatusSprites { get; private set; } = [];

    /// <summary>The system menu's Sinmitu button shows and hides the panel.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(ShowStatusPanel))]
    public partial bool IsStatusPanelShown { get; set; } = true;

    public bool ShowStatusPanel => InGame && IsStatusPanelShown;

    void RefreshStatus()
    {
        var sprites = new List<MemorySprite>();
        if (InGame && _girl is { } girl && _stage is { } stage)
        {
            void Add(double x, double y, double w, double h, string file) => sprites.Add(new MemorySprite(x, y, w, h, Ui.Get(file)));
            Add(0, 32, 144, 160, "st00_06_01_00_00.tga");
            string day = Math.Clamp(_progress.Day, 0, 999).ToString("000");
            for (int i = 0; i < 3; i++)
                Add(8 + 24 * i, 56, 32, 32, $"st00_06_03_{day[i] - '0' + 1:00}_00.tga");
            Add(8, 104, 80, 80, $"st00_06_02_{(int)stage.Variant.Time:00}_00.tga");
            var progress = _progress.For(girl.Girl.Id);
            Add(0, 184, 64, 56, $"st00_06_04_{Math.Clamp(progress.Intimacy, 1, 5):00}_00.tga");
            if (progress.Completed || progress.AlbumPictures.Contains(5))
                Add(0, 232, 64, 56, "st00_06_04_06_00.tga");
        }
        StatusSprites = sprites;
        OnPropertyChanged(nameof(StatusSprites));
    }
}
