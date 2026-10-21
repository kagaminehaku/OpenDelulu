using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using OpenDelulu.ViewModels;

namespace OpenDelulu.Views;

/// <summary>The original oil menu; clicks go to <see cref="PlayViewModel.PressOilHud"/>, the handle drags the menu.</summary>
public partial class OilHudView : UserControl
{
    Point? _dragFrom;

    public OilHudView()
    {
        InitializeComponent();
    }

    PlayViewModel? Play => DataContext as PlayViewModel;

    static bool IsHandle(HudQuad quad) => quad.Frame.EndsWith("Ido", System.StringComparison.Ordinal);

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
        if (IsHandle(quad))
        {
            _dragFrom = e.GetPosition(Quads);
            e.Pointer.Capture(control);
            return;
        }
        Play?.PressOilHud(quad);
    }

    void OnQuadMoved(object? sender, PointerEventArgs e)
    {
        if (_dragFrom is not { } from)
            return;
        var now = e.GetPosition(Quads);
        Play?.MoveOilHud(now.X - from.X, now.Y - from.Y);
        _dragFrom = now;
    }

    void OnQuadReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragFrom = null;
        e.Pointer.Capture(null);
    }
}
