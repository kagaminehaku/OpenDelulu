using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using OpenDelulu.ViewModels;

namespace OpenDelulu.Views;

/// <summary>An original in-game menu; clicks go to <see cref="DsHud.Press"/>, the handle drags the menu.</summary>
public partial class DsHudView : UserControl
{
    Point? _dragFrom;

    public DsHudView()
    {
        InitializeComponent();
    }

    DsHud? Hud => DataContext as DsHud;

    void OnQuadEntered(object? sender, PointerEventArgs e)
    {
        if ((sender as Control)?.DataContext is HudQuad quad)
            quad.IsHover = true;
    }

    void OnQuadExited(object? sender, PointerEventArgs e)
    {
        if ((sender as Control)?.DataContext is HudQuad quad)
            quad.IsHover = false;
    }

    void OnQuadPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not HudQuad quad)
            return;
        e.Handled = true;
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            return;
        if (quad.IsHandle)
        {
            _dragFrom = e.GetPosition(Quads);
            e.Pointer.Capture(control);
            return;
        }
        Hud?.Press(quad);
    }

    void OnQuadMoved(object? sender, PointerEventArgs e)
    {
        if (_dragFrom is not { } from)
            return;
        var now = e.GetPosition(Quads);
        Hud?.Move(now.X - from.X, now.Y - from.Y);
        _dragFrom = now;
    }

    void OnQuadReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragFrom = null;
        e.Pointer.Capture(null);
    }
}
