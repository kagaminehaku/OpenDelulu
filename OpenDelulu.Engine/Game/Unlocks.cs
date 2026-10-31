using OpenDelulu.Engine.Characters;

namespace OpenDelulu.Engine.Game;

/// <summary>
/// The exe's unlocks of swimsuits and accessories (sb_ItemListData.cpp), kept in <see cref="GameProgress"/>:
/// <list type="bullet">
/// <item>Rank (0x4283A0): (level − 1) × 3 + 1, 2 or 3 by love (0..86, 87..171, 172..255), at most 15. Every swimsuit whose
/// lbNN column 60 is between 1 and the best rank reached opens (0x428480), except the girls' reward suits.</item>
/// <item>A girl's first pick on the island map (03_chrsel, 0x428CE0): her suit (table 0x45C420) and accessories (0x45C470,
/// one or two rows per place).</item>
/// <item>Her completion, level 5 with love 255 (05_date, 0x428F10): a second suit and set (0x45C8F0 / 0x45C940).</item>
/// <item>The end of an oil or H scene (0x428590, three times): a random accessory not yet open, from a random lc table.</item>
/// </list>
/// Girls are indexed 0..7 as 1, 2, 3, 4, 5, 6, 11, 12. New unlocks are stored as 1 ("new"), the completion ones as 3 like
/// the exe.
/// </summary>
public static class Unlocks
{
    /// <summary>Girl ids by the exe's index (tickets, rewards).</summary>
    public static readonly int[] Girls = [1, 2, 3, 4, 5, 6, 11, 12];

    /// <summary>lc table (1..11) of each of the 15 accessory places (lc00 rows 1..15).</summary>
    public static readonly int[] PlaceTables = [1, 2, 3, 3, 4, 5, 6, 7, 8, 9, 8, 9, 10, 10, 11];

    static readonly int[] FirstSwimsuit = [41, 141, 151, 61, 21, 101, 62, 250];
    static readonly int[] CompleteSwimsuit = [5, 15, 35, 166, 14, 7, 124, 250];

    // Per girl, per place: one or two lc rows (0 = none).
    static readonly (int, int)[][] FirstAccessories =
    [
        [(1, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (1, 0), (0, 0), (0, 0), (1, 0), (0, 0), (0, 0), (0, 0), (1, 0)],
        [(11, 0), (0, 0), (0, 0), (0, 0), (1, 0), (0, 0), (0, 0), (0, 0), (6, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (6, 0)],
        [(16, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (11, 0), (0, 0), (11, 0), (0, 0), (0, 0), (0, 0), (11, 0)],
        [(0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (16, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (16, 0)],
        [(0, 0), (1, 0), (0, 0), (0, 0), (6, 0), (0, 0), (1, 0), (6, 0), (0, 0), (0, 0), (21, 0), (0, 0), (0, 0), (0, 0), (21, 0)],
        [(0, 0), (6, 0), (1, 0), (1, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (1, 0), (1, 0), (26, 0)],
        [(0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (11, 12), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0)],
        [(0, 0), (0, 0), (0, 0), (0, 0), (36, 0), (0, 0), (26, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0)],
    ];

    static readonly (int, int)[][] CompleteAccessories =
    [
        [(51, 0), (0, 0), (0, 0), (0, 0), (0, 0), (1, 0), (0, 0), (46, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (76, 0)],
        [(0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (1, 0), (0, 0), (46, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (76, 0)],
        [(0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (41, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (61, 0)],
        [(0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (81, 0)],
        [(0, 0), (1, 0), (0, 0), (0, 0), (16, 0), (0, 0), (0, 0), (77, 76), (26, 0), (0, 0), (0, 0), (0, 0), (6, 0), (6, 0), (66, 0)],
        [(0, 0), (6, 0), (0, 0), (0, 0), (21, 0), (0, 0), (0, 0), (51, 52), (31, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (71, 0)],
        [(0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (36, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (86, 0)],
        [(0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (26, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0)],
    ];

    static readonly HashSet<int> RewardSuits = [.. FirstSwimsuit, .. CompleteSwimsuit];

    /// <summary>The accessory key of the item list: (table − 1) × 128 + row.</summary>
    public static int AccessoryKey(int table, int row) => (table - 1) * 128 + row;

    public static int Rank(GirlProgress girl)
    {
        int tier = girl.Love <= 86 ? 1 : girl.Love <= 171 ? 2 : 3;
        return Math.Clamp((girl.Intimacy - 1) * 3 + tier, 0, 15);
    }

    /// <summary>Whether the wardrobe offers a swimsuit (everything is open for progress older than the unlocks).</summary>
    public static bool HasSwimsuit(GameProgress progress, int row) => progress.Swimsuits is not { } open || open.ContainsKey(row);

    public static bool HasAccessory(GameProgress progress, int table, int row) =>
        progress.Accessories is not { } open || open.ContainsKey(AccessoryKey(table, row));

    /// <summary>Makes sure the unlock lists exist (a new game: nothing open until the rules open it).</summary>
    public static void Start(GameProgress progress)
    {
        progress.Swimsuits ??= [];
        progress.Accessories ??= [];
    }

    /// <summary>
    /// Opens what a girl's rank opens (after her love or level changed); returns the swimsuit rows newly opened.
    /// </summary>
    public static List<int> ApplyRank(GameProgress progress, CharacterDatabase characters, GirlProgress girl)
    {
        var opened = new List<int>();
        if (progress.Swimsuits is not { } open)
            return opened;
        int rank = Rank(girl);
        if (rank <= progress.Rank && progress.Rank > 0)
            return opened;
        progress.Rank = Math.Max(progress.Rank, rank);
        foreach (var suit in characters.Girls.SelectMany(g => g.Swimsuits).GroupBy(s => s.Row).Select(g => g.First()))
        {
            if (suit.Requirement < 1 || suit.Requirement > progress.Rank || RewardSuits.Contains(suit.Row) || open.ContainsKey(suit.Row))
                continue;
            open[suit.Row] = 1;
            opened.Add(suit.Row);
        }
        return opened;
    }

    /// <summary>Her first pick on the map: her reward suit and accessories. True when anything opened.</summary>
    public static bool Meet(GameProgress progress, int girlId)
    {
        var girl = progress.For(girlId);
        if (girl.Met)
            return false;
        girl.Met = true;
        return Reward(progress, girlId, FirstSwimsuit, FirstAccessories, 1);
    }

    /// <summary>
    /// Level 5 with full love: she is completed and her second reward opens. True when she just got completed.
    /// </summary>
    public static bool CheckCompleted(GameProgress progress, int girlId)
    {
        var girl = progress.For(girlId);
        if (girl.Completed || girl.Intimacy < 5 || girl.Love < 255)
            return false;
        girl.Completed = true;
        Reward(progress, girlId, CompleteSwimsuit, CompleteAccessories, 3);
        return true;
    }

    static bool Reward(GameProgress progress, int girlId, int[] suits, (int, int)[][] accessories, int state)
    {
        int index = Array.IndexOf(Girls, girlId);
        if (index < 0 || progress.Swimsuits is not { } openSuits || progress.Accessories is not { } openAccessories)
            return false;
        bool any = openSuits.TryAdd(suits[index], state);
        for (int place = 0; place < PlaceTables.Length; place++)
        {
            var (a, b) = accessories[index][place];
            foreach (int row in new[] { a, b })
                if (row > 0)
                    any |= openAccessories.TryAdd(AccessoryKey(PlaceTables[place], row), state);
        }
        return any;
    }

    /// <summary>
    /// The end of an oil or H scene: up to <paramref name="count"/> random accessories that exist and are not open yet,
    /// each from a random table. Returns them as (table, row).
    /// </summary>
    public static List<(int Table, int Row)> RandomAccessories(GameProgress progress, AccessoryDatabase accessories, int count = 3,
        Random? random = null)
    {
        random ??= Random.Shared;
        var opened = new List<(int, int)>();
        if (progress.Accessories is not { } open)
            return opened;
        for (int n = 0; n < count; n++)
        {
            var tables = Enumerable.Range(1, 11).OrderBy(_ => random.Next()).ToList();
            foreach (int table in tables)
            {
                if (accessories.Items(table).FirstOrDefault(i => !open.ContainsKey(AccessoryKey(table, i.Row))) is not { } item)
                    continue;
                open[AccessoryKey(table, item.Row)] = 1;
                opened.Add((table, item.Row));
                break;
            }
        }
        return opened;
    }

    /// <summary>
    /// The date activities that can happen per stage (1..9) and level (1..5), from the exe's date scene (0x47EA00): she
    /// picks one of the list at random, the repeated ones being likelier.
    /// </summary>
    static readonly int[][][] DateActivities =
    [
        [[1, 5, 5, 10, 10], [1, 2, 5, 6, 6, 8, 8, 10, 10, 11, 11], [1, 2, 3, 5, 6, 7, 8, 9, 10, 11], [1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13], [1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 16, 17]],
        [[4], [4], [4], [4], [4, 18]],
        [[1, 5, 5, 10, 10], [1, 2, 5, 6, 6, 8, 8, 10, 10, 11, 11], [1, 2, 3, 5, 6, 7, 8, 9, 10, 11], [1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13], [1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 16, 17]],
        [[1, 5, 5, 10, 10], [1, 2, 5, 8, 8, 10, 10, 11, 11], [1, 2, 3, 5, 7, 8, 9, 10, 11], [1, 2, 3, 5, 7, 8, 9, 10, 11, 12, 13], [1, 2, 3, 5, 7, 8, 9, 10, 11, 12, 13, 14, 16, 17]],
        [[1, 5, 5, 10, 10], [1, 2, 5, 8, 8, 10, 10, 11, 11], [1, 2, 3, 5, 7, 8, 9, 10, 11], [1, 2, 3, 5, 7, 8, 9, 10, 11, 12, 13], [1, 2, 3, 5, 7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 20]],
        [[1, 5, 5, 10, 10], [1, 2, 5, 8, 8, 10, 10, 11, 11], [1, 2, 3, 5, 7, 8, 9, 10, 11], [1, 2, 3, 5, 7, 8, 9, 10, 11, 12, 13], [1, 2, 3, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17]],
        [[1, 5], [1, 2, 5], [1, 2, 3, 5], [1, 2, 3, 5, 12], [1, 2, 3, 5, 12]],
        [[1, 2], [1, 2], [1, 2], [1, 2], [1, 2, 19]],
        [[1, 5], [1, 2, 5], [1, 2, 3, 5], [1, 2, 3, 5, 12], [1, 2, 3, 5, 12, 21]],
    ];

    /// <summary>The activity pool of a stage and level (with repeats), or null for a stage outside the table.</summary>
    public static IReadOnlyList<int>? DateActivityPool(int stage, int level) =>
        stage is >= 1 and <= 9 ? DateActivities[stage - 1][Math.Clamp(level, 1, 5) - 1] : null;

    /// <summary>Love points: capped at 255.</summary>
    public static void AddLove(GirlProgress girl, int points) => girl.Love = Math.Clamp(girl.Love + points, 0, 255);

    /// <summary>
    /// Love gained by a completed date at each level 1..5 (exe 0x4C8750: love += settings[level − 1], the settings
    /// being lg00 row 1 columns 36..40): 85, 51, 37, 26, 17, so 3, 5, 7, 10 and 15 dates fill her love.
    /// </summary>
    public static readonly int[] DefaultDateLove = [85, 51, 37, 26, 17];

    /// <summary>The date love of lg00 (row 1, columns 36..40), or <see cref="DefaultDateLove"/>.</summary>
    public static int[] DateLove(GameFileSystem fs, string tableArchive)
    {
        try
        {
            var row = fs.ReadTable(tableArchive, "lg00_00_00_00_00.lst").Rows.FirstOrDefault(r => r.Length > 40 && r[0] == "1");
            if (row is not null)
            {
                var values = Enumerable.Range(36, 5).Select(c => int.TryParse(row[c], out int v) ? v : 0).ToArray();
                if (values.All(v => v > 0))
                    return values;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
        }
        return DefaultDateLove;
    }
}
