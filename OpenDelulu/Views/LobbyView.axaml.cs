using Avalonia.Controls;
using Avalonia.Input;
using OpenDelulu.ViewModels;

namespace OpenDelulu.Views;

/// <summary>The hotel lobby; button presses go to <see cref="PlayViewModel.PressLobby"/>.</summary>
public partial class LobbyView : UserControl
{
    public LobbyView()
    {
        InitializeComponent();
    }

    void OnButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is LobbyButton button)
            (DataContext as PlayViewModel)?.PressLobby(button);
        e.Handled = true;
    }
}
