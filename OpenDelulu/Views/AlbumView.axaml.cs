using Avalonia.Controls;
using Avalonia.Input;
using OpenDelulu.ViewModels;

namespace OpenDelulu.Views;

/// <summary>The original CG album; pointer handlers forward to <see cref="PlayViewModel"/>.</summary>
public partial class AlbumView : UserControl
{
    public AlbumView()
    {
        InitializeComponent();
    }

    PlayViewModel? Play => DataContext as PlayViewModel;

    int TabAt(object? sender, PointerEventArgs e)
    {
        if (sender is not Control strip)
            return -1;
        var p = e.GetPosition(strip);
        return PlayViewModel.AlbumTabAt(Canvas.GetLeft(strip) + p.X, Canvas.GetTop(strip) + p.Y);
    }

    void OnTabsMoved(object? sender, PointerEventArgs e)
    {
        if (Play is { } play)
            play.AlbumHoverTab = TabAt(sender, e);
    }

    void OnTabsExited(object? sender, PointerEventArgs e)
    {
        if (Play is { } play)
            play.AlbumHoverTab = -1;
    }

    void OnTabsPressed(object? sender, PointerPressedEventArgs e)
    {
        Play?.ChooseAlbumTab(TabAt(sender, e));
        e.Handled = true;
    }

    void OnSlotPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AlbumSlot slot)
            Play?.ChooseAlbumSlot(slot);
        e.Handled = true;
    }

    void OnCancelPressed(object? sender, PointerPressedEventArgs e)
    {
        Play?.CloseAlbum();
        e.Handled = true;
    }

    void OnPicturePressed(object? sender, PointerPressedEventArgs e)
    {
        Play?.CloseAlbumPicture();
        e.Handled = true;
    }
}
