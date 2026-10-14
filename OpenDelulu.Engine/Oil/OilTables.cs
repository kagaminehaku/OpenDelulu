using System.Globalization;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Formats.Lst;

namespace OpenDelulu.Engine.Oil;

/// <summary>Body regions of the touch areas (la01 column 3).</summary>
public enum OilRegion
{
    Head = 0,
    Torso = 1,
    Breasts = 2,
    Buttocks = 3,
    Crotch = 4,
    LeftArm = 5,
    RightArm = 6,
    LeftLeg = 7,
    RightLeg = 8,
}

/// <summary>
/// One touch area of la01_01_LL (LL = intimacy level): a hidden "o01_A_*" helper mesh of the body and what touching it
/// does. Columns: frame, unlocked at this level, region, special action (lf01 index, -1 = none), side, hand shape,
/// the girl's clip (60/65/70/75/80 = start waiting for touches on the upper body / left arm / right arm / left leg /
/// right leg in onGG_PP.xa; the waiting loop and the small/big reactions follow it) and the hand's clip in te01.xa
/// (9 = rubbing the body, 10 = rubbing an arm).
/// </summary>
public sealed record OilTouchArea(
    int Row, string Frame, bool Unlocked, OilRegion Region, int Special, int Side, int HandShape, int GirlClip, int HandClip)
{
    /// <summary>Breasts, buttocks and crotch refuse touches until the level unlocks them (column 2).</summary>
    public bool Refuses => !Unlocked && Special >= 0 && Region is OilRegion.Breasts or OilRegion.Buttocks or OilRegion.Crotch;
}

/// <summary>
/// The sun-oil scene's tables: touch areas (la01_01_LL), the player's hand (ld00 row 31: te01.xx/.xa) and the voice
/// lists (lp01_LL: rows of "po%02d_LL_SS_00_NN.wav", picked at random within a row).
/// </summary>
public sealed class OilTables
{
    public required IReadOnlyList<OilTouchArea> Areas { get; init; }
    public AssetRef? Hand { get; init; }
    public AssetRef? HandAnimation { get; init; }
    /// <summary>Voice file name patterns per lp row (girl id still to be formatted in).</summary>
    public required IReadOnlyDictionary<int, IReadOnlyList<string>> Voices { get; init; }

    public static OilTables Load(GameFileSystem fs, string tableArchive, int level)
    {
        level = Math.Clamp(level, 1, 5);
        LstTable? Table(string name)
        {
            try { return fs.ReadTable(tableArchive, name); }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException) { return null; }
        }

        var areas = new List<OilTouchArea>();
        foreach (var row in Table($"la01_01_{level:00}_00_00.lst")?.Rows ?? [])
        {
            if (row.Length < 9 || !int.TryParse(row[0], out int n) || !row[1].StartsWith("o01_A_", StringComparison.Ordinal))
                continue;
            int Int(int column) => int.TryParse(row[column], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
            areas.Add(new OilTouchArea(n, row[1], Int(2) != 0, (OilRegion)Int(3), Int(4), Int(5), Int(6), Int(7), Int(8)));
        }

        AssetRef? hand = null, handAnimation = null;
        var skins = Table("ld00_00_00_00_00.lst");
        if (skins?.Rows.FirstOrDefault(r => r.Length > 4 && r[0] == "31") is { } handRow)
        {
            hand = AssetRef.FromRow(handRow, 1);
            handAnimation = AssetRef.FromRow(handRow, 3);
        }

        // Voices exist from level 2 on (the oil scene's first level); level 1 uses level 2's lines.
        var voices = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var row in Table($"lp01_{Math.Max(2, level):00}_00_00_00.lst")?.Rows ?? [])
        {
            if (row.Length < 2 || !int.TryParse(row[0], out int n) || !int.TryParse(row[1], out int count) || count <= 0)
                continue;
            var files = new List<string>();
            for (int i = 0; i < count && 3 + i * 2 < row.Length; i++)
                if (row[3 + i * 2] is { Length: > 1 } file && file != "0")
                    files.Add(file);
            if (files.Count > 0)
                voices[n] = files;
        }
        return new OilTables { Areas = areas, Hand = hand, HandAnimation = handAnimation, Voices = voices };
    }
}
