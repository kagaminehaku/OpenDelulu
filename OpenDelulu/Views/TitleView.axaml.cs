using Avalonia.Controls;
using Avalonia.Input;
using OpenDelulu.ViewModels;

namespace OpenDelulu.Views;

/// <summary>The original title screen; pointer handlers forward to <see cref="PlayViewModel"/>.</summary>
public partial class TitleView : UserControl
{
    public TitleView()
    {
        InitializeComponent();
    }

    PlayViewModel? Play => DataContext as PlayViewModel;

    void OnEntryEntered(object? sender, PointerEventArgs e)
    {
        if ((sender as Control)?.DataContext is HudQuad quad)
            Play?.HoverTitle(quad);
    }

    void OnEntryPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is HudQuad quad)
            Play?.PressTitle(quad);
        e.Handled = true;
    }
}
