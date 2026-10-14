using OpenDelulu.Engine.Storage;

namespace OpenDelulu.Engine;

/// <summary>The three products of the SB3 family. Each later one requires the earlier ones.</summary>
public enum GameEdition
{
    /// <summary>Sexy Beach 3 (sb3_*.pp).</summary>
    Base,
    /// <summary>Bonus Disc / "Plus" (sb3x_*.pp).</summary>
    Plus,
    /// <summary>Gravure add-on (sb3f_/sb3g_*.pp).</summary>
    Gravure,
}

/// <summary>An edition found on disk: its folder and the folder holding its .pp archives.</summary>
public sealed record EditionFolder(GameEdition Edition, string Folder, string DataFolder);

/// <summary>
/// Locates the installed editions. Accepts either the folder that contains the edition
/// folders (e.g. <c>D:\Sexy Beach 3</c>) or a single edition folder.
/// </summary>
public sealed class GameInstall
{
    static readonly (GameEdition Edition, string Folder)[] KnownFolders =
    [
        (GameEdition.Base, "SexyBeach3"),
        (GameEdition.Plus, "SexyBeach3Plus"),
        (GameEdition.Gravure, "SexyBeach3PlusG"),
    ];

    public IGameStorage Storage { get; }
    public IReadOnlyList<EditionFolder> Editions { get; }

    GameInstall(IGameStorage storage, IReadOnlyList<EditionFolder> editions)
    {
        Storage = storage;
        Editions = editions;
    }

    public bool Has(GameEdition edition) => Editions.Any(e => e.Edition == edition);

    public static GameInstall Detect(IGameStorage storage)
    {
        var found = new List<EditionFolder>();
        var subfolders = storage.EnumerateDirectories("").ToList();

        foreach (var (edition, name) in KnownFolders)
        {
            var actual = subfolders.FirstOrDefault(d => d.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (actual is not null && storage.DirectoryExists($"{actual}/data"))
                found.Add(new EditionFolder(edition, actual, $"{actual}/data"));
        }

        // The user picked a single edition folder (or a folder with just "data").
        if (found.Count == 0 && storage.DirectoryExists("data"))
            found.Add(new EditionFolder(GuessEdition(storage), "", "data"));

        if (found.Count == 0)
            throw new DirectoryNotFoundException(
                $"No Sexy Beach 3 data found in {storage.Description}. Pick the folder that contains SexyBeach3 (and optionally SexyBeach3Plus / SexyBeach3PlusG).");

        return new GameInstall(storage, found);
    }

    public static GameInstall Detect(string folder) => Detect(new FolderGameStorage(folder));

    static GameEdition GuessEdition(IGameStorage storage)
    {
        var archives = storage.EnumerateFiles("data", "*.pp").ToList();
        if (archives.Any(a => a.StartsWith("sb3g_", StringComparison.OrdinalIgnoreCase)))
            return GameEdition.Gravure;
        if (archives.Any(a => a.StartsWith("sb3x_", StringComparison.OrdinalIgnoreCase)))
            return GameEdition.Plus;
        return GameEdition.Base;
    }
}
