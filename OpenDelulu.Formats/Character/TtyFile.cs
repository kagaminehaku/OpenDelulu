using OpenDelulu.Formats.IO;

namespace OpenDelulu.Formats.Character;

/// <summary>
/// A named parameter set of a .tty jiggle rig ("初期位置" = initial position, ...).
/// <see cref="Values"/> holds 19 raw 32-bit values, mostly floats (one is -9.807 = gravity, others are limits in
/// radians and spring constants); <see cref="Entries"/> are 11-byte records (byte, uint16, float, float).
/// </summary>
public sealed record TtyParameterSet(int Id, string Name, uint[] Values, IReadOnlyList<(byte Flag, ushort Index, float A, float B)> Entries)
{
    public float Float(int index) => BitConverter.UInt32BitsToSingle(Values[index]);

    public override string ToString() => $"{Id} {Name}";
}

/// <summary>
/// .tty: soft-body jiggle for breasts (boNN_01.tty: o01_J_Mune*) and buttocks (bo00_02.tty: o01_J_Siri*).
/// Verified on all 4 files:
/// <code>
/// int32  version (1)
/// header bytes (2 or 3), then joint names (byte length + NOT-encoded), until the int32 set count
/// set 0: name; sets 1..: uint16 id + name; each: 19 x 4 bytes, byte n, n x 11 bytes
/// </code>
/// </summary>
public sealed class TtyFile
{
    public int Version { get; init; }
    public byte[] Header { get; init; } = [];
    public IReadOnlyList<string> Joints { get; init; } = [];
    public IReadOnlyList<TtyParameterSet> Sets { get; init; } = [];

    public static TtyFile Read(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data, writable: false), ShiftJis.Encoding);
        int version = r.ReadInt32();

        // Header bytes until the first "o01_" joint name.
        var header = new List<byte>();
        while (r.BaseStream.Position < data.Length && !NameAt(data, (int)r.BaseStream.Position))
            header.Add(r.ReadByte());
        var joints = new List<string>();
        while (NameAt(data, (int)r.BaseStream.Position))
            joints.Add(r.ReadFixedName(r.ReadByte()));

        int count = r.ReadInt32();
        var sets = new List<TtyParameterSet>(count);
        for (int i = 0; i < count; i++)
        {
            int id = i == 0 ? 0 : r.ReadUInt16();
            string name = r.ReadFixedName(r.ReadByte());
            var values = new uint[19];
            for (int v = 0; v < values.Length; v++)
                values[v] = r.ReadUInt32();
            int entryCount = r.ReadByte();
            var entries = new List<(byte, ushort, float, float)>(entryCount);
            for (int e = 0; e < entryCount; e++)
                entries.Add((r.ReadByte(), r.ReadUInt16(), r.ReadSingle(), r.ReadSingle()));
            sets.Add(new TtyParameterSet(id, name, values, entries));
        }
        if (r.BaseStream.Position != data.Length)
            throw new InvalidDataException($".tty: {data.Length - r.BaseStream.Position} unparsed bytes.");
        return new TtyFile { Version = version, Header = [.. header], Joints = joints, Sets = sets };
    }

    /// <summary>True if a length-prefixed NOT-encoded name starting with "o01_" begins at <paramref name="at"/>.</summary>
    static bool NameAt(byte[] data, int at)
    {
        if (at + 5 >= data.Length)
            return false;
        int length = data[at];
        return length is >= 4 and <= 64 && at + 1 + length <= data.Length
            && (byte)~data[at + 1] == 'o' && (byte)~data[at + 2] == '0' && (byte)~data[at + 3] == '1' && (byte)~data[at + 4] == '_';
    }
}
