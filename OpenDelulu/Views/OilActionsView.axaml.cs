using Avalonia.Controls;
using Avalonia.Input;
using OpenDelulu.ViewModels;

namespace OpenDelulu.Views;

/// <summary>The oil scene's action buttons; pointer handlers forward to <see cref="PlayViewModel"/>.</summary>
public partial class OilActionsView : UserControl
{
    public OilActionsView()
    {
        InitializeComponent();
    }

    void OnActionPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is OilActionChoice choice && DataContext is PlayViewModel play)
            play.ChooseOilAction(choice);
        e.Handled = true;
    }

    void OnCancelPressed(object? sender, PointerPressedEventArgs e)
    {
        (DataContext as PlayViewModel)?.CancelOilActions();
        e.Handled = true;
    }
}
