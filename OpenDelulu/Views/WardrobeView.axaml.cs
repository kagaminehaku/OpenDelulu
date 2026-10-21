using Avalonia.Controls;
using Avalonia.Input;
using OpenDelulu.ViewModels;

namespace OpenDelulu.Views;

/// <summary>The original swimsuit screen; pointer handlers forward to <see cref="PlayViewModel"/>.</summary>
public partial class WardrobeView : UserControl
{
    public WardrobeView()
    {
        InitializeComponent();
    }

    PlayViewModel? Play => DataContext as PlayViewModel;

    void OnCategoryPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control tabs)
            Play?.ChooseWardrobeCategoryAt(Canvas.GetTop(tabs) + e.GetPosition(tabs).Y);
        e.Handled = true;
    }

    void OnPageUpPressed(object? sender, PointerPressedEventArgs e)
    {
        Play?.TurnWardrobePage(-1);
        e.Handled = true;
    }

    void OnPageDownPressed(object? sender, PointerPressedEventArgs e)
    {
        Play?.TurnWardrobePage(1);
        e.Handled = true;
    }

    void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SwimsuitCard card)
            Play?.ChooseSwimsuitCard(card);
        e.Handled = true;
    }

    void OnOkPressed(object? sender, PointerPressedEventArgs e)
    {
        Play?.CloseWardrobe(keep: true);
        e.Handled = true;
    }

    void OnCancelPressed(object? sender, PointerPressedEventArgs e)
    {
        Play?.CloseWardrobe(keep: false);
        e.Handled = true;
    }
}
