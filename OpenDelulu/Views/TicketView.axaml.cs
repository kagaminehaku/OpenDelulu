using Avalonia.Controls;
using Avalonia.Input;
using OpenDelulu.ViewModels;

namespace OpenDelulu.Views;

/// <summary>The ticket counter; clicks go to <see cref="PlayViewModel"/>.</summary>
public partial class TicketView : UserControl
{
    public TicketView()
    {
        InitializeComponent();
    }

    PlayViewModel? Play => DataContext as PlayViewModel;

    void OnTicketsPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control area)
            Play?.ChooseTicketAt(Canvas.GetTop(area) + e.GetPosition(area).Y);
        e.Handled = true;
    }

    void OnDeletePressed(object? sender, PointerPressedEventArgs e)
    {
        Play?.DeleteTicket();
        e.Handled = true;
    }

    void OnOkPressed(object? sender, PointerPressedEventArgs e)
    {
        Play?.OpenTicket();
        e.Handled = true;
    }

    void OnQuitPressed(object? sender, PointerPressedEventArgs e)
    {
        Play?.QuitFromTicket();
        e.Handled = true;
    }
}
