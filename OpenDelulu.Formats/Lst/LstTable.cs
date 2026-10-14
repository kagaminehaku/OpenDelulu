using OpenDelulu.Formats.IO;

namespace OpenDelulu.Formats.Lst;

/// <summary>
/// A decoded .lst file: the game's data tables (character parts, clothes, stages, poses...).
/// Rows are CRLF separated, columns are TAB separated. Row 0 is usually a header/defaults row.
/// Tables reference assets as (archive, file) pairs, e.g. <c>sb3_0310.pp  sn00_01_01_01_01.tga</c>.
/// </summary>
public sealed class LstTable
{
    public string Name { get; }
    public string Text { get; }
    public IReadOnlyList<string[]> Rows { get; }

    LstTable(string name, string text)
    {
        Name = name;
        Text = text;
        Rows = text.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .Select(line => line.Split('\t'))
            .ToArray();
    }

    /// <param name="data">Subfile bytes as returned by <see cref="Pp.PpArchive.ReadAllBytes(Pp.PpEntry)"/>.</param>
    public static LstTable Decode(ReadOnlySpan<byte> data, string name = "")
    {
        var buffer = data.ToArray();
        LstCipher.Transform(buffer);
        if (!LstCipher.HasMagic(buffer))
            throw new InvalidDataException($"{name}: missing .lst magic; not an SB3 table or wrong key.");
        int start = LstCipher.Magic.Length;
        return new LstTable(name, ShiftJis.Encoding.GetString(buffer, start, buffer.Length - start));
    }
}
