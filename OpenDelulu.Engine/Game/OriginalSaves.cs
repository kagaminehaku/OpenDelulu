using System.Buffers.Binary;
using OpenDelulu.Engine.Storage;
using OpenDelulu.Formats.Save;

namespace OpenDelulu.Engine.Game;

/// <summary>
/// The original game's saves (data/save of every installed edition): read for the album, imported into a ticket and
/// written back from Open Delulu's progress. A ticket N (1..3) is three kinds of files:
/// <list type="bullet">
/// <item>saveN000 (tickets): +0x00 seconds, +0x04 hours, +0x08 minutes, +0x0C milliseconds of play, +0x14 days, +0x18
/// time of day, +0x1C one "clear" byte and +0x30 one level byte per girl (by <see cref="Unlocks.Girls"/>).</item>
/// <item>saveN001 (item list): the same play time, then day / time of day, the album flags (20 bytes per girl id from
/// +0x18), the best unlock rank +0x1CC, the swimsuits +0x3E6 (one byte per lbNN row) and accessories +0xDE0 (128 per lc
/// table), 0 = locked.</item>
/// <item>saveNCC0 (one per girl, CC = 1.. in the order of <see cref="PersonalGirls"/>): +0x04 level, +0x05 love, +0x06
/// completed, +0xEC swimsuit row, +0xF0 the item row worn at each of the 15 accessory places, then statistics, a dated
/// history and her baked skin and face textures.</item>
/// </list>
/// Writing keeps every byte that is not understood: each file starts from the slot's own file; a new slot's ticket and
/// personal data start from another slot's (personal data: the same girl's, for her pictures), its item list from zeros. The save folder is copied once per run before the
/// first write (data/save/OpenDelulu-backup-…).
/// </summary>
public static class OriginalSaves
{
    /// <summary>Girls of the album's "Ecchi" tab, in slot order (each one's H picture, st03_03_GG_05).</summary>
    public static readonly int[] HPictureGirls = [1, 3, 4, 5, 6, 2, 11, 12];

    /// <summary>Girl id of each personal save saveNCC0, CC = index + 1 (matched by the faces stored in them).</summary>
    public static readonly int[] PersonalGirls = [1, 3, 4, 5, 6, 2, 11];

    static bool _backedUp;

    /// <summary>Tests: the save folder to use instead of the game's.</summary>
    public static string? FolderOverride { get; set; }

    /// <summary>
    /// Album pictures unlocked in any item list save (saveN001.sav, every slot and edition): girl → picture numbers,
    /// 1..4 for her own pictures and 5 for her H picture. Unreadable files are skipped.
    /// </summary>
    public static Dictionary<int, HashSet<int>> AlbumPictures(GameInstall install)
    {
        var pictures = new Dictionary<int, HashSet<int>>();
        foreach (var save in ItemLists(install))
            foreach (var (girl, set) in Pictures(save))
            {
                if (!pictures.TryGetValue(girl, out var all))
                    pictures[girl] = all = [];
                all.UnionWith(set);
            }
        return pictures;
    }

    static Dictionary<int, HashSet<int>> Pictures(SaveFile save)
    {
        var pictures = new Dictionary<int, HashSet<int>>();
        void Add(int girl, int picture)
        {
            if (!pictures.TryGetValue(girl, out var set))
                pictures[girl] = set = [];
            set.Add(picture);
        }
        for (int girl = 1; girl <= 12; girl++)
            for (int picture = 1; picture <= 4; picture++)
                if (save.GirlFlag(girl, picture))
                    Add(girl, picture);
        for (int slot = 0; slot < HPictureGirls.Length; slot++)
            if (save.GirlFlag(12, slot + 1))
                Add(HPictureGirls[slot], 5);
        return pictures;
    }

    static IEnumerable<SaveFile> ItemLists(GameInstall install)
    {
        foreach (var edition in install.Editions)
        {
            string dir = $"{edition.DataFolder}/save";
            if (!install.Storage.DirectoryExists(dir))
                continue;
            foreach (var name in install.Storage.EnumerateFiles(dir, "save*.sav"))
                if (SaveFile.DetectKind(name) == SaveKind.ItemList && TryRead(install.Storage, $"{dir}/{name}") is { } save)
                    yield return save;
        }
    }

    static SaveFile? TryRead(IGameStorage storage, string path)
    {
        try
        {
            if (!storage.FileExists(path))
                return null;
            using var stream = storage.OpenRead(path);
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            return SaveFile.Read(data, path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ the folder written

    /// <summary>
    /// The data/save folder Open Delulu reads tickets from and writes them to: the Plus edition's when installed (its exe
    /// is the one with every girl), else the base game's. Null when the game is not in a plain folder.
    /// </summary>
    public static string? SaveFolder(GameInstall install)
    {
        if (FolderOverride is { } folderOverride)
            return folderOverride;
        if (install.Storage is not FolderGameStorage folder)
            return null;
        var edition = install.Editions.FirstOrDefault(e => e.Edition == GameEdition.Plus)
            ?? install.Editions.FirstOrDefault(e => e.Edition == GameEdition.Base);
        return edition is null ? null : Path.Combine(folder.RootPath, edition.DataFolder.Replace('/', Path.DirectorySeparatorChar), "save");
    }

    static string FileName(int slot, int part) => $"save{slot}{part:000}.sav";

    static SaveFile? ReadFile(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        try
        {
            return File.Exists(path) ? SaveFile.Read(File.ReadAllBytes(path), path) : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Whether ticket <paramref name="slot"/> (1..3) has an original save.</summary>
    public static bool Exists(GameInstall install, int slot) =>
        SaveFolder(install) is { } folder && File.Exists(Path.Combine(folder, FileName(slot, 0)));

    // ------------------------------------------------------------------ import

    /// <summary>Open Delulu progress from ticket <paramref name="slot"/> of the original saves, or null.</summary>
    public static GameProgress? Import(GameInstall install, int slot)
    {
        if (SaveFolder(install) is not { } folder || ReadFile(folder, FileName(slot, 0)) is not { Kind: SaveKind.Tickets } ticket)
            return null;
        var progress = new GameProgress { OriginalSlot = slot };
        int seconds = ticket["+0x00"]!.Int32, hours = ticket["+0x04"]!.Int32, minutes = ticket["+0x08"]!.Int32;
        progress.PlaySeconds = hours * 3600.0 + minutes * 60 + seconds + ticket["+0x0C"]!.Int32 / 1000.0;
        progress.Day = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(ticket["+0x14"]!.Data));
        progress.TimeOfDay = Math.Clamp(ticket["+0x18"]!.Int32, 1, 5);
        var clear = ticket["+0x1C"]!.Data;
        var levels = ticket["+0x30"]!.Data;
        for (int i = 0; i < Unlocks.Girls.Length; i++)
        {
            if (levels[i] == 0)
                continue;
            var girl = progress.For(Unlocks.Girls[i]);
            girl.Intimacy = Math.Clamp((int)levels[i], 1, 5);
            girl.Completed = clear[i] != 0;
            girl.Met = true;
        }

        if (ReadFile(folder, FileName(slot, 1)) is { Kind: SaveKind.ItemList } items)
        {
            progress.Rank = items["+0x1CC"]!.Data[0];
            var suits = items["+0x3E6"]!.Data;
            progress.Swimsuits = [];
            for (int row = 1; row < suits.Length; row++)
                if (suits[row] != 0)
                    progress.Swimsuits[row] = suits[row];
            var accessories = items["+0xDE0"]!.Data;
            progress.Accessories = [];
            for (int key = 0; key < accessories.Length; key++)
                if (accessories[key] != 0)
                    progress.Accessories[key] = accessories[key];
            foreach (var (id, pictures) in Pictures(items))
                progress.For(id).AlbumPictures = [.. pictures.Order()];
        }

        for (int cc = 1; cc <= PersonalGirls.Length; cc++)
        {
            if (ReadFile(folder, FileName(slot, cc * 10)) is not { Kind: SaveKind.Personal } personal)
                continue;
            var girl = progress.For(PersonalGirls[cc - 1]);
            girl.Intimacy = Math.Clamp((int)personal["+0x04"]!.Data[0], 1, 5);
            girl.Love = personal["+0x05"]!.Data[0];
            girl.Completed = personal["+0x06"]!.Data[0] != 0;
            if (personal["+0xEC"]!.Int32 is int suit and > 0)
                girl.SwimsuitRow = suit;
            var worn = personal["+0xF0"]!.Data;
            girl.Look.Accessories.Clear();
            for (int place = 0; place < 15; place++)
                if (BinaryPrimitives.ReadInt32LittleEndian(worn.AsSpan(place * 4)) is int row and > 0)
                    girl.Look.Accessories[place + 1] = row;
        }
        return progress;
    }

    // ------------------------------------------------------------------ export

    /// <summary>
    /// Writes <paramref name="progress"/> into ticket <paramref name="slot"/> (1..3) of the original saves. Returns the
    /// files written; throws <see cref="IOException"/> when the folder cannot be written.
    /// </summary>
    public static List<string> Export(GameInstall install, int slot, GameProgress progress)
    {
        var written = new List<string>();
        if (SaveFolder(install) is not { } folder)
            return written;
        Directory.CreateDirectory(folder);
        Backup(folder);
        bool plus = install.Has(GameEdition.Plus);

        // Play time and calendar, shared by the ticket and the item list.
        long ms = (long)Math.Round(progress.PlaySeconds * 1000);
        int hours = (int)Math.Min(999, ms / 3_600_000), minutes = (int)(ms / 60_000 % 60), seconds = (int)(ms / 1000 % 60), millis = (int)(ms % 1000);

        var ticket = ReadFile(folder, FileName(slot, 0)) ?? Template(folder, 0) ?? SaveFile.Empty(SaveKind.Tickets, plus);
        ticket.Set("+0x00", seconds);
        ticket.Set("+0x04", hours);
        ticket.Set("+0x08", minutes);
        ticket.Set("+0x0C", millis);
        ticket.Set("+0x14", progress.Day);
        ticket.Set("+0x18", progress.TimeOfDay);
        var clear = new byte[20];
        var levels = new byte[20];
        for (int i = 0; i < Unlocks.Girls.Length; i++)
            if (progress.Girls.TryGetValue(Unlocks.Girls[i], out var girl) && (girl.Met || girl.Intimacy > 1 || girl.Love > 0))
            {
                levels[i] = (byte)girl.Intimacy;
                clear[i] = (byte)(girl.Completed ? 1 : 0);
            }
        ticket.Set("+0x1C", clear);
        ticket.Set("+0x30", levels);
        Write(folder, FileName(slot, 0), ticket, written);

        // A new slot's item list starts empty (another slot's would bring its album and unlocks along).
        var items = ReadFile(folder, FileName(slot, 1)) ?? NewItemList(plus);
        // B+…: the play time again; the "A" fields by position (their offsets differ between editions).
        items.Set("B+0x00", seconds);
        items.Set("B+0x04", hours);
        items.Set("B+0x08", minutes);
        items.Set("B+0x0C", millis);
        items.Set(items.Fields[5].Name, progress.Day);
        items.Set(items.Fields[6].Name, progress.TimeOfDay);
        items.Set("+0x1CC", Math.Clamp(progress.Rank, 0, 15));
        if (progress.Swimsuits is { } suits)
        {
            var bytes = new byte[256];
            foreach (var (row, state) in suits)
                if (row is > 0 and < 256)
                    bytes[row] = (byte)Math.Clamp(state, 1, 3);
            items.Set("+0x3E6", bytes);
        }
        if (progress.Accessories is { } accessories)
        {
            var bytes = new byte[1408];
            foreach (var (key, state) in accessories)
                if (key is >= 0 and < 1408)
                    bytes[key] = (byte)Math.Clamp(state, 1, 3);
            items.Set("+0xDE0", bytes);
        }
        var flags = (byte[])items["+0x004"]!.Data.Clone();
        foreach (var (id, girl) in progress.Girls)
            foreach (int picture in girl.AlbumPictures)
            {
                int at = picture == 5 && Array.IndexOf(HPictureGirls, id) is var h and >= 0
                    ? 0x18 + 20 * 11 + h + 1 - 0x04
                    : picture is >= 1 and <= 4 && id is >= 1 and <= 12 ? 0x18 + 20 * (id - 1) + picture - 0x04 : -1;
                if (at >= 0 && at < flags.Length)
                    flags[at] = 1;
            }
        items.Set("+0x004", flags);
        Write(folder, FileName(slot, 1), items, written);

        for (int cc = 1; cc <= PersonalGirls.Length; cc++)
        {
            int id = PersonalGirls[cc - 1];
            if (!progress.Girls.TryGetValue(id, out var girl))
                continue;
            var personal = ReadFile(folder, FileName(slot, cc * 10)) ?? PersonalTemplate(folder, cc);
            if (personal is null)
                continue; // no picture data to start from; the exe makes it when she is first picked
            personal.Set("+0x04", girl.Intimacy);
            personal.Set("+0x05", girl.Love);
            personal.Set("+0x06", girl.Completed ? 1 : 0);
            if (girl.SwimsuitRow is int suit)
                personal.Set("+0xEC", suit);
            var worn = new byte[60];
            foreach (var (place, row) in girl.Look.Accessories)
                if (place is >= 1 and <= 15)
                    BinaryPrimitives.WriteInt32LittleEndian(worn.AsSpan((place - 1) * 4), row);
            personal.Set("+0xF0", worn);
            Write(folder, FileName(slot, cc * 10), personal, written);
        }
        return written;
    }

    /// <summary>Deletes the files of ticket <paramref name="slot"/> (after the run's backup of the folder).</summary>
    public static void Delete(GameInstall install, int slot)
    {
        if (SaveFolder(install) is not { } folder || !Directory.Exists(folder))
            return;
        Backup(folder);
        foreach (var file in Directory.GetFiles(folder, $"save{slot}*.sav"))
            File.Delete(file);
    }

    /// <summary>An item list for a slot without one: everything locked, the flags the exe sets for a game set.</summary>
    static SaveFile NewItemList(bool plus)
    {
        var items = SaveFile.Empty(SaveKind.ItemList, plus);
        items.Set(items.Fields[0].Name, 1);
        items.Set(items.Fields[7].Name, 1);
        items.Set(items.Fields[8].Name, Enumerable.Repeat((byte)1, 7).ToArray());
        items.Set(items.Fields[9].Name, Enumerable.Repeat((byte)1, 45).ToArray());
        items.Set(plus ? "+0x13AC" : "+0x13AA", Enumerable.Repeat((byte)1, 7).ToArray());
        items.Set(plus ? "+0x13B8" : "+0x13B6", 0x01010101);
        return items;
    }

    /// <summary>Another slot's file of the same part, the starting point for a new ticket's fields not understood.</summary>
    static SaveFile? Template(string folder, int part)
    {
        for (int slot = 1; slot <= 9; slot++)
            if (ReadFile(folder, FileName(slot, part)) is { } save)
                return save;
        return null;
    }

    /// <summary>The same girl's personal save of another slot (it carries her skin and face pictures).</summary>
    static SaveFile? PersonalTemplate(string folder, int cc) => Template(folder, cc * 10);

    static void Write(string folder, string name, SaveFile save, List<string> written)
    {
        string path = Path.Combine(folder, name), temp = path + ".tmp";
        File.WriteAllBytes(temp, save.ToBytes());
        File.Move(temp, path, overwrite: true);
        written.Add(path);
    }

    /// <summary>Copies the save folder's files once per run before anything is overwritten.</summary>
    static void Backup(string folder)
    {
        if (_backedUp)
            return;
        var files = Directory.GetFiles(folder, "save*.sav");
        if (files.Length > 0)
        {
            string backup = Path.Combine(folder, $"OpenDelulu-backup-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(backup);
            foreach (var file in files)
                File.Copy(file, Path.Combine(backup, Path.GetFileName(file)), overwrite: false);
        }
        _backedUp = true;
    }
}
