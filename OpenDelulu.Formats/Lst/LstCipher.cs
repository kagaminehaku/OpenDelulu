namespace OpenDelulu.Formats.Lst;

/// <summary>
/// Second encryption layer on .lst tables (applied on top of the .pp negation).
/// <para>
/// Every complete 4-byte group is XOR-ed with a repeating 32-byte key; the trailing
/// <c>length % 4</c> bytes are stored in plain text. The decrypted payload starts with an
/// 8-byte magic followed by Shift-JIS, tab-separated text.
/// </para>
/// Recovered by frequency analysis over all 278 .lst files shipped with SB3, the Bonus Disc and
/// the Gravure add-on; every one of them decodes to valid Shift-JIS with this key.
/// </summary>
public static class LstCipher
{
    static ReadOnlySpan<byte> Key =>
    [
        0xDB, 0xBD, 0x3B, 0x4B, 0xFF, 0x92, 0x57, 0xE3, 0x2D, 0x35, 0xE0, 0x5E, 0x26, 0x1D, 0x0C, 0x57,
        0x1D, 0x2E, 0x7F, 0x48, 0x2E, 0xB6, 0x50, 0x85, 0xB7, 0xFC, 0xBF, 0x9A, 0xF5, 0x83, 0x44, 0x18,
    ];

    public static ReadOnlySpan<byte> Magic => [0xB0, 0xFF, 0x57, 0xE9, 0x8D, 0xA2, 0x2A, 0xC1];

    /// <summary>XOR transform; symmetric, so it both encrypts and decrypts.</summary>
    public static void Transform(Span<byte> data)
    {
        var key = Key;
        int full = data.Length & ~3;
        for (int i = 0; i < full; i++)
            data[i] ^= key[i & 31];
    }

    /// <summary>Returns true when <paramref name="decrypted"/> begins with the .lst magic.</summary>
    public static bool HasMagic(ReadOnlySpan<byte> decrypted) => decrypted.StartsWith(Magic);
}
