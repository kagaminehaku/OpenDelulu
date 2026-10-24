using Avalonia.Controls;
using Avalonia.Input;
using OpenDelulu.ViewModels;

namespace OpenDelulu.Views;

/// <summary>The photo menu; clicks go to <see cref="PlayViewModel.PressPhoto"/>.</summary>
public partial class PhotoView : UserControl
{
    public PhotoView()
    {
        InitializeComponent();
    }

    void OnMenuPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control area && DataContext is PlayViewModel play)
        {
            var p = e.GetPosition(area);
            e.Handled = play.PressPhoto(Canvas.GetLeft(area) + p.X, Canvas.GetTop(area) + p.Y);
        }
    }
}
