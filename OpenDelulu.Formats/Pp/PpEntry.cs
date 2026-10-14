namespace OpenDelulu.Formats.Pp;

/// <summary>A file stored inside a .pp archive. <see cref="Offset"/> is absolute within the archive.</summary>
public sealed record PpEntry(string Name, long Offset, int Size)
{
    public string Extension => Path.GetExtension(Name).ToLowerInvariant();

    public override string ToString() => Name;
}
