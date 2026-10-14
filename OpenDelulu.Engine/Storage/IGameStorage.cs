namespace OpenDelulu.Engine.Storage;

/// <summary>
/// Read-only access to an installed copy of the game. Paths are relative, '/' separated.
/// Desktop uses a plain folder; mobile ports can back this with app storage, SAF or a bundle.
/// </summary>
public interface IGameStorage
{
    /// <summary>Human readable location, for diagnostics.</summary>
    string Description { get; }

    bool DirectoryExists(string relativePath);

    bool FileExists(string relativePath);

    /// <summary>Names (not paths) of files directly inside <paramref name="relativeDirectory"/>.</summary>
    IEnumerable<string> EnumerateFiles(string relativeDirectory, string searchPattern = "*");

    /// <summary>Names of sub-directories directly inside <paramref name="relativeDirectory"/>.</summary>
    IEnumerable<string> EnumerateDirectories(string relativeDirectory);

    /// <summary>Opens a seekable read stream. Archives are read with random access.</summary>
    Stream OpenRead(string relativePath);
}
