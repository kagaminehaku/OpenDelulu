using System;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenDelulu.ViewModels;

/// <summary>
/// The original's confirmation box (53_check): a question picture st00_03_NN (6 "End Your Date Here?", 7 "Finish Rubbing
/// Oil?"…) with YES/NO buttons st00_03_00_01..04 that turn orange under the pointer. Without the textures the
/// translated question is written instead.
/// </summary>
public sealed partial class PlayViewModel
{
    public const int QuestionEndDate = 6, QuestionEndOil = 7;

    Action? _confirmYes, _confirmNo;

    [ObservableProperty] public partial bool IsConfirming { get; set; }
    [ObservableProperty] public partial Bitmap? ConfirmQuestion { get; set; }
    [ObservableProperty] public partial string ConfirmText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YesImage))]
    public partial bool YesHovered { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoImage))]
    public partial bool NoHovered { get; set; }

    public Bitmap? YesImage => Ui.Get(YesHovered ? "st00_03_00_02_00.tga" : "st00_03_00_01_00.tga");
    public Bitmap? NoImage => Ui.Get(NoHovered ? "st00_03_00_04_00.tga" : "st00_03_00_03_00.tga");

    /// <summary>Asks before doing something final; <paramref name="no"/> runs when declined.</summary>
    void Confirm(int question, string textKey, Action yes, Action? no = null)
    {
        _confirmYes = yes;
        _confirmNo = no;
        ConfirmQuestion = Ui.Get($"st00_03_{question:00}_00_00.tga");
        ConfirmText = T(textKey);
        YesHovered = NoHovered = false;
        Dialogue.Hide();
        IsConfirming = true;
    }

    /// <summary>The player's answer (buttons, Y/Enter, N).</summary>
    public void AnswerConfirm(bool yes)
    {
        if (!IsConfirming)
            return;
        IsConfirming = false;
        var action = yes ? _confirmYes : _confirmNo;
        _confirmYes = _confirmNo = null;
        action?.Invoke();
    }

    /// <summary>The Leave button: ends the date after asking.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void RequestLeave()
    {
        if (!InGame || IsConfirming)
            return;
        Confirm(QuestionEndDate, "confirm.endDate", Leave, () =>
        {
            if (_oil is not null)
                ShowOilMenu();
            else
                ShowMenu();
        });
    }
}
