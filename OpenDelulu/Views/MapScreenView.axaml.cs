using Avalonia.Controls;
using Avalonia.Input;
using OpenDelulu.ViewModels;

namespace OpenDelulu.Views;

/// <summary>The original island map: pointer handlers forward to <see cref="PlayViewModel"/>.</summary>
public partial class MapScreenView : UserControl
{
    public MapScreenView()
    {
        InitializeComponent();
    }

    PlayViewModel? Play => DataContext as PlayViewModel;

    void OnGirlEntered(object? sender, PointerEventArgs e) => Play?.HoverGirl((sender as Control)?.DataContext as MapGirl);

    void OnGirlExited(object? sender, PointerEventArgs e)
    {
        if (Play is { } play && ReferenceEquals(play.HoveredGirl, (sender as Control)?.DataContext))
            play.HoverGirl(null);
    }

    void OnGirlPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MapGirl girl)
            Play?.ChooseMapGirl(girl);
        e.Handled = true;
    }

    void OnStageEntered(object? sender, PointerEventArgs e) => Play?.HoverStage((sender as Control)?.DataContext as MapStage);

    void OnStageExited(object? sender, PointerEventArgs e)
    {
        if (Play is { } play && ReferenceEquals(play.HoveredStage, (sender as Control)?.DataContext))
            play.HoverStage(null);
    }

    void OnStagePressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MapStage stage)
            Play?.ChooseMapStage(stage);
        e.Handled = true;
    }

    void OnTimePressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MapTime time)
            Play?.ChooseMapTime(time);
        e.Handled = true;
    }

    void OnCancelPressed(object? sender, PointerPressedEventArgs e)
    {
        Play?.CancelMapStage();
        e.Handled = true;
    }
}
