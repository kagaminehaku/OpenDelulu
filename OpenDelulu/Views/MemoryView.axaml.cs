using Avalonia.Controls;
using Avalonia.Input;
using OpenDelulu.ViewModels;

namespace OpenDelulu.Views;

/// <summary>The memory screens; clicks go to <see cref="PlayViewModel.PressMemory"/>.</summary>
public partial class MemoryView : UserControl
{
    public MemoryView()
    {
        InitializeComponent();
    }

    void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control area && DataContext is PlayViewModel play)
        {
            var p = e.GetPosition(area);
            play.PressMemory(p.X, p.Y);
        }
        e.Handled = true;
    }
}
