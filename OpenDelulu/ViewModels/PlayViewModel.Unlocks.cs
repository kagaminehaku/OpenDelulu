using System.Collections.Generic;
using OpenDelulu.Engine.Game;

namespace OpenDelulu.ViewModels;

/// <summary>
/// The exe's unlocks during play (<see cref="Unlocks"/>): a girl's first pick opens her reward, love from dates, oil
/// and H raises the unlock rank, level 5 with full love completes her, and the end of an oil or H scene opens three
/// random accessories. The love amounts are not decoded: talking 5, a date activity 25, oil 15, H 30.
/// </summary>
public sealed partial class PlayViewModel
{
    /// <summary>She was picked on the island map.</summary>
    void MeetGirl(int girlId)
    {
        int suits = _progress.Swimsuits?.Count ?? 0, accessories = _progress.Accessories?.Count ?? 0;
        if (Unlocks.Meet(_progress, girlId))
            AnnounceUnlocks(suits, accessories);
        ApplyRank();
    }

    void GainLove(int points)
    {
        if (_girl is null)
            return;
        Unlocks.AddLove(Progress, points);
        ApplyRank();
    }

    /// <summary>After her love or level changed: the rank's swimsuits, and her completion reward.</summary>
    void ApplyRank()
    {
        if (_girl is null)
            return;
        int suits = _progress.Swimsuits?.Count ?? 0, accessories = _progress.Accessories?.Count ?? 0;
        Unlocks.ApplyRank(_progress, _assembler.Database, Progress);
        Unlocks.CheckCompleted(_progress, _girl.Girl.Id);
        AnnounceUnlocks(suits, accessories);
    }

    /// <summary>The end of an oil or H scene.</summary>
    void UnlockRandomAccessories()
    {
        int suits = _progress.Swimsuits?.Count ?? 0, accessories = _progress.Accessories?.Count ?? 0;
        Unlocks.RandomAccessories(_progress, _assembler.Accessories);
        AnnounceUnlocks(suits, accessories);
    }

    /// <summary>"Unlocked: 2 new swimsuits, 3 new accessories" in the status line when the counts grew.</summary>
    void AnnounceUnlocks(int suitsBefore, int accessoriesBefore)
    {
        int suits = (_progress.Swimsuits?.Count ?? 0) - suitsBefore, accessories = (_progress.Accessories?.Count ?? 0) - accessoriesBefore;
        var parts = new List<string>();
        if (suits > 0)
            parts.Add(F("play.unlockedSuits", suits));
        if (accessories > 0)
            parts.Add(F("play.unlockedAccessories", accessories));
        if (parts.Count > 0)
            ShowStatus(F("play.unlocked", string.Join(", ", parts)), seconds: 4);
        // The front desk tells it again back in the lobby, like the exe.
        _progress.PendingUnlocks |= (suits > 0 ? 1 : 0) | (accessories > 0 ? 2 : 0);
    }
}
