using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenDelulu.Services;

/// <summary>
/// The options of the original CONFIG screen (52_config, st01) apart from the volumes, saved with the app settings.
/// Several belong to features that are not built yet (backgrounds, obstacles, accessories, H scenes); they are kept so
/// those features can follow them later. Opacities are percent (0 = hidden, 100 = shown), colours 0..255.
/// </summary>
public sealed partial class GameConfig : ObservableObject
{
    /// <summary>The settings in use; the saved values are copied into this one object when the settings load.</summary>
    public static GameConfig Current { get; } = new();

    // System page: per-channel mute (BGM, Enviro, F/X, Voice), Date (key 1), Help (key 2), menu transparency.
    [ObservableProperty] public partial bool MuteMusic { get; set; }
    [ObservableProperty] public partial bool MuteAmbient { get; set; }
    [ObservableProperty] public partial bool MuteEffects { get; set; }
    [ObservableProperty] public partial bool MuteVoice { get; set; }
    [ObservableProperty] public partial bool ShowDate { get; set; } = true;
    [ObservableProperty] public partial bool ShowHelp { get; set; } = true;
    [ObservableProperty] public partial int MenuOpacity { get; set; } = 100;

    // Game page: background levels 1..3 (key 3) and the colour behind them, obstacles (4), crosshair (5),
    // accessories (6), pareo (7), pubic hair (H), hand transparency (8), muff glasses (M).
    [ObservableProperty] public partial bool Background1 { get; set; } = true;
    [ObservableProperty] public partial bool Background2 { get; set; } = true;
    [ObservableProperty] public partial bool Background3 { get; set; } = true;
    [ObservableProperty] public partial int BackgroundRed { get; set; }
    [ObservableProperty] public partial int BackgroundGreen { get; set; }
    [ObservableProperty] public partial int BackgroundBlue { get; set; }
    [ObservableProperty] public partial bool EraseObstacles { get; set; }
    [ObservableProperty] public partial bool Crosshair { get; set; }
    [ObservableProperty] public partial bool Accessories { get; set; } = true;
    [ObservableProperty] public partial bool Pareo { get; set; } = true;
    [ObservableProperty] public partial bool PubicHair { get; set; } = true;
    [ObservableProperty] public partial int HandOpacity { get; set; } = 100;
    [ObservableProperty] public partial int MuffGlasses { get; set; } = 100;

    // Ecchi page: clothes (key 9), the man's body (0) and his member (-).
    [ObservableProperty] public partial bool Clothes { get; set; } = true;
    [ObservableProperty] public partial int BodyOpacity { get; set; } = 100;
    [ObservableProperty] public partial int DickOpacity { get; set; } = 100;

    public GameConfig Clone()
    {
        var copy = new GameConfig();
        copy.CopyFrom(this);
        return copy;
    }

    public void CopyFrom(GameConfig other)
    {
        MuteMusic = other.MuteMusic;
        MuteAmbient = other.MuteAmbient;
        MuteEffects = other.MuteEffects;
        MuteVoice = other.MuteVoice;
        ShowDate = other.ShowDate;
        ShowHelp = other.ShowHelp;
        MenuOpacity = other.MenuOpacity;
        Background1 = other.Background1;
        Background2 = other.Background2;
        Background3 = other.Background3;
        BackgroundRed = other.BackgroundRed;
        BackgroundGreen = other.BackgroundGreen;
        BackgroundBlue = other.BackgroundBlue;
        EraseObstacles = other.EraseObstacles;
        Crosshair = other.Crosshair;
        Accessories = other.Accessories;
        Pareo = other.Pareo;
        PubicHair = other.PubicHair;
        HandOpacity = other.HandOpacity;
        MuffGlasses = other.MuffGlasses;
        Clothes = other.Clothes;
        BodyOpacity = other.BodyOpacity;
        DickOpacity = other.DickOpacity;
    }

    /// <summary>The defaults of one page (0 System, 1 Game, 2 Ecchi), for its Default button.</summary>
    public void ResetPage(int page)
    {
        var defaults = new GameConfig();
        var keep = Clone();
        CopyFrom(defaults);
        if (page != 0)
        {
            MuteMusic = keep.MuteMusic; MuteAmbient = keep.MuteAmbient; MuteEffects = keep.MuteEffects; MuteVoice = keep.MuteVoice;
            ShowDate = keep.ShowDate; ShowHelp = keep.ShowHelp; MenuOpacity = keep.MenuOpacity;
        }
        if (page != 1)
        {
            Background1 = keep.Background1; Background2 = keep.Background2; Background3 = keep.Background3;
            BackgroundRed = keep.BackgroundRed; BackgroundGreen = keep.BackgroundGreen; BackgroundBlue = keep.BackgroundBlue;
            EraseObstacles = keep.EraseObstacles; Crosshair = keep.Crosshair; Accessories = keep.Accessories; Pareo = keep.Pareo;
            PubicHair = keep.PubicHair; HandOpacity = keep.HandOpacity; MuffGlasses = keep.MuffGlasses;
        }
        if (page != 2)
        {
            Clothes = keep.Clothes; BodyOpacity = keep.BodyOpacity; DickOpacity = keep.DickOpacity;
        }
    }
}
