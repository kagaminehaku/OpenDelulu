using NVorbis;
using OpenDelulu.Formats.Audio;

namespace OpenDelulu.Engine.Audio;

/// <summary>A stream of interleaved float samples in [-1, 1].</summary>
public interface IAudioSource : IDisposable
{
    int Channels { get; }
    int SampleRate { get; }
    /// <summary>Total length, if known.</summary>
    TimeSpan? Duration { get; }

    /// <summary>Reads up to <paramref name="frames"/> frames; returns the frames read (0 at the end).</summary>
    int Read(float[] buffer, int offset, int frames);

    /// <summary>Goes back to the start (for looping).</summary>
    void Rewind();
}

/// <summary>Decoded PCM (the game's .wav voices).</summary>
public sealed class PcmSource(WavFile wav) : IAudioSource
{
    int _frame;

    public int Channels => wav.Channels;
    public int SampleRate => wav.SampleRate;
    public TimeSpan? Duration => TimeSpan.FromSeconds(wav.Duration);

    public int Read(float[] buffer, int offset, int frames)
    {
        int count = Math.Min(frames, wav.Frames - _frame);
        if (count <= 0)
            return 0;
        var samples = wav.Samples.AsSpan(_frame * Channels, count * Channels);
        for (int i = 0; i < samples.Length; i++)
            buffer[offset + i] = samples[i] * (1f / 32768f);
        _frame += count;
        return count;
    }

    public void Rewind() => _frame = 0;

    public void Dispose()
    {
    }
}

/// <summary>Ogg Vorbis, decoded while playing (music tracks are several minutes long).</summary>
public sealed class VorbisSource : IAudioSource
{
    readonly VorbisReader _reader;

    public VorbisSource(byte[] data)
    {
        _reader = new VorbisReader(new MemoryStream(data, writable: false), closeOnDispose: true);
    }

    public int Channels => _reader.Channels;
    public int SampleRate => _reader.SampleRate;
    public TimeSpan? Duration => _reader.TotalTime;

    public int Read(float[] buffer, int offset, int frames) => _reader.ReadSamples(buffer, offset, frames * Channels) / Channels;

    public void Rewind() => _reader.SamplePosition = 0;

    public void Dispose() => _reader.Dispose();
}

public static class AudioSources
{
    public static bool IsAudio(string fileName) =>
        fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens a .wav or .ogg by content (both kinds of voice share names apart from the extension).</summary>
    public static IAudioSource Open(byte[] data) =>
        data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual("OggS"u8) ? new VorbisSource(data) : new PcmSource(WavFile.Read(data));
}
