using System.Globalization;

namespace OpenDelulu.Formats.Lst;

/// <summary>
/// Typed views of the gameplay tables loaded by sb_LoadList.cpp. Which table is used where comes from the exe's
/// load-error strings next to each name format (e.g. "%slx%02d_00_00_00_00.lst" sits in the body init code).
/// Column meanings that are not documented here are still unknown and are exposed as raw numbers.
/// </summary>
public static class GameTables
{
    /// <summary>Parses a table cell as a number; empty cells are 0.</summary>
    public static double Number(string cell) =>
        cell.Length == 0 ? 0 : double.Parse(cell, NumberStyles.Float, CultureInfo.InvariantCulture);

    public static bool IsNumber(string cell) =>
        cell.Length == 0 || double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>Data rows (row 0 is a defaults/header row), without the leading row-index column.</summary>
    /// Trailing rows with an empty index column (lh04_00_01) are skipped.
    internal static IEnumerable<(int Row, string[] Cells)> DataRows(LstTable table) =>
        table.Rows.Skip(1).Where(r => r[0].Length > 0).Select(r => (int.Parse(r[0], CultureInfo.InvariantCulture), r[1..]));

    /// <summary>Validates a table by its name prefix; returns a short summary. Throws on unexpected layout.</summary>
    public static string? Check(LstTable table)
    {
        string prefix = table.Name.Length >= 2 ? table.Name[..2].ToLowerInvariant() : "";
        return prefix switch
        {
            "lf" => $"{TouchActionTable.Read(table).Actions.Count} touch actions",
            "lp" => $"{VoiceListTable.Read(table).Lists.Count(l => l.Count > 0)} voice lists",
            "lx" => $"{FrameGroupTable.Read(table).Groups.Count} groups",
            "ly" => $"{TouchAreaTable.Read(table).Rows.Count} area rows",
            "lz" => $"{UiRectTable.Read(table).Rects.Count(r => r.Width > 0)} rects",
            "lg" or "lh" => $"{NumericTable.Read(table).Rows.Count} numeric rows",
            _ => null,
        };
    }
}

/// <summary>
/// lfNN: the touch actions (左乳もみA体型(ブラあり) = knead left breast, body type A, with bra; ブラ引っ張り = pull bra…).
/// Columns: name, two frame names (where the hand attaches), 23 numbers (animation/flag ids, still raw),
/// the hand model frame (o01_P_Te) and a final number. Rows whose name is "0" are unused slots.
/// </summary>
public sealed class TouchActionTable
{
    public sealed record TouchAction(int Row, string Name, string Frame, string TargetFrame, double[] Values, string HandFrame, double Last);

    public IReadOnlyList<TouchAction> Actions { get; init; } = [];

    public static TouchActionTable Read(LstTable table)
    {
        var actions = new List<TouchAction>();
        foreach (var (row, c) in GameTables.DataRows(table))
        {
            if (c.Length < 28)
                throw new InvalidDataException($"{table.Name} row {row}: expected 28 columns, got {c.Length}.");
            if (c[0] == "0")
                continue;
            actions.Add(new TouchAction(row, c[0], c[1], c[2], [.. c[3..26].Select(GameTables.Number)], c[26], GameTables.Number(c[27])));
        }
        return new TouchActionTable { Actions = actions };
    }
}

/// <summary>
/// lpNN_MM_KK: voice lists. Each row is one situation: a count n, then n (archive, file) pairs to pick from at
/// random. Archive names are formats ("%s_%d60%d.pp") and files contain %02d for the character number.
/// </summary>
public sealed class VoiceListTable
{
    public IReadOnlyList<IReadOnlyList<(string Archive, string File)>> Lists { get; init; } = [];

    public static VoiceListTable Read(LstTable table)
    {
        var lists = new List<IReadOnlyList<(string, string)>>();
        foreach (var (row, c) in GameTables.DataRows(table))
        {
            int count = (int)GameTables.Number(c[0]);
            if (count < 0 || 1 + count * 2 > c.Length)
                throw new InvalidDataException($"{table.Name} row {row}: {count} voices do not fit in {c.Length} columns.");
            var list = new List<(string, string)>(count);
            for (int i = 0; i < count; i++)
            {
                string archive = c[1 + i * 2], file = c[2 + i * 2];
                if (!archive.EndsWith(".pp", StringComparison.OrdinalIgnoreCase) || !file.Contains('.'))
                    throw new InvalidDataException($"{table.Name} row {row}: '{archive}' / '{file}' is not an archive/file pair.");
                list.Add((archive, file));
            }
            lists.Add(list);
        }
        return new VoiceListTable { Lists = lists };
    }
}

/// <summary>
/// lxNN: name groups used by the body (lx01: frames such as o01_P_Bura01/02 or the nails o01_P_TumeH01_R…,
/// lx02: materials such as m01_Bura01, m01_Kami). A header row "rows names" is followed by that many rows
/// holding that many names in total; empty rows end the table.
/// </summary>
public sealed class FrameGroupTable
{
    public IReadOnlyList<IReadOnlyList<string[]>> Groups { get; init; } = [];

    public static FrameGroupTable Read(LstTable table)
    {
        var rows = GameTables.DataRows(table).Select(r => r.Cells.Where(x => x.Length > 0).ToArray()).ToList();
        var groups = new List<IReadOnlyList<string[]>>();
        int i = 0;
        while (i < rows.Count && rows[i].Length > 0)
        {
            var header = rows[i];
            if (header.Length != 2 || !header.All(GameTables.IsNumber))
                throw new InvalidDataException($"{table.Name}: expected a 'rows names' header at data row {i + 1}.");
            int count = (int)GameTables.Number(header[0]), names = (int)GameTables.Number(header[1]);
            var group = rows.Skip(i + 1).Take(count).ToList();
            // Unused name slots are written as "0".
            int actual = group.Sum(g => g.Count(x => x != "0"));
            if (group.Count != count || actual > names)
                throw new InvalidDataException($"{table.Name}: group at row {i + 1} declares {count} rows / {names} names, found {group.Count} / {actual}.");
            groups.Add(group);
            i += 1 + count;
        }
        if (rows.Skip(i).Any(r => r.Length > 0))
            throw new InvalidDataException($"{table.Name}: data after the last group.");
        return new FrameGroupTable { Groups = groups };
    }
}

/// <summary>
/// lyNN: touch areas (o01_A_* collision frames). ly01: one group of areas per row; ly02: an area followed by its
/// neighbours ("$$$" rows separate blocks); ly03: the "_K" areas and small index lists. Rows are kept as name/number lists.
/// </summary>
public sealed class TouchAreaTable
{
    public const string Separator = "$$$";

    public IReadOnlyList<string[]> Rows { get; init; } = [];

    public static TouchAreaTable Read(LstTable table)
    {
        var rows = GameTables.DataRows(table).Select(r => r.Cells.Where(x => x.Length > 0).ToArray()).ToList();
        foreach (var name in rows.SelectMany(r => r).Where(x => !GameTables.IsNumber(x) && x != Separator))
            if (!name.StartsWith("o01_", StringComparison.Ordinal))
                throw new InvalidDataException($"{table.Name}: unexpected cell '{name}'.");
        return new TouchAreaTable { Rows = rows };
    }
}

/// <summary>lz00: screen rectangles: width, height, x, y and two scales (= width/128, height/128).</summary>
public sealed class UiRectTable
{
    public sealed record UiRect(int Row, double Width, double Height, double X, double Y, double ScaleX, double ScaleY);

    public IReadOnlyList<UiRect> Rects { get; init; } = [];

    public static UiRectTable Read(LstTable table)
    {
        var rects = new List<UiRect>();
        foreach (var (row, c) in GameTables.DataRows(table))
        {
            if (c.Length < 6 || !c.Take(6).All(GameTables.IsNumber))
                throw new InvalidDataException($"{table.Name} row {row}: expected 6 numbers.");
            var v = c.Take(6).Select(GameTables.Number).ToArray();
            rects.Add(new UiRect(row, v[0], v[1], v[2], v[3], v[4], v[5]));
        }
        return new UiRectTable { Rects = rects };
    }
}

/// <summary>
/// lg00 (global gameplay parameters: timers in ms such as 120000, rates such as 0.01/2.5…) and lhNN_MM_KK
/// (per-character reaction parameters): rows of plain numbers whose meanings are still unknown.
/// </summary>
public sealed class NumericTable
{
    public IReadOnlyList<double[]> Rows { get; init; } = [];

    public static NumericTable Read(LstTable table)
    {
        var rows = new List<double[]>();
        foreach (var (row, c) in GameTables.DataRows(table))
        {
            var cells = c.Where(x => x.Length > 0).ToArray();
            if (!cells.All(GameTables.IsNumber))
                throw new InvalidDataException($"{table.Name} row {row}: non-numeric cell '{cells.First(x => !GameTables.IsNumber(x))}'.");
            rows.Add([.. cells.Select(GameTables.Number)]);
        }
        return new NumericTable { Rows = rows };
    }
}
