using OpenDelulu.Formats.Save;

namespace OpenDelulu.Engine.Game;

/// <summary>Read-only use of the original game's saves (data/save of every installed edition).</summary>
public static class OriginalSaves
{
    /// <summary>Girls of the album's "Ecchi" tab, in slot order (each one's H picture, st03_03_GG_05).</summary>
    public static readonly int[] HPictureGirls = [1, 3, 4, 5, 6, 2, 11, 12];

    /// <summary>
    /// Album pictures unlocked in any item list save (saveN001.sav, every slot and edition): girl → picture numbers,
    /// 1..4 for her own pictures and 5 for her H picture. Unreadable files are skipped.
    /// </summary>
    public static Dictionary<int, HashSet<int>> AlbumPictures(GameInstall install)
    {
        var pictures = new Dictionary<int, HashSet<int>>();
        void Add(int girl, int picture)
        {
            if (!pictures.TryGetValue(girl, out var set))
                pictures[girl] = set = [];
            set.Add(picture);
        }
        foreach (var save in ItemLists(install))
        {
            for (int girl = 1; girl <= 12; girl++)
                for (int picture = 1; picture <= 4; picture++)
                    if (save.GirlFlag(girl, picture))
                        Add(girl, picture);
            for (int slot = 0; slot < HPictureGirls.Length; slot++)
                if (save.GirlFlag(12, slot + 1))
                    Add(HPictureGirls[slot], 5);
        }
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
            {
                if (SaveFile.DetectKind(name) != SaveKind.ItemList)
                    continue;
                SaveFile? save = null;
                try
                {
                    using var stream = install.Storage.OpenRead($"{dir}/{name}");
                    var data = new byte[stream.Length];
                    stream.ReadExactly(data);
                    save = SaveFile.Read(data, name);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                {
                }
                if (save is not null)
                    yield return save;
            }
        }
    }
}
