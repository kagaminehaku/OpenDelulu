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
/// What happens when the girl's oil animation enters a clip (lh01_PP_00, one row per clip of onBB_PP.xa; the exe copies
/// each row into a 0x18-byte record and applies it when the clip changes, 0x4A52E1): her eye and mouth expressions
/// (face morph blocks: 0 default, 1 happy, 6 surprised, 7 troubled, 12 aroused…) and a voice row of lp01_LL to say,
/// with a priority against the line she is saying (0 = no voice). Columns 1, 4, 5 and 6..8 are not understood yet.
/// </summary>
public readonly record struct OilClipEvent(int Eyes, int Mouth, int VoiceRow, int Priority);

/// <summary>
/// The sun-oil scene's tables: touch areas (la01_01_LL), the player's hand (ld00 row 31: te01.xx/.xa) and the voice
/// lists (lp01_LL: rows of "po%02d_LL_SS_00_NN.wav", picked at random within a row).
/// </summary>
public sealed class OilTables
{
    public required IReadOnlyList<OilTouchArea> Areas { get; init; }
    public AssetRef? Hand { get; init; }
    public AssetRef? HandAnimation { get; init; }
    /// <summary>The other hand, te02 (lf01 column 25 = 2).</summary>
    public AssetRef? LeftHand { get; init; }
    public AssetRef? LeftHandAnimation { get; init; }
    /// <summary>Voice file name patterns per lp row (girl id still to be formatted in).</summary>
    public required IReadOnlyDictionary<int, IReadOnlyList<string>> Voices { get; init; }

    /// <summary>Per clip of the pose's animation (lh01_PP_00).</summary>
    public IReadOnlyDictionary<int, OilClipEvent> ClipEvents { get; init; } = new Dictionary<int, OilClipEvent>();

    /// <summary>The oil drips (ld00 row 32: ek01..03 .xx/.xa, one per kind of oil).</summary>
    public IReadOnlyList<(AssetRef Model, AssetRef? Animation)> Drips { get; init; } = [];

    /// <summary>Special touches (lf01): grouped by la01's special index, ten rows per group.</summary>
    public IReadOnlyList<OilAction> Actions { get; init; } = [];

    public static OilTables Load(GameFileSystem fs, string tableArchive, int level, int pose = 1)
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

        AssetRef? hand = null, handAnimation = null, leftHand = null, leftHandAnimation = null;
        var skins = Table("ld00_00_00_00_00.lst");
        if (skins?.Rows.FirstOrDefault(r => r.Length > 8 && r[0] == "31") is { } handRow)
        {
            hand = AssetRef.FromRow(handRow, 1);
            handAnimation = AssetRef.FromRow(handRow, 3);
            leftHand = AssetRef.FromRow(handRow, 5);
            leftHandAnimation = AssetRef.FromRow(handRow, 7);
        }
        var drips = new List<(AssetRef, AssetRef?)>();
        if (skins?.Rows.FirstOrDefault(r => r.Length > 2 && r[0] == "32") is { } dripRow)
            for (int c = 1; c + 1 < dripRow.Length; c += 4)
                if (AssetRef.FromRow(dripRow, c) is { } model && model.File.EndsWith(".xx", StringComparison.OrdinalIgnoreCase))
                    drips.Add((model, AssetRef.FromRow(dripRow, c + 2)));

        var events = new Dictionary<int, OilClipEvent>();
        foreach (var row in Table($"lh01_{Math.Clamp(pose, 1, 9):00}_00_00_00.lst")?.Rows ?? [])
        {
            if (row.Length < 11 || !int.TryParse(row[0], out int clip))
                continue;
            int Int(int column) => int.TryParse(row[column], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
            events[clip] = new OilClipEvent(Int(2), Int(3), Int(9), Int(10));
        }

        var actions = new List<OilAction>();
        foreach (var row in Table("lf01_00_00_00_00.lst")?.Rows ?? [])
        {
            if (row.Length < 28 || !int.TryParse(row[0], out int n) || row[1] == "0" || !row[2].StartsWith("o01_", StringComparison.Ordinal))
                continue;
            int Int(int column) => int.TryParse(row[column], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
            actions.Add(new OilAction(n, row[1], row[2], row[3], Int(21), Int(22), Int(25), Int(26)));
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
        return new OilTables
        {
            Areas = areas, Hand = hand, HandAnimation = handAnimation, LeftHand = leftHand, LeftHandAnimation = leftHandAnimation,
            Voices = voices, ClipEvents = events, Drips = drips, Actions = actions,
        };
    }
}

/// <summary>
/// A special touch of lf01: its name (左乳もみA体型(ブラあり) = kneading the left breast, body type A, wearing the top…),
/// the body locators the hand attaches to (right/left), the hand's clip in te01/te02.xa (11/16/21 = kneading for body
/// types A/B/C, 26 pulling the top…), the girl's clip (109 kneading, 114 pulling the top, 119 pulling a nipple…; the
/// loop, small and big reactions and the way back follow it) and the hand used (1 = te01, 2 = te02). Rows N*10+1..N*10+9
/// belong to la01's special index N; their variants differ by body type (A/B/C体型) and what she wears
/// (ブラあり with the top, ブラなし without, 全裸 nude). Column 26 is the action's button, st00_01_(N+1)
/// (0 Stroke Gently, 1 Tug Bra, 2 Finger Nipple, 3 Under Suit, 5 Expose Tits, 9 Massage Ass, 10 Pull Outward…).
/// Columns 4..20 and 23..24 are not understood yet.
/// </summary>
public sealed record OilAction(int Row, string Name, string Locator, string LeftLocator, int HandClip, int GirlClip, int Hand, int Button = -1)
{
    public int Group => (Row - 1) / 10;
}
