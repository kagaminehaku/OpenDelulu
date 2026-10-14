namespace OpenDelulu.Formats.Video;

/// <summary>A decoded picture: Y (width × height), Cb and Cr (half size each way).</summary>
public sealed class VideoFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Y { get; init; }
    public required byte[] Cb { get; init; }
    public required byte[] Cr { get; init; }
    /// <summary>Display index (0, 1, 2…).</summary>
    public int Number { get; internal set; }

    /// <summary>
    /// Converts the top-left <paramref name="width"/> × <paramref name="height"/> pixels (the picture inside the
    /// macroblock padding) to RGBA, BT.601 studio range.
    /// </summary>
    public void ToRgba(Span<byte> rgba, int stride, int width, int height)
    {
        int cw = Width >> 1;
        for (int y = 0; y < Math.Min(height, Height); y++)
        {
            var row = rgba.Slice(y * stride, width * 4);
            int yRow = y * Width, cRow = (y >> 1) * cw;
            for (int x = 0; x < Math.Min(width, Width); x++)
            {
                int luma = (Y[yRow + x] - 16) * 298;
                int cb = Cb[cRow + (x >> 1)] - 128, cr = Cr[cRow + (x >> 1)] - 128;
                int o = x * 4;
                row[o] = Clamp((luma + 409 * cr + 128) >> 8);
                row[o + 1] = Clamp((luma - 100 * cb - 208 * cr + 128) >> 8);
                row[o + 2] = Clamp((luma + 516 * cb + 128) >> 8);
                row[o + 3] = 255;
            }
        }
    }

    static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}

/// <summary>
/// MPEG-1 video decoder (ISO/IEC 11172-2): sequence/GOP/picture/slice/macroblock layers, I, P and B pictures,
/// half-pel motion compensation, intra/non-intra dequantisation and an 8×8 float IDCT. Pictures come out in display
/// order. Written for the game's movies (logo, opening, dates); D-pictures and MPEG-2 extensions are not supported.
/// </summary>
public sealed class Mpeg1Video
{
    const int PictureI = 1, PictureP = 2, PictureB = 3;
    static readonly double[] FrameRates = [0, 23.976, 24, 25, 29.97, 30, 50, 59.94, 60];

    readonly MpegBitReader _bits;
    readonly int[] _intraMatrix = new int[64], _nonIntraMatrix = new int[64];
    int _mbWidth, _mbHeight;
    // Reference pictures: _forward is the older, _backward the newer decoded I/P picture; _current receives B pictures.
    VideoFrame? _forward, _backward, _current;
    int _references;
    bool _backwardShown = true;
    int _output;
    bool _ended;

    // Picture state.
    int _pictureType, _forwardRSize, _backwardRSize;
    bool _fullPelForward, _fullPelBackward;
    // Slice/macroblock state.
    int _quantizerScale, _mbAddress;
    int _dcY, _dcCb, _dcCr;
    int _mvForwardX, _mvForwardY, _mvBackwardX, _mvBackwardY;
    bool _mbForward, _mbBackward, _mbIntra;
    readonly int[] _block = new int[64];
    readonly float[] _idct = new float[64];

    public int Width { get; private set; }
    public int Height { get; private set; }
    public double FrameRate { get; private set; } = 30;

    public Mpeg1Video(Stream elementaryStream)
    {
        _bits = new MpegBitReader(elementaryStream);
        Array.Copy(DefaultIntraMatrix, _intraMatrix, 64);
        Array.Fill(_nonIntraMatrix, 16);
        // Read up to the first sequence header so the size is known.
        while (_bits.NextStartCode() is int code)
        {
            if (code == 0xB3)
            {
                ReadSequenceHeader();
                return;
            }
        }
        throw new InvalidDataException("No MPEG-1 sequence header.");
    }

    /// <summary>The next picture in display order, or null at the end of the stream.</summary>
    public VideoFrame? NextFrame()
    {
        while (!_ended)
        {
            if (_bits.NextStartCode() is not int code)
            {
                _ended = true;
                break;
            }
            switch (code)
            {
                case 0xB3:
                    ReadSequenceHeader();
                    break;
                case 0xB7:
                    _ended = true;
                    break;
                case 0x00:
                    if (DecodePicture() is { } frame)
                        return frame;
                    break;
            }
        }
        // Flush the last reference picture.
        if (!_backwardShown)
        {
            _backwardShown = true;
            _backward!.Number = _output++;
            return _backward;
        }
        return null;
    }

    void ReadSequenceHeader()
    {
        int width = _bits.Read(12), height = _bits.Read(12);
        _bits.Read(4); // aspect ratio
        int rate = _bits.Read(4);
        _bits.Read(18); // bit rate
        _bits.Read(1);
        _bits.Read(10); // VBV buffer size
        _bits.Read(1); // constrained parameters
        if (_bits.Read(1) == 1)
            for (int i = 0; i < 64; i++)
                _intraMatrix[ZigZag[i]] = _bits.Read(8);
        else
            Array.Copy(DefaultIntraMatrix, _intraMatrix, 64);
        if (_bits.Read(1) == 1)
            for (int i = 0; i < 64; i++)
                _nonIntraMatrix[ZigZag[i]] = _bits.Read(8);
        else
            Array.Fill(_nonIntraMatrix, 16);
        if (rate > 0 && rate < FrameRates.Length)
            FrameRate = FrameRates[rate];
        if (width == Width && height == Height && _forward is not null)
            return;
        Width = width;
        Height = height;
        _mbWidth = (width + 15) >> 4;
        _mbHeight = (height + 15) >> 4;
        _forward = NewFrame();
        _backward = NewFrame();
        _current = NewFrame();
        _references = 0;
        _backwardShown = true;
    }

    VideoFrame NewFrame()
    {
        int w = _mbWidth * 16, h = _mbHeight * 16;
        return new VideoFrame { Width = w, Height = h, Y = new byte[w * h], Cb = new byte[w * h / 4], Cr = new byte[w * h / 4] };
    }

    VideoFrame? DecodePicture()
    {
        _bits.Read(10); // temporal reference
        _pictureType = _bits.Read(3);
        _bits.Read(16); // VBV delay
        if (_pictureType is not (PictureI or PictureP or PictureB))
            return null; // D-pictures are not supported
        if (_pictureType is PictureP or PictureB)
        {
            _fullPelForward = _bits.Read(1) == 1;
            int f = _bits.Read(3);
            _forwardRSize = f - 1;
        }
        if (_pictureType == PictureB)
        {
            _fullPelBackward = _bits.Read(1) == 1;
            int f = _bits.Read(3);
            _backwardRSize = f - 1;
        }
        // Pictures whose references are missing (B-pictures of an open GOP at the start) are skipped.
        if ((_pictureType == PictureP && _references < 1) || (_pictureType == PictureB && _references < 2))
            return null;

        // An I/P picture overwrites the older reference; afterwards the previous newer one is "forward" (what a P
        // picture predicts from) and the new one "backward".
        var target = _pictureType == PictureB ? _current! : _forward!;
        if (_pictureType != PictureB)
            (_forward, _backward) = (_backward, target);

        // Slices until the next non-slice start code.
        while (true)
        {
            int? code = _bits.PeekStartCode();
            if (code is not int c || c < 0x01 || c > 0xAF)
                break;
            _bits.NextStartCode();
            DecodeSlice(c, target);
        }

        if (_pictureType == PictureB)
        {
            _current!.Number = _output++;
            return _current;
        }
        // The previous reference picture is due now (display order: I/P pictures wait for the B pictures before them).
        bool showPrevious = !_backwardShown;
        _references = Math.Min(2, _references + 1);
        _backwardShown = false;
        if (showPrevious)
        {
            _forward!.Number = _output++;
            return _forward;
        }
        return null;
    }

    void DecodeSlice(int verticalPosition, VideoFrame target)
    {
        _quantizerScale = _bits.Read(5);
        while (_bits.Read(1) == 1)
            _bits.Read(8);
        _mbAddress = (verticalPosition - 1) * _mbWidth - 1;
        ResetDcPredictors();
        _mvForwardX = _mvForwardY = _mvBackwardX = _mvBackwardY = 0;
        _mbForward = _mbBackward = false;
        bool first = true;
        while (!_bits.AtStartCode() && _mbAddress < _mbWidth * _mbHeight - 1)
        {
            if (!DecodeMacroblock(target, first))
                break;
            first = false;
        }
    }

    void ResetDcPredictors() => _dcY = _dcCb = _dcCr = 128 * 8;

    bool DecodeMacroblock(VideoFrame target, bool first)
    {
        int increment = 0;
        while (true)
        {
            int v = Vlc.MacroblockAddressIncrement.Decode(_bits);
            if (v == 34) // stuffing
                continue;
            if (v == 35) // escape
            {
                increment += 33;
                continue;
            }
            if (v < 0)
                return false;
            increment += v;
            break;
        }

        // Skipped macroblocks.
        if (!first && increment > 1)
        {
            ResetDcPredictors();
            if (_pictureType == PictureP)
            {
                _mvForwardX = _mvForwardY = 0;
                for (int i = 1; i < increment; i++)
                    PredictMacroblock(target, _mbAddress + i, forward: true, backward: false, 0, 0, 0, 0);
            }
            else if (_pictureType == PictureB)
            {
                for (int i = 1; i < increment; i++)
                    PredictMacroblock(target, _mbAddress + i, _mbForward, _mbBackward, _mvForwardX, _mvForwardY, _mvBackwardX, _mvBackwardY);
            }
        }
        _mbAddress += increment;
        if (_mbAddress >= _mbWidth * _mbHeight)
            return false;

        int type = (_pictureType switch
        {
            PictureI => Vlc.MacroblockTypeI,
            PictureP => Vlc.MacroblockTypeP,
            _ => Vlc.MacroblockTypeB,
        }).Decode(_bits);
        if (type < 0)
            return false;
        bool quant = (type & 0x10) != 0, forward = (type & 0x08) != 0, backward = (type & 0x04) != 0,
            pattern = (type & 0x02) != 0, intra = (type & 0x01) != 0;
        if (quant)
            _quantizerScale = _bits.Read(5);

        if (intra)
        {
            _mvForwardX = _mvForwardY = _mvBackwardX = _mvBackwardY = 0;
            _mbForward = _mbBackward = false;
        }
        else
        {
            ResetDcPredictors();
            if (forward)
            {
                _mvForwardX = DecodeMotion(_mvForwardX, _forwardRSize);
                _mvForwardY = DecodeMotion(_mvForwardY, _forwardRSize);
            }
            else if (_pictureType == PictureP)
            {
                _mvForwardX = _mvForwardY = 0; // P macroblock without motion: zero vector
                forward = true;
            }
            if (backward)
            {
                _mvBackwardX = DecodeMotion(_mvBackwardX, _backwardRSize);
                _mvBackwardY = DecodeMotion(_mvBackwardY, _backwardRSize);
            }
            _mbForward = forward;
            _mbBackward = backward;
        }
        _mbIntra = intra;

        int cbp = intra ? 0x3F : pattern ? Vlc.CodedBlockPattern.Decode(_bits) : 0;
        if (cbp < 0)
            return false;

        if (!intra)
            PredictMacroblock(target, _mbAddress, _mbForward, _mbBackward, _mvForwardX, _mvForwardY, _mvBackwardX, _mvBackwardY);

        int mbX = _mbAddress % _mbWidth, mbY = _mbAddress / _mbWidth;
        for (int b = 0; b < 6; b++)
        {
            if ((cbp & (0x20 >> b)) == 0)
                continue;
            DecodeBlock(b);
            var (plane, stride, x, y) = b switch
            {
                < 4 => (target.Y, target.Width, mbX * 16 + (b & 1) * 8, mbY * 16 + (b >> 1) * 8),
                4 => (target.Cb, target.Width >> 1, mbX * 8, mbY * 8),
                _ => (target.Cr, target.Width >> 1, mbX * 8, mbY * 8),
            };
            InverseDct(_block, _idct);
            int o = y * stride + x;
            for (int r = 0; r < 8; r++, o += stride)
                for (int c = 0; c < 8; c++)
                {
                    float value = _idct[r * 8 + c] + (intra ? 0 : plane[o + c]);
                    int v = (int)MathF.Round(value);
                    plane[o + c] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                }
        }
        // Intra macroblocks without the forward/backward state for B skips.
        return true;
    }

    int DecodeMotion(int previous, int rSize)
    {
        int code = Vlc.MotionCode.Decode(_bits);
        if (code == int.MinValue)
            return previous;
        int f = 1 << rSize;
        int delta;
        if (f == 1 || code == 0)
            delta = code;
        else
        {
            int residual = _bits.Read(rSize);
            delta = (Math.Abs(code) - 1) * f + residual + 1;
            if (code < 0)
                delta = -delta;
        }
        int vector = previous + delta;
        int max = (16 * f) - 1, min = -16 * f;
        if (vector > max)
            vector -= 32 * f;
        else if (vector < min)
            vector += 32 * f;
        return vector;
    }

    void DecodeBlock(int index)
    {
        Array.Clear(_block);
        int n = 0;
        var matrix = _mbIntra ? _intraMatrix : _nonIntraMatrix;
        if (_mbIntra)
        {
            int size = (index < 4 ? Vlc.DcSizeLuma : Vlc.DcSizeChroma).Decode(_bits);
            int diff = 0;
            if (size > 0)
            {
                int bits = _bits.Read(size);
                diff = (bits & (1 << (size - 1))) != 0 ? bits : bits - (1 << size) + 1;
            }
            ref int predictor = ref (index < 4 ? ref _dcY : ref index == 4 ? ref _dcCb : ref _dcCr);
            predictor += diff * 8;
            _block[0] = predictor;
            n = 1;
        }
        else
        {
            // First coefficient of a non-intra block: "1s" is run 0, level ±1.
            if (_bits.Peek(1) == 1)
            {
                _bits.Read(1);
                int level = _bits.Read(1) == 1 ? -1 : 1;
                Dequantise(0, level, matrix);
                n = 1;
            }
        }

        while (true)
        {
            int entry = Vlc.DctCoefficient.Decode(_bits);
            if (entry == EndOfBlock)
                break;
            int run, level;
            if (entry == Escape)
            {
                run = _bits.Read(6);
                level = _bits.Read(8);
                if (level == 0)
                    level = _bits.Read(8);
                else if (level == 128)
                    level = _bits.Read(8) - 256;
                else if (level > 128)
                    level -= 256;
            }
            else if (entry < 0)
            {
                break; // broken stream
            }
            else
            {
                run = entry >> 8;
                level = entry & 0xFF;
                if (_bits.Read(1) == 1)
                    level = -level;
            }
            n += run;
            if (n >= 64)
                break;
            Dequantise(n, level, matrix);
            n++;
        }
    }

    void Dequantise(int scanIndex, int level, int[] matrix)
    {
        int position = ZigZag[scanIndex];
        int value = _mbIntra
            ? 2 * level * _quantizerScale * matrix[position] / 16
            : (2 * level + Math.Sign(level)) * _quantizerScale * matrix[position] / 16;
        if ((value & 1) == 0 && value != 0)
            value -= Math.Sign(value);
        _block[position] = Math.Clamp(value, -2048, 2047);
    }

    /// <summary>Copies (or averages) the motion-compensated prediction of one macroblock into the target.</summary>
    void PredictMacroblock(VideoFrame target, int address, bool forward, bool backward, int fx, int fy, int bx, int by)
    {
        int mbX = address % _mbWidth, mbY = address / _mbWidth;
        if (_fullPelForward)
        {
            fx <<= 1;
            fy <<= 1;
        }
        if (_fullPelBackward)
        {
            bx <<= 1;
            by <<= 1;
        }
        // In a P-picture the reference is the previous I/P picture ("forward"); in a B-picture forward = the older
        // one and backward = the newer one.
        var forwardRef = _pictureType == PictureP ? _forward! : _forward!;
        var backwardRef = _backward!;
        if (!forward && !backward)
        {
            forward = true;
            fx = fy = 0;
        }
        for (int plane = 0; plane < 3; plane++)
        {
            int size = plane == 0 ? 16 : 8, stride = plane == 0 ? target.Width : target.Width >> 1;
            int x = mbX * size, y = mbY * size;
            byte[] dst = Plane(target, plane);
            int ffx = plane == 0 ? fx : fx / 2, ffy = plane == 0 ? fy : fy / 2;
            int bbx = plane == 0 ? bx : bx / 2, bby = plane == 0 ? by : by / 2;
            if (forward && backward)
            {
                Span<byte> a = stackalloc byte[256], b = stackalloc byte[256];
                Copy(Plane(forwardRef, plane), stride, PlaneHeight(plane), x, y, ffx, ffy, size, a);
                Copy(Plane(backwardRef, plane), stride, PlaneHeight(plane), x, y, bbx, bby, size, b);
                for (int r = 0; r < size; r++)
                    for (int c = 0; c < size; c++)
                        dst[(y + r) * stride + x + c] = (byte)((a[r * size + c] + b[r * size + c] + 1) >> 1);
            }
            else
            {
                Span<byte> a = stackalloc byte[256];
                if (forward)
                    Copy(Plane(forwardRef, plane), stride, PlaneHeight(plane), x, y, ffx, ffy, size, a);
                else
                    Copy(Plane(backwardRef, plane), stride, PlaneHeight(plane), x, y, bbx, bby, size, a);
                for (int r = 0; r < size; r++)
                    a.Slice(r * size, size).CopyTo(dst.AsSpan((y + r) * stride + x, size));
            }
        }
    }

    int PlaneHeight(int plane) => plane == 0 ? _mbHeight * 16 : _mbHeight * 8;

    static byte[] Plane(VideoFrame frame, int plane) => plane switch { 0 => frame.Y, 1 => frame.Cb, _ => frame.Cr };

    /// <summary>Half-pel block fetch: (vx, vy) in half samples, clamped to the picture.</summary>
    static void Copy(byte[] src, int stride, int height, int x, int y, int vx, int vy, int size, Span<byte> dst)
    {
        int sx = x + (vx >> 1), sy = y + (vy >> 1);
        bool hx = (vx & 1) != 0, hy = (vy & 1) != 0;
        int maxX = stride - 1, maxY = height - 1;
        for (int r = 0; r < size; r++)
        {
            int y0 = Math.Clamp(sy + r, 0, maxY), y1 = Math.Clamp(sy + r + 1, 0, maxY);
            for (int c = 0; c < size; c++)
            {
                int x0 = Math.Clamp(sx + c, 0, maxX), x1 = Math.Clamp(sx + c + 1, 0, maxX);
                int p = src[y0 * stride + x0];
                int value;
                if (hx && hy)
                    value = (p + src[y0 * stride + x1] + src[y1 * stride + x0] + src[y1 * stride + x1] + 2) >> 2;
                else if (hx)
                    value = (p + src[y0 * stride + x1] + 1) >> 1;
                else if (hy)
                    value = (p + src[y1 * stride + x0] + 1) >> 1;
                else
                    value = p;
                dst[r * size + c] = (byte)value;
            }
        }
    }

    // ------------------------------------------------------------------ IDCT

    static readonly float[] IdctCos = BuildIdctTable();

    static float[] BuildIdctTable()
    {
        var table = new float[64];
        for (int x = 0; x < 8; x++)
            for (int u = 0; u < 8; u++)
                table[x * 8 + u] = (float)((u == 0 ? Math.Sqrt(0.125) : 0.5) * Math.Cos((2 * x + 1) * u * Math.PI / 16));
        return table;
    }

    /// <summary>Separable 8×8 inverse DCT (orthonormal: a DC of 8v gives v everywhere).</summary>
    static void InverseDct(int[] input, float[] output)
    {
        Span<float> temp = stackalloc float[64];
        for (int r = 0; r < 8; r++)
            for (int x = 0; x < 8; x++)
            {
                float sum = 0;
                for (int u = 0; u < 8; u++)
                    sum += IdctCos[x * 8 + u] * input[r * 8 + u];
                temp[r * 8 + x] = sum;
            }
        for (int c = 0; c < 8; c++)
            for (int y = 0; y < 8; y++)
            {
                float sum = 0;
                for (int v = 0; v < 8; v++)
                    sum += IdctCos[y * 8 + v] * temp[v * 8 + c];
                output[y * 8 + c] = sum;
            }
    }

    // ------------------------------------------------------------------ tables

    const int EndOfBlock = -2, Escape = -3;

    internal static readonly int[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    static readonly int[] DefaultIntraMatrix =
    [
        8, 16, 19, 22, 26, 27, 29, 34,
        16, 16, 22, 24, 27, 29, 34, 37,
        19, 22, 26, 27, 29, 34, 34, 38,
        22, 22, 26, 27, 29, 34, 37, 40,
        22, 26, 27, 29, 32, 35, 40, 48,
        26, 27, 29, 32, 35, 40, 48, 58,
        26, 27, 29, 34, 38, 46, 56, 69,
        27, 29, 35, 38, 46, 56, 69, 83,
    ];

    /// <summary>Variable-length code tables of ISO/IEC 11172-2 annex B, as binary tries.</summary>
    static class Vlc
    {
        public static readonly VlcTable MacroblockAddressIncrement = new(
        [
            ("1", 1), ("011", 2), ("010", 3), ("0011", 4), ("0010", 5), ("00011", 6), ("00010", 7), ("0000111", 8),
            ("0000110", 9), ("00001011", 10), ("00001010", 11), ("00001001", 12), ("00001000", 13), ("00000111", 14),
            ("00000110", 15), ("0000010111", 16), ("0000010110", 17), ("0000010101", 18), ("0000010100", 19),
            ("0000010011", 20), ("0000010010", 21), ("00000100011", 22), ("00000100010", 23), ("00000100001", 24),
            ("00000100000", 25), ("00000011111", 26), ("00000011110", 27), ("00000011101", 28), ("00000011100", 29),
            ("00000011011", 30), ("00000011010", 31), ("00000011001", 32), ("00000011000", 33),
            ("00000001111", 34), ("00000001000", 35),
        ]);

        // Flags: 0x10 quant, 0x08 forward, 0x04 backward, 0x02 pattern, 0x01 intra.
        public static readonly VlcTable MacroblockTypeI = new([("1", 0x01), ("01", 0x11)]);

        public static readonly VlcTable MacroblockTypeP = new(
        [
            ("1", 0x0A), ("01", 0x02), ("001", 0x08), ("00011", 0x01), ("00010", 0x1A), ("00001", 0x12), ("000001", 0x11),
        ]);

        public static readonly VlcTable MacroblockTypeB = new(
        [
            ("10", 0x0C), ("11", 0x0E), ("010", 0x04), ("011", 0x06), ("0010", 0x08), ("0011", 0x0A), ("00011", 0x01),
            ("00010", 0x1E), ("000011", 0x1A), ("000010", 0x16), ("000001", 0x11),
        ]);

        public static readonly VlcTable CodedBlockPattern = new(
        [
            ("111", 60), ("1101", 4), ("1100", 8), ("1011", 16), ("1010", 32), ("10011", 12), ("10010", 48), ("10001", 20),
            ("10000", 40), ("01111", 28), ("01110", 44), ("01101", 52), ("01100", 56), ("01011", 1), ("01010", 61),
            ("01001", 2), ("01000", 62), ("001111", 24), ("001110", 36), ("001101", 3), ("001100", 63), ("0010111", 5),
            ("0010110", 9), ("0010101", 17), ("0010100", 33), ("0010011", 6), ("0010010", 10), ("0010001", 18),
            ("0010000", 34), ("00011111", 7), ("00011110", 11), ("00011101", 19), ("00011100", 35), ("00011011", 13),
            ("00011010", 49), ("00011001", 21), ("00011000", 41), ("00010111", 14), ("00010110", 50), ("00010101", 22),
            ("00010100", 42), ("00010011", 15), ("00010010", 51), ("00010001", 23), ("00010000", 43), ("00001111", 25),
            ("00001110", 37), ("00001101", 26), ("00001100", 38), ("00001011", 29), ("00001010", 45), ("00001001", 53),
            ("00001000", 57), ("00000111", 30), ("00000110", 46), ("00000101", 54), ("00000100", 58), ("000000111", 31),
            ("000000110", 47), ("000000101", 55), ("000000100", 59), ("000000011", 27), ("000000010", 39),
        ]);

        public static readonly VlcTable MotionCode = BuildMotionCode();

        static VlcTable BuildMotionCode()
        {
            // Magnitude codes; the sign bit follows (1 = negative) except for 0.
            (string, int)[] magnitudes =
            [
                ("01", 1), ("001", 2), ("0001", 3), ("000011", 4), ("0000101", 5), ("0000100", 6), ("0000011", 7),
                ("000001011", 8), ("000001010", 9), ("000001001", 10), ("0000010001", 11), ("0000010000", 12),
                ("0000001111", 13), ("0000001110", 14), ("0000001101", 15), ("0000001100", 16),
            ];
            var codes = new List<(string, int)> { ("1", 0) };
            foreach (var (code, value) in magnitudes)
            {
                codes.Add((code + "0", value));
                codes.Add((code + "1", -value));
            }
            return new VlcTable(codes);
        }

        public static readonly VlcTable DcSizeLuma = new(
        [
            ("100", 0), ("00", 1), ("01", 2), ("101", 3), ("110", 4), ("1110", 5), ("11110", 6), ("111110", 7), ("1111110", 8),
        ]);

        public static readonly VlcTable DcSizeChroma = new(
        [
            ("00", 0), ("01", 1), ("10", 2), ("110", 3), ("1110", 4), ("11110", 5), ("111110", 6), ("1111110", 7), ("11111110", 8),
        ]);

        /// <summary>dct_coeff_next (table B.5c without the sign bit): value = run &lt;&lt; 8 | level, EOB and escape.</summary>
        public static readonly VlcTable DctCoefficient = new(
        [
            ("10", EndOfBlock), ("000001", Escape),
            ("11", R(0, 1)), ("011", R(1, 1)), ("0100", R(0, 2)), ("0101", R(2, 1)), ("00101", R(0, 3)), ("00111", R(3, 1)),
            ("00110", R(4, 1)), ("000110", R(1, 2)), ("000111", R(5, 1)), ("000101", R(6, 1)), ("000100", R(7, 1)),
            ("0000110", R(0, 4)), ("0000100", R(2, 2)), ("0000111", R(8, 1)), ("0000101", R(9, 1)),
            ("00100110", R(0, 5)), ("00100001", R(0, 6)), ("00100101", R(1, 3)), ("00100100", R(3, 2)),
            ("00100111", R(10, 1)), ("00100011", R(11, 1)), ("00100010", R(12, 1)), ("00100000", R(13, 1)),
            ("0000001010", R(0, 7)), ("0000001100", R(1, 4)), ("0000001011", R(2, 3)), ("0000001111", R(4, 2)),
            ("0000001001", R(5, 2)), ("0000001110", R(14, 1)), ("0000001101", R(15, 1)), ("0000001000", R(16, 1)),
            ("000000011101", R(0, 8)), ("000000011000", R(0, 9)), ("000000010011", R(0, 10)), ("000000010000", R(0, 11)),
            ("000000011011", R(1, 5)), ("000000010100", R(2, 4)), ("000000011100", R(3, 3)), ("000000010010", R(4, 3)),
            ("000000011110", R(6, 2)), ("000000010101", R(7, 2)), ("000000010001", R(8, 2)), ("000000011111", R(17, 1)),
            ("000000011010", R(18, 1)), ("000000011001", R(19, 1)), ("000000010111", R(20, 1)), ("000000010110", R(21, 1)),
            ("0000000011010", R(0, 12)), ("0000000011001", R(0, 13)), ("0000000011000", R(0, 14)), ("0000000010111", R(0, 15)),
            ("0000000010110", R(1, 6)), ("0000000010101", R(1, 7)), ("0000000010100", R(2, 5)), ("0000000010011", R(3, 4)),
            ("0000000010010", R(5, 3)), ("0000000010001", R(9, 2)), ("0000000010000", R(10, 2)), ("0000000011111", R(22, 1)),
            ("0000000011110", R(23, 1)), ("0000000011101", R(24, 1)), ("0000000011100", R(25, 1)), ("0000000011011", R(26, 1)),
            ("00000000011111", R(0, 16)), ("00000000011110", R(0, 17)), ("00000000011101", R(0, 18)), ("00000000011100", R(0, 19)),
            ("00000000011011", R(0, 20)), ("00000000011010", R(0, 21)), ("00000000011001", R(0, 22)), ("00000000011000", R(0, 23)),
            ("00000000010111", R(0, 24)), ("00000000010110", R(0, 25)), ("00000000010101", R(0, 26)), ("00000000010100", R(0, 27)),
            ("00000000010011", R(0, 28)), ("00000000010010", R(0, 29)), ("00000000010001", R(0, 30)), ("00000000010000", R(0, 31)),
            ("000000000011000", R(0, 32)), ("000000000010111", R(0, 33)), ("000000000010110", R(0, 34)), ("000000000010101", R(0, 35)),
            ("000000000010100", R(0, 36)), ("000000000010011", R(0, 37)), ("000000000010010", R(0, 38)), ("000000000010001", R(0, 39)),
            ("000000000010000", R(0, 40)), ("000000000011111", R(1, 8)), ("000000000011110", R(1, 9)), ("000000000011101", R(1, 10)),
            ("000000000011100", R(1, 11)), ("000000000011011", R(1, 12)), ("000000000011010", R(1, 13)), ("000000000011001", R(1, 14)),
            ("0000000000010011", R(1, 15)), ("0000000000010010", R(1, 16)), ("0000000000010001", R(1, 17)), ("0000000000010000", R(1, 18)),
            ("0000000000010100", R(6, 3)), ("0000000000011010", R(11, 2)), ("0000000000011001", R(12, 2)), ("0000000000011000", R(13, 2)),
            ("0000000000010111", R(14, 2)), ("0000000000010110", R(15, 2)), ("0000000000010101", R(16, 2)), ("0000000000011111", R(27, 1)),
            ("0000000000011110", R(28, 1)), ("0000000000011101", R(29, 1)), ("0000000000011100", R(30, 1)), ("0000000000011011", R(31, 1)),
        ]);

        static int R(int run, int level) => (run << 8) | level;
    }
}

/// <summary>A prefix code as a binary trie; <see cref="Decode"/> returns the value, or int.MinValue for an invalid code.</summary>
sealed class VlcTable
{
    // node * 2 + bit → child node (positive), leaf (-(index + 1)) or 0 (none).
    readonly List<int> _children = [0, 0];
    readonly List<int> _values = [];

    public VlcTable(IEnumerable<(string Code, int Value)> codes)
    {
        foreach (var (code, value) in codes)
        {
            int node = 0;
            for (int i = 0; i < code.Length; i++)
            {
                int slot = node * 2 + (code[i] - '0');
                if (i == code.Length - 1)
                {
                    if (_children[slot] != 0)
                        throw new InvalidOperationException($"VLC {code} collides.");
                    _values.Add(value);
                    _children[slot] = -_values.Count;
                }
                else
                {
                    if (_children[slot] < 0)
                        throw new InvalidOperationException($"VLC {code} has a shorter code as prefix.");
                    if (_children[slot] == 0)
                    {
                        _children[slot] = _children.Count / 2;
                        _children.Add(0);
                        _children.Add(0);
                    }
                    node = _children[slot];
                }
            }
        }
    }

    public int Decode(MpegBitReader bits)
    {
        int node = 0;
        for (int depth = 0; depth < 17; depth++)
        {
            int next = _children[node * 2 + bits.Read(1)];
            if (next < 0)
                return _values[-next - 1];
            if (next == 0)
                return int.MinValue;
            node = next;
        }
        return int.MinValue;
    }
}

/// <summary>Big-endian bit reader over a forward-only stream, with MPEG start code helpers.</summary>
public sealed class MpegBitReader(Stream stream)
{
    readonly byte[] _buffer = new byte[1 << 16];
    int _length, _bytePos, _bitPos; // _bitPos: 0..7 within _buffer[_bytePos]
    bool _eof;

    bool Ensure(int bytes)
    {
        if (_length - _bytePos >= bytes)
            return true;
        if (_eof)
            return false;
        int remaining = _length - _bytePos;
        Buffer.BlockCopy(_buffer, _bytePos, _buffer, 0, remaining);
        _length = remaining;
        _bytePos = 0;
        while (_length < _buffer.Length)
        {
            int n = stream.Read(_buffer, _length, _buffer.Length - _length);
            if (n <= 0)
            {
                _eof = true;
                break;
            }
            _length += n;
        }
        return _length - _bytePos >= bytes;
    }

    public int Read(int count)
    {
        int value = Peek(count);
        Skip(count);
        return value;
    }

    public int Peek(int count)
    {
        if (count == 0)
            return 0;
        Ensure(5);
        int value = 0;
        int bytePos = _bytePos, bitPos = _bitPos;
        for (int i = 0; i < count; i++)
        {
            int bit = bytePos < _length ? (_buffer[bytePos] >> (7 - bitPos)) & 1 : 0;
            value = (value << 1) | bit;
            if (++bitPos == 8)
            {
                bitPos = 0;
                bytePos++;
            }
        }
        return value;
    }

    void Skip(int count)
    {
        _bitPos += count;
        _bytePos += _bitPos >> 3;
        _bitPos &= 7;
    }

    void AlignToByte()
    {
        if (_bitPos != 0)
        {
            _bitPos = 0;
            _bytePos++;
        }
    }

    /// <summary>True when the next 23 bits are zero (a start code follows; the slice ends).</summary>
    public bool AtStartCode()
    {
        if (!Ensure(4))
            return true;
        return Peek(23) == 0;
    }

    /// <summary>Skips to the next start code and returns its value byte, or null at the end.</summary>
    public int? NextStartCode()
    {
        AlignToByte();
        while (Ensure(4))
        {
            if (_buffer[_bytePos] == 0 && _buffer[_bytePos + 1] == 0 && _buffer[_bytePos + 2] == 1)
            {
                int code = _buffer[_bytePos + 3];
                _bytePos += 4;
                return code;
            }
            _bytePos++;
        }
        return null;
    }

    /// <summary>The value of the next start code without consuming it (skipping to it).</summary>
    public int? PeekStartCode()
    {
        AlignToByte();
        while (Ensure(4))
        {
            if (_buffer[_bytePos] == 0 && _buffer[_bytePos + 1] == 0 && _buffer[_bytePos + 2] == 1)
                return _buffer[_bytePos + 3];
            _bytePos++;
        }
        return null;
    }
}
