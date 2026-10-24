using System.Globalization;
using OpenDelulu.Formats.Lst;

namespace OpenDelulu.Engine.Characters;

/// <summary>
/// One of the 15 accessory places (lc00 rows 1..15): head, face, ears, neck, back, waist, pareo, wrists, fingers,
/// ankles and shoes. Places on both sides share one item table.
/// </summary>
/// <param name="Table">lcNN table number (1..11).</param>
/// <param name="BodyFrame">Frame of the character the item hangs on (body, face or hair); null = given per item
/// (head, face) or none (pareo texture, shoes skinned to the body bones).</param>
/// <param name="ItemFrame">Root frame of the item model.</param>
public sealed record AccessorySlot(int Index, int Table, string? BodyFrame, string? ItemFrame)
{
    /// <summary>The pareo: items are textures for the body's own pareo meshes, not models.</summary>
    public bool IsPareo => Table == 7;

    /// <summary>Shoes: the item is skinned to the leg bones and has no frame to hang on.</summary>
    public bool IsShoes => Table == 11;
}

/// <summary>
/// An item of an lcNN table. Rows come in groups of five (row = 5 × group + variant + 1): the first row names the item;
/// rows without a name (icon 255) are its colour variants, and some groups hold several named items.
/// </summary>
/// <param name="Model">The .xx (or, for the pareo, the .tga texture).</param>
/// <param name="IconNumber">Card number in the st02 accessory icon sheet; 255 = none (a variant).</param>
/// <param name="Plus">Column 7: 1 for the Plus expansion's items.</param>
/// <param name="BodyFrame">Head and face items name their frame (o01_N_Atama02_AC…) themselves.</param>
/// <param name="RightIcon">Paired places (icon file "st02_55_01_%02d_00"): the right side's card is the next number.</param>
public sealed record AccessoryItem(int Table, int Row, string Name, AssetRef Model, int IconNumber, AssetRef? Icon, bool Plus,
    string? BodyFrame, string? ItemFrame, AssetRef? RightIcon = null)
{
    public int Group => (Row - 1) / 5;
    public int Variant => (Row - 1) % 5;
}

/// <summary>The accessory tables lc00..lc11.</summary>
public sealed class AccessoryDatabase
{
    readonly Dictionary<int, IReadOnlyList<AccessoryItem>> _items;

    AccessoryDatabase(IReadOnlyList<AccessorySlot> slots, Dictionary<int, IReadOnlyList<AccessoryItem>> items)
    {
        Slots = slots;
        _items = items;
    }

    public IReadOnlyList<AccessorySlot> Slots { get; }

    /// <summary>Items of an lcNN table (ordered by row; missing files left out).</summary>
    public IReadOnlyList<AccessoryItem> Items(int table) => _items.GetValueOrDefault(table, []);

    public AccessorySlot? Slot(int index) => Slots.FirstOrDefault(s => s.Index == index);

    public AccessoryItem? Find(int table, int row) => Items(table).FirstOrDefault(i => i.Row == row);

    public static AccessoryDatabase Load(GameFileSystem fs)
    {
        string archiveName = CharacterDatabase.TableArchives.FirstOrDefault(fs.HasArchive)
            ?? throw new FileNotFoundException("No table archive is installed.");
        var archive = fs.GetArchive(archiveName);
        LstTable? Read(string name) => archive.Find(name) is { } entry ? LstTable.Decode(archive.ReadAllBytes(entry), entry.Name) : null;

        var slots = new List<AccessorySlot>();
        var items = new Dictionary<int, IReadOnlyList<AccessoryItem>>();
        foreach (var row in Read("lc00_00_00_00_00.lst")?.Rows ?? [])
        {
            if (!int.TryParse(row[0], out int index) || index < 1 || row.Length < 5)
                continue;
            string table = row[1].Trim();
            if (table.Length < 4 || !int.TryParse(table.AsSpan(2, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                continue;
            slots.Add(new AccessorySlot(index, number, Frame(row, 3), Frame(row, 4)));
            if (items.ContainsKey(number) || Read(table) is not { } list)
                continue;
            var loaded = new List<AccessoryItem>();
            foreach (var r in list.Rows)
            {
                if (!int.TryParse(r[0], out int rowNumber) || rowNumber < 1 || AssetRef.FromRow(r, 2) is not { } model
                    || !fs.ExistsReferenced(model.Archive, model.File))
                    continue;
                int icon = int.TryParse(Cell(r, 4), out int i) ? i : 255;
                var template = icon != 255 ? AssetRef.FromRow(r, 5) : null;
                AssetRef? IconFile(int n) => template is { } a ? a with { File = a.File.Replace("%02d", n.ToString("00", CultureInfo.InvariantCulture)) } : null;
                var iconAsset = IconFile(icon);
                var rightIcon = template?.File.Contains("%02d") == true ? IconFile(icon + 1) : null;
                string name = Cell(r, 1);
                loaded.Add(new AccessoryItem(number, rowNumber, name == "0" ? "" : name, model, icon, iconAsset, Cell(r, 7) == "1",
                    Frame(r, 8), Frame(r, 9), rightIcon));
            }
            items[number] = loaded;
        }
        return new AccessoryDatabase(slots, items);
    }

    static string Cell(string[] row, int column) => column < row.Length ? row[column].Trim() : "";

    static string? Frame(string[] row, int column) => Cell(row, column) is { Length: > 1 } name ? name : null;
}
