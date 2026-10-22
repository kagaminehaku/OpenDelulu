using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Engine.Hud;
using OpenDelulu.Services;

namespace OpenDelulu.ViewModels;

/// <summary>One quad of an original ds menu as drawn: picture, hover fill, place on the 1024×768 screen.</summary>
public sealed partial class HudQuad(string frame, string key) : ObservableObject
{
    public string Frame { get; } = frame;
    /// <summary>The frame name without "o01_P_" (e.g. "OilIcon01", "Oil02_CK").</summary>
    public string Key { get; } = key;
    [ObservableProperty] public partial Bitmap? Image { get; set; }
    [ObservableProperty] public partial Bitmap? HoverImage { get; set; }
    [ObservableProperty] public partial double X { get; set; }
    [ObservableProperty] public partial double Y { get; set; }
    [ObservableProperty] public partial double W { get; set; }
    [ObservableProperty] public partial double H { get; set; }
    [ObservableProperty] public partial bool IsVisible { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoverOpacity))]
    public partial bool IsHover { get; set; }

    /// <summary>The light blue m00_sentaku fill over the hovered quad.</summary>
    public double HoverOpacity => IsHover && HoverImage is not null ? 0.5 : 0;

    /// <summary>Check marks take no pointer.</summary>
    public bool IsHitTestable { get; init; } = true;

    public bool IsCheck => Key.EndsWith("_CK", StringComparison.Ordinal);
    public bool IsHandle => Key.EndsWith("Ido", StringComparison.Ordinal);
}

/// <summary>
/// An original in-game menu (a <see cref="DsMenu"/>) on screen: the move handle drags it, …Min folds it to its title
/// bar (シーン最小化) and back, …Close hides it until <see cref="Reopen"/>. The owner says which list is unfolded
/// (<see cref="State"/>: the clip name without its A..D suffix), supplies pictures for item quads and handles presses.
/// </summary>
public sealed partial class DsHud : ObservableObject
{
    readonly DsMenu _menu;
    readonly Dictionary<string, HudQuad> _byKey = [];
    readonly Dictionary<string, Bitmap?> _bitmaps = [];
    readonly double _width, _height;
    char _variant = 'A';

    public DsHud(DsMenu menu, double width, double height)
    {
        _menu = menu;
        _width = width;
        _height = height;
        foreach (var quad in menu.Quads)
        {
            string key = quad.Frame.StartsWith("o01_P_", StringComparison.Ordinal) ? quad.Frame[6..] : quad.Frame;
            var item = new HudQuad(quad.Frame, key) { IsHitTestable = !key.EndsWith("_CK", StringComparison.Ordinal) };
            _byKey[key] = item;
            Quads.Add(item);
        }
    }

    public ObservableCollection<HudQuad> Quads { get; } = [];

    [ObservableProperty] public partial bool IsVisible { get; set; }
    /// <summary>Closed with its × button (the owner offers a way back).</summary>
    [ObservableProperty] public partial bool IsClosed { get; set; }

    public bool IsMinimized { get; private set; }

    /// <summary>Top-left of the menu on the 1024×768 screen.</summary>
    public double X { get; private set; }
    public double Y { get; private set; }

    /// <summary>The unfolded state as a clip name without suffix ("シーン表示" when nothing is open).</summary>
    public Func<string> State { get; set; } = () => "シーン表示";

    /// <summary>The owner handles "…Close" itself (the system bar slides away instead of closing).</summary>
    public bool OwnerHandlesClose { get; init; }

    /// <summary>The clip of the folded menu (シーン最小化; カメラ最小化 for the camera menu).</summary>
    public string MinimizedState { get; set; } = "シーン最小化";

    /// <summary>Picture for a quad key (null = the embedded texture).</summary>
    public Func<string, Bitmap?>? ImageFor { get; set; }

    /// <summary>Whether the check mark of an item key (without "_CK") shows.</summary>
    public Func<string, bool>? IsChecked { get; set; }

    /// <summary>A quad was clicked (handle, minimise and close are handled here).</summary>
    public event Action<string>? Pressed;

    public HudQuad? this[string key] => _byKey.GetValueOrDefault(key);

    public void Show()
    {
        Play(instant: true);
        IsVisible = !IsClosed;
    }

    public void Hide()
    {
        IsVisible = false;
        IsClosed = false;
    }

    public void Reopen()
    {
        IsClosed = false;
        IsVisible = true;
        Refresh();
    }

    public void ToggleClosed()
    {
        if (IsVisible)
        {
            IsClosed = true;
            IsVisible = false;
        }
        else
            Reopen();
    }

    /// <summary>Plays the clip of the current state (A..D variant when the menu has them).</summary>
    public void Play(bool instant)
    {
        string state = IsMinimized ? MinimizedState : State();
        if (!_menu.Play(state + _variant, instant))
            _menu.Play(state, instant);
        Refresh();
    }

    public void Tick(double seconds)
    {
        if (IsVisible && _menu.IsAnimating)
        {
            _menu.Advance(seconds);
            Refresh();
        }
    }

    public void Press(HudQuad quad)
    {
        if (quad.Key.EndsWith("Min", StringComparison.Ordinal))
        {
            IsMinimized = !IsMinimized;
            Play(instant: false);
        }
        else if (quad.Key.EndsWith("Close", StringComparison.Ordinal) && !OwnerHandlesClose)
        {
            IsClosed = true;
            IsVisible = false;
        }
        else if (!quad.IsHandle)
            Pressed?.Invoke(quad.Key);
    }

    /// <summary>Drags the menu (a delta on the 1024×768 screen); lists open towards the free side.</summary>
    public void Move(double dx, double dy)
    {
        X = Math.Clamp(X + dx, 0, 1024 - 96);
        Y = Math.Clamp(Y + dy, 0, 768 - 24);
        bool right = X + _width <= 1024, down = Y + _height / 2 <= 384;
        char variant = (right, down) switch { (true, true) => 'A', (false, true) => 'B', (true, false) => 'C', _ => 'D' };
        if (variant != _variant)
        {
            _variant = variant;
            Play(instant: true);
        }
        else
            Refresh();
    }

    Bitmap? Embedded(string name)
    {
        if (name.Length == 0)
            return null;
        if (_bitmaps.TryGetValue(name, out var cached))
            return cached;
        Bitmap? bitmap = null;
        try
        {
            if (_menu.Texture(name) is { } image)
                bitmap = UiTextures.ToBitmap(image);
        }
        catch (InvalidOperationException)
        {
            // No bitmap support (headless view-model tests): the menu still works, without pictures.
        }
        return _bitmaps[name] = bitmap;
    }

    public void Refresh()
    {
        foreach (var layout in _menu.Layout())
        {
            if (!_byKey.TryGetValue(layout.Quad.Frame.StartsWith("o01_P_", StringComparison.Ordinal) ? layout.Quad.Frame[6..] : layout.Quad.Frame, out var item))
                continue;
            item.X = X + layout.X;
            item.Y = Y + layout.Y;
            item.W = layout.Width;
            item.H = layout.Height;
            var textures = layout.Quad.Textures;
            item.HoverImage ??= textures.Count > 1 ? Embedded(textures[1]) : null;
            if (item.IsCheck)
            {
                item.Image ??= textures.Count > 0 ? Embedded(textures[0]) : null;
                item.IsVisible = layout.IsVisible && IsChecked?.Invoke(item.Key[..^3]) == true;
                continue;
            }
            // …Min shows "minimise" (m00_min) or, when minimised, "restore" (m00_max, its third texture).
            item.Image = item.Key.EndsWith("Min", StringComparison.Ordinal)
                ? Embedded(IsMinimized && textures.Count > 2 ? textures[2] : textures.FirstOrDefault() ?? "")
                : ImageFor?.Invoke(item.Key) ?? (textures.Count > 0 ? Embedded(textures[0]) : null);
            item.IsVisible = layout.IsVisible;
        }
    }
}
