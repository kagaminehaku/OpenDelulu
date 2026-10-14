using System.Collections.Concurrent;
using OpenDelulu.Formats.Lst;
using OpenDelulu.Formats.Pp;
using OpenDelulu.Formats.Xx;

namespace OpenDelulu.Engine;

/// <summary>An archive known to the file system (not necessarily opened yet).</summary>
public sealed record ArchiveInfo(string Name, GameEdition Edition, string Path, long Size);

/// <summary>
/// Virtual file system over every .pp archive of every installed edition.
/// The game's tables address assets as (archive name, file name), so lookups are by archive name;
/// when two editions ship the same archive name, the later edition wins (it is the newer copy).
/// </summary>
public sealed class GameFileSystem : IDisposable
{
    readonly Dictionary<string, ArchiveInfo> _archives = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, Lazy<PpArchive>> _open = new(StringComparer.OrdinalIgnoreCase);

    public GameInstall Install { get; }

    /// <summary>Archives in edition order, then by name.</summary>
    public IReadOnlyList<ArchiveInfo> Archives { get; }

    /// <summary>Files named .pp that are not SB3 archives (e.g. sb3x_0111.pp is really an MPEG movie).</summary>
    public IReadOnlyList<ArchiveInfo> SkippedFiles { get; }

    public GameFileSystem(GameInstall install)
    {
        Install = install;
        var skipped = new List<ArchiveInfo>();
        foreach (var edition in install.Editions.OrderBy(e => e.Edition))
        {
            foreach (var file in install.Storage.EnumerateFiles(edition.DataFolder, "*.pp").Order(StringComparer.OrdinalIgnoreCase))
            {
                string path = $"{edition.DataFolder}/{file}";
                using var stream = install.Storage.OpenRead(path);
                var info = new ArchiveInfo(file, edition.Edition, path, stream.Length);
                if (PpArchive.IsSb3Archive(stream))
                    _archives[file] = info;
                else
                    skipped.Add(info);
            }
        }
        Archives = _archives.Values.OrderBy(a => a.Edition).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        SkippedFiles = skipped;
    }

    public bool HasArchive(string archiveName) => _archives.ContainsKey(archiveName);

    public PpArchive GetArchive(string archiveName)
    {
        if (!_archives.TryGetValue(archiveName, out var info))
            throw new FileNotFoundException($"Archive {archiveName} is not installed.");
        return _open.GetOrAdd(info.Name, _ => new Lazy<PpArchive>(() =>
            PpArchive.Open(Install.Storage.OpenRead(info.Path), info.Name))).Value;
    }

    public byte[] ReadFile(string archiveName, string fileName) => GetArchive(archiveName).ReadAllBytes(fileName);

    public LstTable ReadTable(string archiveName, string fileName) =>
        LstTable.Decode(ReadFile(archiveName, fileName), fileName);

    public XxModel ReadModel(string archiveName, string fileName) => XxReader.Read(ReadFile(archiveName, fileName));

    /// <summary>Finds a file by name in any archive (slow: opens every archive). Prefers later editions.</summary>
    public (ArchiveInfo Archive, PpEntry Entry)? FindAnywhere(string fileName)
    {
        foreach (var info in Archives.Reverse())
        {
            var entry = GetArchive(info.Name).Find(fileName);
            if (entry is not null)
                return (info, entry);
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var lazy in _open.Values)
            if (lazy.IsValueCreated)
                lazy.Value.Dispose();
        _open.Clear();
    }
}
