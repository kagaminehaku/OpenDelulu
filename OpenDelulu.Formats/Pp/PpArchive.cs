using System.Buffers.Binary;
using OpenDelulu.Formats.IO;

namespace OpenDelulu.Formats.Pp;

/// <summary>
/// Reader for the Sexy Beach 3 flavour of Illusion's .pp container.
/// <code>
/// int32    fileCount
/// int32    totalDataSize           (sum of all file sizes)
/// byte[32] name  x fileCount       (Shift-JIS, every byte negated)
/// int32    size  x fileCount
/// ...      file data, back to back, every byte negated
/// </code>
/// </summary>
/// <remarks>Thread-safe: reads are serialised on the underlying stream.</remarks>
public sealed class PpArchive : IDisposable
{
    const int NameLength = 0x20;

    readonly Stream _stream;
    readonly Lock _gate = new();
    readonly Dictionary<string, PpEntry> _byName;

    public string Name { get; }
    public IReadOnlyList<PpEntry> Entries { get; }

    PpArchive(string name, Stream stream, List<PpEntry> entries)
    {
        Name = name;
        _stream = stream;
        Entries = entries;
        _byName = new Dictionary<string, PpEntry>(entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            _byName.TryAdd(entry.Name, entry);
    }

    public static int HeaderSize(int fileCount) => 8 + (NameLength + 4) * fileCount;

    /// <summary>Checks the header arithmetic without reading the name table.</summary>
    public static bool IsSb3Archive(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 8)
            return false;
        Span<byte> head = stackalloc byte[8];
        stream.Position = 0;
        stream.ReadExactly(head);
        int count = BinaryPrimitives.ReadInt32LittleEndian(head);
        long total = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
        return count > 0 && count < 1_000_000 && HeaderSize(count) + total == stream.Length;
    }

    /// <summary>Opens an archive. Takes ownership of <paramref name="stream"/> (must be seekable).</summary>
    public static PpArchive Open(Stream stream, string name)
    {
        if (!IsSb3Archive(stream))
        {
            stream.Dispose();
            throw new InvalidDataException($"{name} is not a Sexy Beach 3 .pp archive.");
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream, ShiftJis.Encoding, leaveOpen: true);
        int count = reader.ReadInt32();
        reader.ReadUInt32(); // total size, validated above

        var nameTable = reader.ReadExactBytes(NameLength * count);
        Sb3Cipher.Negate(nameTable);

        var entries = new List<PpEntry>(count);
        long offset = HeaderSize(count);
        for (int i = 0; i < count; i++)
        {
            string entryName = ShiftJis.GetString(nameTable.AsSpan(i * NameLength, NameLength));
            int size = reader.ReadInt32();
            entries.Add(new PpEntry(entryName, offset, size));
            offset += size;
        }

        return new PpArchive(name, stream, entries);
    }

    public static PpArchive Open(string path) =>
        Open(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess), Path.GetFileName(path));

    public PpEntry? Find(string fileName) => _byName.GetValueOrDefault(fileName);

    public PpEntry Get(string fileName) =>
        Find(fileName) ?? throw new FileNotFoundException($"{fileName} not found in {Name}.");

    /// <summary>Reads and decodes a whole subfile.</summary>
    public byte[] ReadAllBytes(PpEntry entry)
    {
        var data = ReadRaw(entry);
        Sb3Cipher.Negate(data);
        return data;
    }

    public byte[] ReadAllBytes(string fileName) => ReadAllBytes(Get(fileName));

    /// <summary>Raw (still negated) bytes, e.g. for repacking.</summary>
    public byte[] ReadRaw(PpEntry entry)
    {
        var data = GC.AllocateUninitializedArray<byte>(entry.Size);
        lock (_gate)
        {
            _stream.Position = entry.Offset;
            _stream.ReadExactly(data);
        }
        return data;
    }

    public MemoryStream OpenRead(PpEntry entry) => new(ReadAllBytes(entry), writable: false);

    public void Dispose() => _stream.Dispose();

    public override string ToString() => $"{Name} ({Entries.Count} files)";
}
