using Avalonia.Controls;
using Avalonia.Input;
using OpenDelulu.ViewModels;

namespace OpenDelulu.Views;

/// <summary>The original confirmation box; pointer handlers forward to <see cref="PlayViewModel"/>.</summary>
public partial class ConfirmView : UserControl
{
    public ConfirmView()
    {
        InitializeComponent();
    }

    PlayViewModel? Play => DataContext as PlayViewModel;

    void OnYesEntered(object? sender, PointerEventArgs e) => SetHover(yes: true, value: true);
    void OnYesExited(object? sender, PointerEventArgs e) => SetHover(yes: true, value: false);
    void OnNoEntered(object? sender, PointerEventArgs e) => SetHover(yes: false, value: true);
    void OnNoExited(object? sender, PointerEventArgs e) => SetHover(yes: false, value: false);

    void SetHover(bool yes, bool value)
    {
        if (Play is not { } play)
            return;
        if (yes)
            play.YesHovered = value;
        else
            play.NoHovered = value;
    }

    void OnYesPressed(object? sender, PointerPressedEventArgs e)
    {
        Play?.AnswerConfirm(true);
        e.Handled = true;
    }

    void OnNoPressed(object? sender, PointerPressedEventArgs e)
    {
        Play?.AnswerConfirm(false);
        e.Handled = true;
    }
}
