using OpenDelulu.Formats.IO;

namespace OpenDelulu.Formats.Script;

/// <summary>A line of a message. Lines with <see cref="Styled"/> carry two bytes and two ints of formatting.</summary>
public sealed record SdtLine(string Text, bool Styled, byte Style1, byte Style2, int Style3, int Style4);

/// <summary>
/// One script command. <see cref="Args"/> holds the operands in file order (int, byte or string);
/// table-like commands put their rows in <see cref="Rows"/> and nested commands in <see cref="Blocks"/>.
/// </summary>
public sealed class SdtCommand
{
    public byte Op { get; init; }
    public List<object> Args { get; } = [];
    public List<object[]> Rows { get; } = [];
    public List<List<SdtCommand>> Blocks { get; } = [];

    // Op 1 (message).
    public string? Speaker { get; set; }
    public string? Voice { get; set; }
    public List<SdtLine>? Lines { get; set; }

    public string Name => SdtFile.OpName(Op);

    public override string ToString() => Op switch
    {
        SdtFile.OpComment => $"// {Args[0]}",
        SdtFile.OpMessage => $"{Speaker?.Trim('　')}{(string.IsNullOrEmpty(Voice) ? "" : $" [{Voice}]")}: {string.Join(" / ", Lines!.Select(l => l.Text))}",
        _ => $"{Name}({string.Join(", ", Args)}){(Rows.Count > 0 ? $" {Rows.Count} rows" : "")}{(Blocks.Count > 0 ? $" {Blocks.Count} blocks" : "")}",
    };
}

/// <summary>A scene: its id, a value, labels (int + string) and the command list.</summary>
public sealed class SdtScene
{
    public int Id { get; init; }
    public int Value { get; init; }
    public List<(int Value, string Text)> Labels { get; } = [];
    public List<SdtCommand> Commands { get; } = [];
}

/// <summary>
/// .sdt: event / dialogue scripts (cNN_MM_KK = character NN, sNN = stages, e99/c99/s99 = common).
/// Read from the loader in SexyBeach3PlusOriginal.exe (0x4645A0, one handler per opcode at 0x464A50).
/// Strings are int32 length (including the terminator) + bytes, bitwise NOT, Shift-JIS.
/// <code>
/// int32 version (100), string name, int32 scene count
/// scene: int32 id, int32 value, int32 n, n x (int32, string), int32 m, m x command
/// command: byte opcode + operands (see <see cref="Read"/>)
/// </code>
/// Verified on all 181 files (base sb3_0700 and Bonus Disc sb3x_1700, English translation).
/// </summary>
public sealed class SdtFile
{
    /// <summary>Opcodes used by the data (the exe handles 0..24; the others never occur).</summary>
    public const byte OpComment = 0, OpMessage = 1, OpGoto = 9, OpChoice = 11, OpSetVar = 14, OpEnd = 20, OpSwitch = 24;

    public int Version { get; init; }
    public string Name { get; init; } = "";
    public List<SdtScene> Scenes { get; } = [];
    /// <summary>Blank comments after the last counted command (ignored by the game).</summary>
    public int TrailingBlankLines { get; private set; }

    public IEnumerable<SdtCommand> AllCommands() => Scenes.SelectMany(s => Flatten(s.Commands));

    static IEnumerable<SdtCommand> Flatten(IEnumerable<SdtCommand> commands)
    {
        foreach (var command in commands)
        {
            yield return command;
            foreach (var child in command.Blocks.SelectMany(Flatten))
                yield return child;
        }
    }

    public static string OpName(byte op) => op switch
    {
        OpComment => "Comment",
        OpMessage => "Message",
        // Goto(script name, scene id).
        OpGoto => "Goto",
        // Menu: Rows hold the option texts, Blocks the commands run for each option.
        OpChoice => "Choice",
        // SetVar(variable, byte, byte, value); e.g. 20 = selected girl, 30 = intimacy setting (from script comments).
        OpSetVar => "SetVar",
        OpEnd => "End",
        // Switch(subject): Rows are cases (byte, byte, value), Blocks the matching commands; used on intimacy level.
        OpSwitch => "Switch",
        _ => $"Op{op}",
    };

    public static SdtFile Read(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data, writable: false), ShiftJis.Encoding);
        var file = new SdtFile { Version = r.ReadInt32(), Name = ReadString(r) };
        int scenes = Count(r);
        for (int s = 0; s < scenes; s++)
        {
            var scene = new SdtScene { Id = r.ReadInt32(), Value = r.ReadInt32() };
            int labels = Count(r);
            for (int i = 0; i < labels; i++)
                scene.Labels.Add((r.ReadInt32(), ReadString(r)));
            scene.Commands.AddRange(ReadCommands(r, Count(r)));
            file.Scenes.Add(scene);
        }
        // 11 files have blank comment lines after the declared command count; the game never reads them.
        while (r.BaseStream.Position < data.Length)
        {
            long at = r.BaseStream.Position;
            if (ReadCommand(r) is not { Op: OpComment, Args: [""] })
                throw new InvalidDataException($".sdt: unexpected trailing data at 0x{at:X}.");
            file.TrailingBlankLines++;
        }
        return file;
    }

    static List<SdtCommand> ReadCommands(BinaryReader r, int count)
    {
        var list = new List<SdtCommand>(count);
        for (int i = 0; i < count; i++)
            list.Add(ReadCommand(r));
        return list;
    }

    static SdtCommand ReadCommand(BinaryReader r)
    {
        long at = r.BaseStream.Position;
        var c = new SdtCommand { Op = r.ReadByte() };
        var a = c.Args;
        switch (c.Op)
        {
            case OpComment: a.Add(ReadString(r)); break;
            case OpMessage:
                a.Add(r.ReadInt32());
                c.Speaker = ReadString(r);
                c.Voice = ReadString(r);
                int lines = Count(r);
                c.Lines = new List<SdtLine>(lines);
                for (int i = 0; i < lines; i++)
                {
                    bool styled = r.ReadByte() != 0;
                    byte s1 = 0, s2 = 0; int s3 = 0, s4 = 0;
                    if (styled)
                    {
                        s1 = r.ReadByte(); s2 = r.ReadByte(); s3 = r.ReadInt32(); s4 = r.ReadInt32();
                    }
                    c.Lines.Add(new SdtLine(ReadString(r), styled, s1, s2, s3, s4));
                }
                break;
            case 2: a.Add(r.ReadByte()); a.Add(r.ReadInt32()); a.Add(r.ReadInt32()); break;
            case 3: a.Add(r.ReadInt32()); a.Add(r.ReadInt32()); break;
            case 4: a.Add(r.ReadInt32()); a.Add(ReadString(r)); for (int i = 0; i < 4; i++) a.Add(r.ReadInt32()); break;
            case 5:
                for (int i = 0, n = Count(r); i < n; i++)
                {
                    byte kind = r.ReadByte();
                    // kind 0/1: one value, 2/3: three, 4..7: two, other: none.
                    int extra = kind switch { 0 or 1 => 1, 2 or 3 => 3, >= 4 and <= 7 => 2, _ => 0 };
                    c.Rows.Add([kind, r.ReadInt32(), r.ReadInt32(), .. Ints(r, extra)]);
                }
                break;
            case 6: a.Add(r.ReadInt32()); a.Add(ReadString(r)); a.Add(r.ReadByte()); a.Add(r.ReadByte()); break;
            case 7:
                for (int i = 0, n = Count(r); i < n; i++)
                {
                    byte kind = r.ReadByte();
                    c.Rows.Add([kind, r.ReadInt32(), r.ReadInt32(), .. Ints(r, kind is 1 or 2 ? 1 : 0)]);
                }
                break;
            case 8: a.Add(r.ReadInt32()); break;
            case 9: a.Add(ReadString(r)); a.Add(r.ReadInt32()); break;
            case 10: a.Add(ReadString(r)); break;
            case 11:
                for (int i = 0, n = Count(r); i < n; i++)
                {
                    int children = Count(r);
                    c.Rows.Add([ReadString(r)]);
                    c.Blocks.Add(ReadCommands(r, children));
                }
                break;
            case 12 or 13 or 16 or 19 or 20: break;
            case 14: a.Add(r.ReadInt32()); a.Add(r.ReadByte()); a.Add(r.ReadByte()); a.Add(r.ReadInt32()); break;
            case 15:
            {
                int rows = Count(r);
                a.Add(r.ReadByte());
                int first = Count(r), second = Count(r);
                for (int i = 0; i < rows; i++)
                    c.Rows.Add([r.ReadInt32(), r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadInt32()]);
                c.Blocks.Add(ReadCommands(r, first));
                c.Blocks.Add(ReadCommands(r, second));
                break;
            }
            case 17 or 18: a.Add(r.ReadByte()); break;
            case 21: a.Add(r.ReadInt32()); a.Add(r.ReadInt32()); a.Add(r.ReadInt32()); break;
            case 22: a.Add(ReadString(r)); break;
            case 23: a.Add(r.ReadInt32()); a.Add(r.ReadByte()); break;
            case 24:
                a.Add(r.ReadInt32());
                for (int i = 0, n = Count(r); i < n; i++)
                {
                    byte b5 = r.ReadByte(), b4 = r.ReadByte();
                    int v0 = r.ReadInt32(), children = Count(r);
                    c.Rows.Add([b5, b4, v0]);
                    c.Blocks.Add(ReadCommands(r, children));
                }
                break;
            default:
                throw new InvalidDataException($".sdt: unknown opcode {c.Op} at 0x{at:X}.");
        }
        return c;
    }

    static object[] Ints(BinaryReader r, int count)
    {
        var values = new object[count];
        for (int i = 0; i < count; i++)
            values[i] = r.ReadInt32();
        return values;
    }

    static int Count(BinaryReader r)
    {
        int count = r.ReadInt32();
        if (count is < 0 or > 100_000)
            throw new InvalidDataException($".sdt: implausible count {count} at 0x{r.BaseStream.Position - 4:X}.");
        return count;
    }

    /// <summary>int32 length (including the terminator), then NOT-encoded Shift-JIS bytes.</summary>
    static string ReadString(BinaryReader r)
    {
        int length = Count(r);
        var bytes = r.ReadExactBytes(length);
        int end = 0;
        while (end < bytes.Length && bytes[end] != 0xFF)
            bytes[end] = (byte)~bytes[end++];
        return ShiftJis.Encoding.GetString(bytes, 0, end);
    }
}
