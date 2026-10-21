using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Engine.Oil;

namespace OpenDelulu.ViewModels;

/// <summary>An action button of the oil scene: the original picture st00_01_NN, or the table's name without it.</summary>
public sealed record OilActionChoice(OilAction Action, Bitmap? Image, string Label, double Y);

/// <summary>
/// The touch actions of an unlocked special area (lf01), offered as the original's pink buttons (st00_01_*) in a column
/// on the right; choosing one starts it, a press on her stops it.
/// </summary>
public sealed partial class PlayViewModel
{
    public ObservableCollection<OilActionChoice> OilActions { get; } = [];

    [ObservableProperty] public partial bool IsChoosingAction { get; set; }

    void OfferActions(OilTouchArea area, IReadOnlyList<OilAction> actions)
    {
        OilActions.Clear();
        double top = 384 - actions.Count * 32;
        foreach (var (action, i) in actions.Select((a, i) => (a, i)))
            OilActions.Add(new OilActionChoice(action, action.Button >= 0 ? Ui.Get($"st00_01_{action.Button + 1:00}_00_00.tga") : null,
                action.Name, top + i * 64));
        IsChoosingAction = OilActions.Count > 0;
    }

    public void ChooseOilAction(OilActionChoice choice)
    {
        IsChoosingAction = false;
        OilActions.Clear();
        _oil?.StartAction(choice.Action);
        SyncHand();
    }

    public void CancelOilActions()
    {
        IsChoosingAction = false;
        OilActions.Clear();
    }
}
