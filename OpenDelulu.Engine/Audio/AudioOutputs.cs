using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenDelulu.Engine.Audio;

/// <summary>A platform sound device that keeps pulling 44.1 kHz stereo float blocks from the mixer.</summary>
public interface IAudioOutput : IDisposable
{
    string Name { get; }
    void Start(AudioMixer mixer);
}

/// <summary>
/// No device: still renders in real time so sounds finish and lip sync advances (used where no backend exists yet).
/// </summary>
public sealed class NullAudioOutput : IAudioOutput
{
    Thread? _thread;
    volatile bool _running;

    public string Name => "không có thiết bị âm thanh";

    public void Start(AudioMixer mixer)
    {
        _running = true;
        _thread = new Thread(() =>
        {
            const int frames = 1024;
            var buffer = new float[frames * AudioMixer.OutputChannels];
            var clock = Stopwatch.StartNew();
            long rendered = 0;
            while (_running)
            {
                long due = (long)(clock.Elapsed.TotalSeconds * AudioMixer.OutputRate);
                while (rendered + frames <= due)
                {
                    mixer.Render(buffer, frames);
                    rendered += frames;
                }
                Thread.Sleep(10);
            }
        }) { IsBackground = true, Name = "Audio (null)" };
        _thread.Start();
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(200);
    }
}

/// <summary>Windows waveOut (winmm): always available, ~90 ms latency, 16-bit PCM.</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WinMmAudioOutput : IAudioOutput
{
    const int BufferCount = 4, BufferFrames = 1024;
    const uint WaveMapper = 0xFFFFFFFF, CallbackEvent = 0x00050000, WhdrDone = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    struct WaveFormatEx
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveHeader
    {
        public nint Data;
        public uint BufferLength, BytesRecorded;
        public nint User;
        public uint Flags, Loops;
        public nint Next, Reserved;
    }

    [DllImport("winmm.dll")] static extern int waveOutOpen(out nint handle, uint device, ref WaveFormatEx format, nint callback, nint instance, uint flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(nint handle, WaveHeader* header, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(nint handle, WaveHeader* header, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(nint handle, WaveHeader* header, int size);
    [DllImport("winmm.dll")] static extern int waveOutReset(nint handle);
    [DllImport("winmm.dll")] static extern int waveOutClose(nint handle);

    readonly AutoResetEvent _event = new(false);
    nint _device;
    WaveHeader* _headers;
    Thread? _thread;
    volatile bool _running;

    public string Name => "Windows waveOut";

    public void Start(AudioMixer mixer)
    {
        var format = new WaveFormatEx
        {
            FormatTag = 1, // PCM
            Channels = AudioMixer.OutputChannels,
            SamplesPerSec = AudioMixer.OutputRate,
            BitsPerSample = 16,
            BlockAlign = AudioMixer.OutputChannels * 2,
            AvgBytesPerSec = AudioMixer.OutputRate * AudioMixer.OutputChannels * 2,
        };
        int result = waveOutOpen(out _device, WaveMapper, ref format, _event.SafeWaitHandle.DangerousGetHandle(), 0, CallbackEvent);
        if (result != 0)
            throw new InvalidOperationException($"waveOutOpen failed ({result}).");

        int bytes = BufferFrames * AudioMixer.OutputChannels * 2;
        _headers = (WaveHeader*)NativeMemory.AllocZeroed((nuint)(sizeof(WaveHeader) * BufferCount));
        for (int i = 0; i < BufferCount; i++)
        {
            _headers[i].Data = (nint)NativeMemory.AllocZeroed((nuint)bytes);
            _headers[i].BufferLength = (uint)bytes;
            waveOutPrepareHeader(_device, &_headers[i], sizeof(WaveHeader));
            _headers[i].Flags |= WhdrDone; // free to fill
        }

        _running = true;
        _thread = new Thread(() => Pump(mixer)) { IsBackground = true, Name = "Audio (waveOut)", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    void Pump(AudioMixer mixer)
    {
        var mix = new float[BufferFrames * AudioMixer.OutputChannels];
        while (_running)
        {
            for (int i = 0; i < BufferCount && _running; i++)
            {
                if ((_headers[i].Flags & WhdrDone) == 0)
                    continue;
                mixer.Render(mix, BufferFrames);
                var pcm = new Span<short>((void*)_headers[i].Data, mix.Length);
                for (int s = 0; s < mix.Length; s++)
                    pcm[s] = (short)(mix[s] * 32767f);
                _headers[i].Flags &= ~WhdrDone;
                waveOutWrite(_device, &_headers[i], sizeof(WaveHeader));
            }
            _event.WaitOne(50);
        }
    }

    public void Dispose()
    {
        _running = false;
        _event.Set();
        _thread?.Join(500);
        if (_device != 0)
        {
            waveOutReset(_device);
            for (int i = 0; i < BufferCount; i++)
            {
                waveOutUnprepareHeader(_device, &_headers[i], sizeof(WaveHeader));
                NativeMemory.Free((void*)_headers[i].Data);
            }
            NativeMemory.Free(_headers);
            waveOutClose(_device);
            _device = 0;
        }
        _event.Dispose();
    }
}
