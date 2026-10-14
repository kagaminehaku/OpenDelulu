namespace OpenDelulu.Engine.Storage;

/// <summary><see cref="IGameStorage"/> over a local directory (desktop, or Android app-specific storage).</summary>
public sealed class FolderGameStorage(string rootPath) : IGameStorage
{
    public string RootPath { get; } = Path.GetFullPath(rootPath);

    public string Description => RootPath;

    string Resolve(string relativePath) =>
        relativePath.Length == 0 ? RootPath : Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public bool DirectoryExists(string relativePath) => Directory.Exists(Resolve(relativePath));

    public bool FileExists(string relativePath) => File.Exists(Resolve(relativePath));

    public IEnumerable<string> EnumerateFiles(string relativeDirectory, string searchPattern = "*")
    {
        var dir = Resolve(relativeDirectory);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, searchPattern, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).Select(p => Path.GetFileName(p))
            : [];
    }

    public IEnumerable<string> EnumerateDirectories(string relativeDirectory)
    {
        var dir = Resolve(relativeDirectory);
        return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).Select(p => Path.GetFileName(p)) : [];
    }

    public Stream OpenRead(string relativePath) =>
        new FileStream(Resolve(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
}
