using System.Text;

namespace OpenDelulu.Formats.IO;

/// <summary>Shift-JIS (code page 932): the text encoding used by every Illusion file of this era.</summary>
public static class ShiftJis
{
    public static Encoding Encoding { get; } = CreateEncoding();

    static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    public static string GetString(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return Encoding.GetString(end >= 0 ? bytes[..end] : bytes);
    }
}
