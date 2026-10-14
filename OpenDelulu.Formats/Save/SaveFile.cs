using System.Buffers.Binary;
using System.Text.RegularExpressions;
using OpenDelulu.Formats.Imaging;

namespace OpenDelulu.Formats.Save;

/// <summary>The four kinds of files in data/save (names from the exe's source file / error strings).</summary>
public enum SaveKind
{
    Unknown,
    /// <summary>save0000.sav, sb_ConfigData.cpp (コンフィグデータ).</summary>
    Config,
    /// <summary>saveN000.sav, sb_ItemListData.cpp (チケットデータ).</summary>
    Tickets,
    /// <summary>saveN001.sav, sb_ItemListData.cpp (アイテムリストデータ).</summary>
    ItemList,
    /// <summary>saveNCC0.sav, sb_PersonalData.cpp (キャラデータ): an edited character, one per slot CC.</summary>
    Personal,
}

/// <summary>
/// One field as the game writes it: <see cref="Name"/> is the member offset in the game's in-memory object
/// (e.g. "+0x14B"), which is what the exe's save code uses; the meaning of most fields is still unknown.
/// </summary>
public sealed record SaveField(string Name, byte[] Data)
{
    public int Int32 => BinaryPrimitives.ReadInt32LittleEndian(Data);
    public float Single => BitConverter.Int32BitsToSingle(Int32);

    public override string ToString() => Data.Length switch
    {
        1 => $"{Name}={Data[0]}",
        2 => $"{Name}={BinaryPrimitives.ReadUInt16LittleEndian(Data)}",
        4 => $"{Name}={Int32}",
        _ => $"{Name}=[{Data.Length} bytes]",
    };
}

/// <summary>A dated entry of a personal-data history (12 per character).</summary>
public sealed record SaveHistoryEntry(DateTime? Time, ushort[] Words, byte[] Data);

/// <summary>
/// Parser for the files in data/save. The layouts were read from the save routines of SexyBeach3Original.exe
/// and SexyBeach3PlusOriginal.exe (unoptimized MSVC code: one WriteFile per member). Verified on every save of
/// both editions. Integers are little-endian; nothing is encrypted except the personal-data images (bitwise NOT).
/// </summary>
public sealed class SaveFile
{
    public SaveKind Kind { get; init; }
    /// <summary>True if the file has the Bonus Disc (Plus) layout.</summary>
    public bool Plus { get; init; }
    public IReadOnlyList<SaveField> Fields { get; init; } = [];

    // Personal data only.
    public IReadOnlyList<SaveHistoryEntry> History { get; init; } = [];
    /// <summary>
    /// The character's generated skin textures, 256x256 24-bit BMP files ("BM" zeroed like elsewhere in the game):
    /// [0] body skin (replaces body.bmp), [1] face (replaces face.bmp), with skin tone and tan lines baked in.
    /// </summary>
    public IReadOnlyList<byte[]> Pictures { get; init; } = [];
    /// <summary>Plus only: 13 x (68 + 272) bytes appended after the pictures.</summary>
    public IReadOnlyList<SaveField> PlusExtension { get; init; } = [];

    public SaveField? this[string name] => Fields.FirstOrDefault(f => f.Name == name);

    public static SaveKind DetectKind(string fileName)
    {
        var m = Regex.Match(Path.GetFileName(fileName), @"^save(\d)(\d\d)(\d)\.sav$", RegexOptions.IgnoreCase);
        if (!m.Success)
            return SaveKind.Unknown;
        if (fileName.Contains("save0000", StringComparison.OrdinalIgnoreCase))
            return SaveKind.Config;
        return (m.Groups[2].Value, m.Groups[3].Value) switch
        {
            ("00", "0") => SaveKind.Tickets,
            ("00", "1") => SaveKind.ItemList,
            (_, "0") => SaveKind.Personal,
            _ => SaveKind.Unknown,
        };
    }

    public static SaveFile Read(byte[] data, string fileName) => DetectKind(fileName) switch
    {
        SaveKind.Config => ReadFixed(SaveKind.Config, data, data.Length == 66 ? ConfigPlus : ConfigBase),
        SaveKind.Tickets => ReadFixed(SaveKind.Tickets, data, Tickets),
        SaveKind.ItemList => ReadFixed(SaveKind.ItemList, data, data.Length == 2226 ? ItemListPlus : ItemListBase),
        SaveKind.Personal => ReadPersonal(data),
        _ => throw new InvalidDataException($"{fileName}: not a Sexy Beach 3 save file name."),
    };

    // ------------------------------------------------------------------ layouts (member offset, size)

    static readonly (string, int)[] ConfigCommon =
    [
        ("+0x08", 16), ("+0x18", 4), ("+0x1C", 16), ("+0x2C", 4),
        ("+0x30", 1), ("+0x31", 1), ("+0x32", 1), ("+0x33", 1), ("+0x34", 1), ("+0x35", 1), ("+0x36", 1),
    ];

    static readonly (string, int)[] ConfigBase =
        [.. ConfigCommon, ("+0x38", 4), ("+0x40", 1), ("+0x44", 4), ("+0x50", 4), ("+0x58", 1)];

    static readonly (string, int)[] ConfigPlus =
        [.. ConfigCommon, ("+0x3C", 4), ("+0x50", 1), ("+0x54", 4), ("+0x60", 4), ("+0x44", 1), ("+0x37", 1), ("+0x48", 4)];

    /// <summary>The first four ints and the uint16 are the same in tickets and item list (7, 43, 59, 130, 61).</summary>
    static readonly (string, int)[] Tickets =
        [("+0x00", 4), ("+0x04", 4), ("+0x08", 4), ("+0x0C", 4), ("+0x14", 2), ("+0x18", 4), ("+0x1C", 20), ("+0x30", 20)];

    static readonly (string, int)[] ItemListBase =
    [
        ("A+0x14B", 1), ("B+0x00", 4), ("B+0x04", 4), ("B+0x08", 4), ("B+0x0C", 4),
        ("A+0x15C", 2), ("A+0x160", 4), ("A+0x164", 4), ("A+0x192", 7), ("A+0x19A", 45),
        ("+0x004", 400), ("+0x1CC", 1), ("+0x3E6", 256), ("+0xDE0", 1408), ("+0x1360", 70), ("+0x13AA", 7), ("+0x13B6", 4),
    ];

    static readonly (string, int)[] ItemListPlus =
    [
        ("A+0xB8F", 1), ("B+0x00", 4), ("B+0x04", 4), ("B+0x08", 4), ("B+0x0C", 4),
        ("A+0xBA0", 2), ("A+0xBA4", 4), ("A+0xBA8", 4), ("A+0xBD6", 7), ("A+0xBDE", 45),
        ("+0x004", 400), ("+0x1CC", 1), ("+0x3E6", 256), ("+0xDE0", 1408), ("+0x1360", 70), ("+0x13AC", 7), ("+0x13B8", 4),
        ("+0x13A6", 1),
    ];

    /// <summary>Personal data header: 3 bytes, a 212-byte block written member by member, then 204 bytes.</summary>
    static readonly (string, int)[] PersonalHeader =
    [
        ("+0x04", 1), ("+0x05", 1), ("+0x06", 1),
        ("S+0x00", 4), ("S+0x04", 4), ("S+0x08", 4), ("S+0x0C", 4), ("S+0x10", 20),
        ("S+0x24", 4), ("S+0x28", 4), ("S+0x2C", 4), ("S+0x30", 4), ("S+0x34", 20), ("S+0x48", 12),
        ("S+0x54", 4), ("S+0x58", 4), ("S+0x5C", 4), ("S+0x60", 4), ("S+0x64", 20), ("S+0x78", 12),
        ("S+0x84", 4), ("S+0x88", 4), ("S+0x8C", 4), ("S+0x90", 4), ("S+0x94", 4), ("S+0x98", 4), ("S+0x9C", 4), ("S+0xA0", 4),
        ("S+0xA4", 4), ("S+0xA8", 4), ("S+0xAC", 4), ("S+0xB0", 4), ("S+0xB4", 20), ("S+0xC8", 4), ("S+0xCC", 4), ("S+0xD0", 4),
        ("+0xE4", 4), ("+0xEC", 4), ("+0xF0", 60), ("+0x12C", 68), ("+0x170", 68),
    ];

    const int HistoryEntries = 12, HistoryEntrySize = 0xE4;

    /// <summary>Filler written between the parts of each picture (contents derived from a seed, ignored on load).</summary>
    const int PadAfterInfo = 3, PadAfterFileHeader = 0x0E, PadAfterBitmapHeader = 5, PadAfterPixels = 0x19D6;
    static readonly int[] PadsBeforePixels = [0x160A, 0x2618, 0x68C];

    const int PlusEntries = 13, PlusEntryA = 0x44, PlusEntryB = 0x110;

    // ------------------------------------------------------------------ readers

    static SaveFile ReadFixed(SaveKind kind, byte[] data, (string Name, int Size)[] layout)
    {
        int expected = layout.Sum(f => f.Size);
        if (data.Length != expected)
            throw new InvalidDataException($"{kind} save: expected {expected} bytes, got {data.Length}.");
        int at = 0;
        return new SaveFile
        {
            Kind = kind,
            Plus = layout == ConfigPlus || layout == ItemListPlus,
            Fields = Take(data, ref at, layout),
        };
    }

    static List<SaveField> Take(byte[] data, ref int at, (string Name, int Size)[] layout)
    {
        var fields = new List<SaveField>(layout.Length);
        foreach (var (name, size) in layout)
        {
            fields.Add(new SaveField(name, data.AsSpan(at, size).ToArray()));
            at += size;
        }
        return fields;
    }

    static SaveFile ReadPersonal(byte[] data)
    {
        int at = 0;
        var fields = Take(data, ref at, PersonalHeader);

        var history = new List<SaveHistoryEntry>(HistoryEntries);
        for (int i = 0; i < HistoryEntries; i++)
        {
            var entry = data.AsSpan(at, HistoryEntrySize);
            var words = new ushort[10];
            for (int w = 0; w < words.Length; w++)
                words[w] = BinaryPrimitives.ReadUInt16LittleEndian(entry[(w * 2)..]);
            history.Add(new SaveHistoryEntry(ToTime(words), words, entry[20..].ToArray()));
            at += HistoryEntrySize;
        }

        var pictures = new List<byte[]>(2);
        for (int i = 0; i < 2; i++)
        {
            // int size, pad, BITMAPFILEHEADER (14), pad, BITMAPINFOHEADER + 4 pixel bytes (44), pad,
            // int pixel size, pads, pixels, pad. The int and the bitmap parts are bitwise NOT.
            Need(data, at, 4 + PadAfterInfo + 14 + PadAfterFileHeader + 44 + PadAfterBitmapHeader + 4);
            at += 4 + PadAfterInfo;
            var fileHeader = Not(data.AsSpan(at, 14)); at += 14 + PadAfterFileHeader;
            var infoHeader = Not(data.AsSpan(at, 44)); at += 44 + PadAfterBitmapHeader;
            int pixelSize = BinaryPrimitives.ReadInt32LittleEndian(Not(data.AsSpan(at, 4))); at += 4;
            at += PadsBeforePixels.Sum();
            if (pixelSize is < 0 or > 16 << 20)
                throw new InvalidDataException($"Personal save: implausible picture size {pixelSize}.");
            Need(data, at, pixelSize + PadAfterPixels);
            var pixels = Not(data.AsSpan(at, pixelSize)); at += pixelSize + PadAfterPixels;

            var bmp = new byte[14 + 40 + pixelSize];
            fileHeader.CopyTo(bmp, 0);
            infoHeader.AsSpan(0, 40).CopyTo(bmp.AsSpan(14));
            pixels.CopyTo(bmp, 54);
            pictures.Add(bmp);
        }

        var extension = new List<SaveField>();
        bool plus = data.Length - at == PlusEntries * (PlusEntryA + PlusEntryB);
        if (plus)
        {
            for (int i = 0; i < PlusEntries; i++)
            {
                extension.Add(new SaveField($"{i}A", data.AsSpan(at, PlusEntryA).ToArray())); at += PlusEntryA;
                extension.Add(new SaveField($"{i}B", data.AsSpan(at, PlusEntryB).ToArray())); at += PlusEntryB;
            }
        }
        if (at != data.Length)
            throw new InvalidDataException($"Personal save: {data.Length - at} unparsed bytes.");

        return new SaveFile { Kind = SaveKind.Personal, Plus = plus, Fields = fields, History = history, Pictures = pictures, PlusExtension = extension };
    }

    /// <summary>Decodes one of <see cref="Pictures"/>.</summary>
    public RgbaImage DecodePicture(int index) => BmpDecoder.Decode(Pictures[index]);

    static DateTime? ToTime(ushort[] w)
    {
        // year, month, day, hour, minute, second, then three more words (unknown).
        if (w[0] is < 2000 or > 2100 || w[1] is < 1 or > 12 || w[2] is < 1 or > 31 || w[3] > 23 || w[4] > 59 || w[5] > 59)
            return null;
        return new DateTime(w[0], w[1], Math.Min(w[2], DateTime.DaysInMonth(w[0], w[1])), w[3], w[4], w[5]);
    }

    static byte[] Not(ReadOnlySpan<byte> source)
    {
        var result = new byte[source.Length];
        for (int i = 0; i < source.Length; i++)
            result[i] = (byte)~source[i];
        return result;
    }

    static void Need(byte[] data, int at, int count)
    {
        if (at + count > data.Length)
            throw new InvalidDataException("Personal save: file is truncated.");
    }
}
