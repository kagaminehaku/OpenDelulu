using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Engine;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Services;

namespace OpenDelulu.ViewModels;

/// <summary>
/// A picture on the CONFIG page that reacts to the pointer: a sprite rectangle (X, Y, W, H) and a hit rectangle given
/// relative to it. Toggles show their off/on picture, buttons only their hover picture (the normal look is painted on
/// the page).
/// </summary>
public sealed partial class ConfigButton(double x, double y, double width, double height,
    Bitmap? off, Bitmap? offHover, Bitmap? on, Bitmap? onHover, Func<bool>? state, Action click,
    (double X, double Y, double W, double H)? hit = null) : ObservableObject
{
    public double X { get; } = x;
    public double Y { get; } = y;
    public double W { get; } = width;
    public double H { get; } = height;
    public double HitX { get; } = hit?.X - x ?? 0;
    public double HitY { get; } = hit?.Y - y ?? 0;
    public double HitW { get; } = hit?.W ?? width;
    public double HitH { get; } = hit?.H ?? height;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Image))]
    public partial bool IsHover { get; set; }

    public bool IsOn => state?.Invoke() ?? false;
    public Bitmap? Image => IsOn ? (IsHover ? onHover ?? on : on) : (IsHover ? offHover ?? off : off);

    public void Click() => click();
    public void Refresh() => OnPropertyChanged(nameof(Image));
}

/// <summary>
/// A slider of the CONFIG page: a track (hit area), a knob whose centre runs from <see cref="KnobMin"/> to
/// <see cref="KnobMax"/>, three red digits and an optional sample square.
/// </summary>
public sealed class ConfigSlider : ObservableObject
{
    readonly Func<int> _get;
    readonly Action<int> _set;
    readonly Func<int, IBrush>? _sample;
    readonly Bitmap?[] _digits;

    public ConfigSlider(double trackX, double trackY, double trackWidth, double knobMin, double knobMax, double knobTop,
        int max, double digitsY, Bitmap? knob, Bitmap?[] digits, Func<int> get, Action<int> set,
        double? sampleY = null, Func<int, IBrush>? sample = null)
    {
        TrackX = trackX; TrackY = trackY; TrackWidth = trackWidth;
        KnobMin = knobMin; KnobMax = knobMax; KnobTop = knobTop; Max = max; DigitsY = digitsY;
        Knob = knob; _digits = digits; _get = get; _set = set; SampleY = sampleY ?? 0; HasSample = sampleY is not null;
        _sample = sample;
    }

    public double TrackX { get; }
    public double TrackY { get; }
    public double TrackWidth { get; }
    public double KnobMin { get; }
    public double KnobMax { get; }
    public double KnobTop { get; }
    public int Max { get; }
    public double DigitsY { get; }
    public double SampleY { get; }
    public bool HasSample { get; }
    public Bitmap? Knob { get; }

    public int Value => Math.Clamp(_get(), 0, Max);
    public double KnobLeft => KnobMin + Value * (KnobMax - KnobMin) / Max - 12;

    /// <summary>The value right-aligned in three digits (st01_17..26), blanks for leading zeros.</summary>
    public Bitmap? Digit0 => Value >= 100 ? _digits[Value / 100 % 10] : null;
    public Bitmap? Digit1 => Value >= 10 ? _digits[Value / 10 % 10] : null;
    public Bitmap? Digit2 => _digits[Value % 10];

    public IBrush? Sample => _sample?.Invoke(Value);

    /// <summary>Sets the value from a pointer x on the 1024×768 screen.</summary>
    public void SetFromX(double x)
    {
        double t = (x - KnobMin) / (KnobMax - KnobMin);
        _set((int)Math.Round(Math.Clamp(t, 0, 1) * Max));
        Refresh();
    }

    public void Refresh()
    {
        foreach (var name in new[] { nameof(Value), nameof(KnobLeft), nameof(Digit0), nameof(Digit1), nameof(Digit2), nameof(Sample) })
            OnPropertyChanged(name);
    }
}

/// <summary>
/// The original CONFIG screen (52_config): three 800×600 pages (System, Game, Ecchi) at (112,80) of the 1024×768
/// screen. Sprite and hit tables are the exe's (0x4BE987: on/off 32×40 and 96×32, checkboxes 32×32, knobs 24×24,
/// digits 16×24, samples 32×32; knob ranges 356..740 for 0..100). Changes show at once; Save keeps them, Cancel
/// puts the old values back, Default resets the page.
/// </summary>
public sealed partial class ConfigViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly GameFileSystem _fs;
    readonly UiTextures _ui;
    readonly SoundHandle?[] _tests = new SoundHandle?[4];
    GameConfig? _before;
    int[] _volumesBefore = [];

    public ConfigViewModel(MainViewModel main, GameFileSystem fs)
    {
        _main = main;
        _fs = fs;
        _ui = new UiTextures(fs);
        Build();
    }

    GameConfig Config => GameConfig.Current;

    public bool HasOriginal => _ui.Get("st01_01_01_00_00.tga") is not null;

    [ObservableProperty] public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageImage))]
    public partial int Page { get; set; }

    public Bitmap? PageImage => _ui.Get($"st01_{Page + 1:00}_01_00_00.tga");

    public ObservableCollection<ConfigButton> Buttons { get; } = [];
    public ObservableCollection<ConfigSlider> Sliders { get; } = [];

    readonly List<ConfigButton>[] _pageButtons = [[], [], []];
    readonly List<ConfigSlider>[] _pageSliders = [[], [], []];

    Bitmap? Tex(string name) => _ui.Get($"st01_{name}_00_00.tga");

    void Build()
    {
        var digits = Enumerable.Range(17, 10).Select(i => Tex($"{i:00}_01")).ToArray();
        var knob = Tex("15_01");
        IBrush Alpha(int percent) => new SolidColorBrush(Color.FromArgb((byte)(percent * 255 / 100), 32, 96, 192));

        ConfigSlider Slider(int page, double trackX, double trackWidth, double knobMin, double knobMax, double y, int max,
            Func<int> get, Action<int> set, bool sample = false, Func<int, IBrush>? sampleBrush = null)
        {
            var slider = new ConfigSlider(trackX, y + 4, trackWidth, knobMin, knobMax, y, max, y, knob, digits, get, set,
                sample ? y : null, sample ? sampleBrush ?? Alpha : null);
            _pageSliders[page].Add(slider);
            return slider;
        }

        void Pill(int page, double x, double y, Func<bool> get, Action<bool> set) =>
            _pageButtons[page].Add(new ConfigButton(x, y, 96, 32, Tex("08_01"), Tex("08_02"), Tex("09_01"), Tex("09_02"),
                get, () => { set(!get()); RefreshButtons(); }, (x + 5, y + 3, 86, 26)));

        // Shared: tabs (hover pictures over the painted ones) and Default / Save / Cancel.
        for (int page = 0; page < 3; page++)
        {
            (double X, double HitX)[] tabs = [(120, 124), (240, 268), (368, 392)];
            for (int t = 0; t < 3; t++)
            {
                if (t == page)
                    continue;
                int target = t;
                _pageButtons[page].Add(new ConfigButton(tabs[t].X, 88, 160, 56, null, Tex($"{t + 1:00}_02"), null, null, null,
                    () => ShowPage(target), (tabs[t].HitX, 92, 124, 48)));
            }
            _pageButtons[page].Add(new ConfigButton(152, 616, 112, 48, null, Tex("12_02"), null, null, null, Default, (159, 627, 98, 36)));
            _pageButtons[page].Add(new ConfigButton(624, 616, 112, 48, null, Tex("13_02"), null, null, null, Save, (631, 627, 98, 36)));
            _pageButtons[page].Add(new ConfigButton(752, 616, 112, 48, null, Tex("14_02"), null, null, null, Cancel, (759, 627, 98, 36)));
        }

        // System: BGM, Enviro, F/X, Voice rows (mute, volume, test), Date and Help, menu transparency.
        (Func<bool> Get, Action<bool> Set)[] mutes =
        [
            (() => Config.MuteMusic, v => Config.MuteMusic = v), (() => Config.MuteAmbient, v => Config.MuteAmbient = v),
            (() => Config.MuteEffects, v => Config.MuteEffects = v), (() => Config.MuteVoice, v => Config.MuteVoice = v),
        ];
        (Func<int> Get, Action<int> Set)[] volumes =
        [
            (() => _main.MusicVolume, v => _main.MusicVolume = v), (() => _main.AmbientVolume, v => _main.AmbientVolume = v),
            (() => _main.EffectsVolume, v => _main.EffectsVolume = v), (() => _main.VoiceVolume, v => _main.VoiceVolume = v),
        ];
        for (int row = 0; row < 4; row++)
        {
            int channel = row;
            double y = 168 + 64 * row;
            _pageButtons[0].Add(new ConfigButton(296, y, 32, 40, Tex("04_01"), Tex("04_02"), Tex("05_01"), Tex("05_02"),
                mutes[row].Get, () => { mutes[channel].Set(!mutes[channel].Get()); RefreshButtons(); }, (297, y + 1, 30, 33)));
            Slider(0, 352, 392, 356, 740, y + 8, 100, volumes[row].Get, volumes[row].Set);
            _pageButtons[0].Add(new ConfigButton(824, y, 32, 40, Tex("06_01"), Tex("06_02"), Tex("07_01"), Tex("07_02"),
                () => IsTesting(channel), () => ToggleTest(channel), (825, y + 1, 30, 33)));
        }
        Pill(0, 320, 464, () => Config.ShowDate, v => Config.ShowDate = v);
        Pill(0, 688, 464, () => Config.ShowHelp, v => Config.ShowHelp = v);
        Slider(0, 392, 352, 396, 740, 544, 100, () => Config.MenuOpacity, v => Config.MenuOpacity = v, sample: true);

        // Game: background levels and colour, obstacles, crosshair, accessories, pareo, pubic hair, hands, glasses.
        (Func<bool> Get, Action<bool> Set)[] levels =
        [
            (() => Config.Background1, v => Config.Background1 = v), (() => Config.Background2, v => Config.Background2 = v),
            (() => Config.Background3, v => Config.Background3 = v),
        ];
        for (int level = 0; level < 3; level++)
        {
            int index = level;
            double y = 176 + 40 * level;
            _pageButtons[1].Add(new ConfigButton(416, y, 32, 32, Tex("10_01"), Tex("10_02"), Tex("11_01"), Tex("11_02"),
                levels[level].Get, () => { levels[index].Set(!levels[index].Get()); RefreshButtons(); }, (420, y + 3, 24, 24)));
        }
        IBrush Colour(int _) => new SolidColorBrush(Color.FromRgb((byte)Config.BackgroundRed, (byte)Config.BackgroundGreen, (byte)Config.BackgroundBlue));
        var red = Slider(1, 560, 184, 564, 740, 216, 255, () => Config.BackgroundRed, v => Config.BackgroundRed = v);
        var green = Slider(1, 560, 184, 564, 740, 240, 255, () => Config.BackgroundGreen, v => Config.BackgroundGreen = v,
            sample: true, sampleBrush: Colour);
        var blue = Slider(1, 560, 184, 564, 740, 264, 255, () => Config.BackgroundBlue, v => Config.BackgroundBlue = v);
        // The colour sample (824,240) belongs to the middle row; any of the three changes it.
        Config.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GameConfig.BackgroundRed) or nameof(GameConfig.BackgroundBlue))
                green.Refresh();
        };
        _ = red; _ = blue;
        Pill(1, 384, 304, () => Config.EraseObstacles, v => Config.EraseObstacles = v);
        Pill(1, 752, 304, () => Config.Crosshair, v => Config.Crosshair = v);
        Pill(1, 312, 392, () => Config.Accessories, v => Config.Accessories = v);
        Pill(1, 536, 392, () => Config.Pareo, v => Config.Pareo = v);
        Pill(1, 760, 392, () => Config.PubicHair, v => Config.PubicHair = v);
        Slider(1, 356, 384, 356, 740, 472, 100, () => Config.HandOpacity, v => Config.HandOpacity = v, sample: true);
        Slider(1, 356, 384, 356, 740, 544, 100, () => Config.MuffGlasses, v => Config.MuffGlasses = v, sample: true);

        // Ecchi: clothes, body and member transparency.
        Pill(2, 320, 168, () => Config.Clothes, v => Config.Clothes = v);
        Slider(2, 304, 440, 308, 740, 256, 100, () => Config.BodyOpacity, v => Config.BodyOpacity = v, sample: true);
        Slider(2, 304, 440, 308, 740, 328, 100, () => Config.DickOpacity, v => Config.DickOpacity = v, sample: true);
    }

    public void Open()
    {
        _before = Config.Clone();
        _volumesBefore = [_main.MusicVolume, _main.AmbientVolume, _main.EffectsVolume, _main.VoiceVolume];
        ShowPage(0);
        IsOpen = true;
    }

    public void ShowPage(int page)
    {
        Page = page;
        Buttons.Clear();
        Sliders.Clear();
        foreach (var button in _pageButtons[page])
        {
            button.IsHover = false;
            Buttons.Add(button);
        }
        foreach (var slider in _pageSliders[page])
        {
            slider.Refresh();
            Sliders.Add(slider);
        }
        RefreshButtons();
    }

    void RefreshButtons()
    {
        foreach (var button in Buttons)
            button.Refresh();
    }

    void Default()
    {
        Config.ResetPage(Page);
        if (Page == 0)
            (_main.MusicVolume, _main.AmbientVolume, _main.EffectsVolume, _main.VoiceVolume) = (80, 80, 100, 100);
        ShowPage(Page);
    }

    void Save()
    {
        Close();
        _main.SaveSettings();
    }

    void Cancel()
    {
        if (_before is not null)
            Config.CopyFrom(_before);
        if (_volumesBefore.Length == 4)
            (_main.MusicVolume, _main.AmbientVolume, _main.EffectsVolume, _main.VoiceVolume) =
                (_volumesBefore[0], _volumesBefore[1], _volumesBefore[2], _volumesBefore[3]);
        Close();
    }

    void Close()
    {
        for (int i = 0; i < _tests.Length; i++)
            StopTest(i);
        IsOpen = false;
    }

    /// <summary>Esc / right click: like Cancel.</summary>
    public void Dismiss() => Cancel();

    // ------------------------------------------------------------------ test sounds

    /// <summary>
    /// What Test plays per channel. The exe's choice is not decoded; these are the first file of each kind:
    /// BGM bg01, ambience es01 (both looped), effect se01, and Manami's first date line.
    /// </summary>
    static readonly (string Archive, string File, AudioBus Bus, bool Loop)[] TestSounds =
    [
        ("sb3_0600.pp", "bg01_00_00_00_00.ogg", AudioBus.Music, true),
        ("sb3_0600.pp", "es01_00_00_00_00.ogg", AudioBus.Ambient, true),
        ("sb3_0600.pp", "se01_00_00_00_00.ogg", AudioBus.Effects, false),
        ("sb3_0601.pp", "pd01_01_01.wav", AudioBus.Voice, false),
    ];

    bool IsTesting(int channel) => _tests[channel] is { IsPlaying: true };

    void ToggleTest(int channel)
    {
        if (IsTesting(channel))
            StopTest(channel);
        else
        {
            var (archive, file, bus, loop) = TestSounds[channel];
            try
            {
                if (_fs.ExistsReferenced(archive, file))
                    _tests[channel] = AudioSystem.Shared.Play(_fs, archive, file, bus, loop);
            }
            catch (Exception ex) when (ex is System.IO.InvalidDataException or System.IO.IOException or NotSupportedException)
            {
            }
        }
        RefreshButtons();
    }

    void StopTest(int channel)
    {
        _tests[channel]?.Stop(0.1);
        _tests[channel] = null;
    }

    /// <summary>Called by the view's timer so a finished test sound shows its idle icon again.</summary>
    public void Tick()
    {
        if (IsOpen && Page == 0)
            RefreshButtons();
    }
}
