namespace OpenDelulu.Formats.Video;

/// <summary>
/// Reads one elementary stream out of an MPEG-1 program stream (.mpg: pack headers 0x000001BA, system header 0xBB,
/// packets 0xC0..0xDF audio, 0xE0..0xEF video). Exposes the stream's payload as a forward-only <see cref="Stream"/>, so
/// the video and audio decoders can each read their own copy of the file.
/// </summary>
public sealed class MpegProgramStream : Stream
{
    readonly Stream _source;
    readonly Func<int, bool> _wanted;
    byte[] _payload = [];
    int _offset;
    bool _ended;

    /// <param name="wanted">Stream ids to keep, e.g. 0xE0 for the first video stream.</param>
    public MpegProgramStream(Stream source, Func<int, bool> wanted)
    {
        _source = source;
        _wanted = wanted;
    }

    public static MpegProgramStream Video(Stream source) => new(source, id => id is >= 0xE0 and <= 0xEF);
    public static MpegProgramStream Audio(Stream source) => new(source, id => id is >= 0xC0 and <= 0xDF);

    public override int Read(byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (count > 0)
        {
            if (_offset >= _payload.Length && !NextPacket())
                break;
            int n = Math.Min(count, _payload.Length - _offset);
            Buffer.BlockCopy(_payload, _offset, buffer, offset, n);
            _offset += n;
            offset += n;
            count -= n;
            total += n;
        }
        return total;
    }

    /// <summary>Finds the next packet of a wanted stream; false at the end of the file.</summary>
    bool NextPacket()
    {
        while (!_ended)
        {
            if (!SyncToStartCode(out int code))
            {
                _ended = true;
                return false;
            }
            switch (code)
            {
                case 0xBA: // pack header: MPEG-1 has 8 more bytes ('0010' ...), MPEG-2 10 + stuffing
                {
                    int first = _source.ReadByte();
                    if (first < 0)
                        return End();
                    if ((first & 0xC0) == 0x40)
                    {
                        Skip(8);
                        int stuffing = _source.ReadByte() & 7;
                        Skip(stuffing);
                    }
                    else
                    {
                        Skip(7);
                    }
                    continue;
                }
                case 0xB9: // program end
                    return End();
                default:
                {
                    if (code < 0xBB)
                        continue; // not a packet (stray start code)
                    int length = ReadUInt16();
                    if (length < 0)
                        return End();
                    if (!_wanted(code))
                    {
                        Skip(length);
                        continue;
                    }
                    var packet = new byte[length];
                    if (_source.ReadAtLeast(packet, length, throwOnEndOfStream: false) < length)
                        return End();
                    int header = PacketHeaderLength(packet);
                    _payload = packet[header..];
                    _offset = 0;
                    if (_payload.Length > 0)
                        return true;
                    continue;
                }
            }
        }
        return false;
    }

    bool End()
    {
        _ended = true;
        return false;
    }

    /// <summary>MPEG-1 packet header: stuffing 0xFF bytes, optional STD buffer (01xx), optional PTS (0010) or PTS+DTS (0011).</summary>
    static int PacketHeaderLength(byte[] p)
    {
        int i = 0;
        while (i < p.Length && p[i] == 0xFF)
            i++;
        if (i < p.Length && (p[i] & 0xC0) == 0x40)
            i += 2;
        if (i >= p.Length)
            return p.Length;
        if ((p[i] & 0xF0) == 0x20)
            i += 5;
        else if ((p[i] & 0xF0) == 0x30)
            i += 10;
        else if (p[i] == 0x0F)
            i += 1;
        else if ((p[i] & 0xC0) == 0x80)
            i += 3 + p[Math.Min(i + 2, p.Length - 1)]; // MPEG-2 PES header
        return Math.Min(i, p.Length);
    }

    bool SyncToStartCode(out int code)
    {
        int zeros = 0;
        while (true)
        {
            int b = _source.ReadByte();
            if (b < 0)
            {
                code = -1;
                return false;
            }
            if (zeros >= 2 && b == 1)
            {
                code = _source.ReadByte();
                return code >= 0;
            }
            zeros = b == 0 ? zeros + 1 : 0;
        }
    }

    int ReadUInt16()
    {
        int a = _source.ReadByte(), b = _source.ReadByte();
        return a < 0 || b < 0 ? -1 : (a << 8) | b;
    }

    void Skip(int count)
    {
        if (_source.CanSeek)
        {
            _source.Seek(count, SeekOrigin.Current);
            return;
        }
        Span<byte> scratch = stackalloc byte[256];
        while (count > 0)
        {
            int n = _source.Read(scratch[..Math.Min(count, scratch.Length)]);
            if (n <= 0)
                return;
            count -= n;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _source.Dispose();
        base.Dispose(disposing);
    }
}
