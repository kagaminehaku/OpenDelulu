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

    void OnPageTabsPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control tabs)
        {
            var p = e.GetPosition(tabs);
            Play?.ChooseWardrobePageAt(Canvas.GetLeft(tabs) + p.X, Canvas.GetTop(tabs) + p.Y);
        }
        e.Handled = true;
    }

    void OnColorPartPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control tabs)
            Play?.ChooseColorPartAt(Canvas.GetTop(tabs) + e.GetPosition(tabs).Y);
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

    void OnHairPresetPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is HairPreset preset)
            Play?.ChooseHairPreset(preset);
        e.Handled = true;
    }

    void OnFavoriteRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control row)
            Play?.ChooseFavoriteAt(Canvas.GetTop(row) + e.GetPosition(row).Y);
        e.Handled = true;
    }

    void OnFavoriteDeletePressed(object? sender, PointerPressedEventArgs e)
    {
        Play?.DeleteFavorite();
        e.Handled = true;
    }

    void OnFavoriteSavePressed(object? sender, PointerPressedEventArgs e)
    {
        Play?.SaveFavorite();
        e.Handled = true;
    }

    void OnFavoriteLoadPressed(object? sender, PointerPressedEventArgs e)
    {
        Play?.LoadFavorite();
        e.Handled = true;
    }
}
