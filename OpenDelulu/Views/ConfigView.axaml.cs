using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using OpenDelulu.ViewModels;

namespace OpenDelulu.Views;

/// <summary>The original CONFIG screen; pointer handlers forward to <see cref="ConfigViewModel"/>.</summary>
public partial class ConfigView : UserControl
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public ConfigView()
    {
        InitializeComponent();
        // Test icons go back to idle when their sound ends.
        _timer.Tick += (_, _) => (DataContext as ConfigViewModel)?.Tick();
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    void OnButtonEntered(object? sender, PointerEventArgs e)
    {
        if ((sender as Control)?.DataContext is ConfigButton button)
            button.IsHover = true;
    }

    void OnButtonExited(object? sender, PointerEventArgs e)
    {
        if ((sender as Control)?.DataContext is ConfigButton button)
            button.IsHover = false;
    }

    void OnButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ConfigButton button)
            button.Click();
        e.Handled = true;
    }

    void SetSlider(object? sender, PointerEventArgs e)
    {
        if (sender is Control track && track.DataContext is ConfigSlider slider)
            slider.SetFromX(Canvas.GetLeft(track) + e.GetPosition(track).X);
    }

    void OnTrackPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Pointer.Capture(sender as IInputElement);
        SetSlider(sender, e);
        e.Handled = true;
    }

    void OnTrackMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Captured == sender)
            SetSlider(sender, e);
    }

    void OnTrackReleased(object? sender, PointerReleasedEventArgs e) => e.Pointer.Capture(null);
}
