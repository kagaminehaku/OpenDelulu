using System.Collections.Concurrent;
using NLayer;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Formats.Video;

namespace OpenDelulu.Engine.Video;

/// <summary>
/// MPEG-1 Layer II audio of a movie (NLayer), from its own pass over the program stream. NLayer wants a seekable
/// stream, so the (small) audio stream is collected in memory first.
/// </summary>
public sealed class MpegAudioSource : IAudioSource
{
    readonly MpegFile _file;

    public MpegAudioSource(Stream programStream)
    {
        var audio = new MemoryStream();
        using (var demuxer = MpegProgramStream.Audio(programStream))
            demuxer.CopyTo(audio);
        if (audio.Length == 0)
            throw new InvalidDataException("The movie has no audio stream.");
        audio.Position = 0;
        _file = new MpegFile(audio);
    }

    public int Channels => _file.Channels;
    public int SampleRate => _file.SampleRate;
    public TimeSpan? Duration => null;

    public int Read(float[] buffer, int offset, int frames)
    {
        int wanted = frames * Channels;
        int read = _file.ReadSamples(buffer, offset, wanted);
        return read / Math.Max(1, Channels);
    }

    public void Rewind()
    {
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>
/// Plays one of the game's .mpg movies (logo, opening, the stage introductions of dates): the video is decoded on a
/// worker thread a few frames ahead; the sound goes through the shared mixer. Call <see cref="FrameAt"/> from the UI
/// with the time since the start.
/// </summary>
public sealed class MoviePlayer : IDisposable
{
    readonly BlockingCollection<(int Number, byte[] Rgba)> _frames = new(boundedCapacity: 3);
    readonly CancellationTokenSource _stop = new();
    readonly Thread _thread;
    (int Number, byte[] Rgba)? _next;
    volatile bool _decoded;

    public int Width { get; }
    public int Height { get; }
    public double FrameRate { get; }
    public SoundHandle? Sound { get; private set; }

    /// <summary>True once every frame was shown.</summary>
    public bool Finished => _decoded && _next is null && _frames.Count == 0;

    MoviePlayer(Mpeg1Video video)
    {
        Width = video.Width;
        Height = video.Height;
        FrameRate = video.FrameRate > 1 ? video.FrameRate : 30;
        _thread = new Thread(() => Decode(video)) { IsBackground = true, Name = "Movie decoder" };
        _thread.Start();
    }

    /// <summary>Opens a movie from the game folder (data/sb3_00NN.mpg of the newest edition that has it); null if missing.</summary>
    public static MoviePlayer? Open(GameInstall install, string fileName, AudioSystem? audio)
    {
        foreach (var edition in install.Editions.Reverse())
        {
            string path = edition.DataFolder.TrimEnd('/', '\\') + "/" + fileName;
            if (!install.Storage.FileExists(path))
                continue;
            var video = new Mpeg1Video(MpegProgramStream.Video(install.Storage.OpenRead(path)));
            var player = new MoviePlayer(video);
            if (audio is not null)
                player.StartSound(audio, () => install.Storage.OpenRead(path));
            return player;
        }
        return null;
    }

    /// <summary>Collects and starts the sound on a worker thread (the opening movie is 180 MB to read through).</summary>
    void StartSound(AudioSystem audio, Func<Stream> open)
    {
        Task.Run(() =>
        {
            try
            {
                var source = new MpegAudioSource(open());
                if (_stop.IsCancellationRequested)
                {
                    source.Dispose();
                    return;
                }
                Sound = audio.Mixer.Play(source, AudioBus.Music);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException
                or NotSupportedException or ObjectDisposedException or IndexOutOfRangeException)
            {
                // A movie without sound still plays.
            }
        });
    }

    void Decode(Mpeg1Video video)
    {
        try
        {
            while (!_stop.IsCancellationRequested && video.NextFrame() is { } frame)
            {
                var rgba = new byte[Width * Height * 4];
                frame.ToRgba(rgba, Width * 4, Width, Height);
                _frames.Add((frame.Number, rgba), _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidDataException or IOException or IndexOutOfRangeException)
        {
            // Stopped, or a damaged stream: end the movie here.
        }
        finally
        {
            _decoded = true;
        }
    }

    /// <summary>The newest frame due at <paramref name="seconds"/> (RGBA, <see cref="Width"/> × <see cref="Height"/>), or null when none is new.</summary>
    public byte[]? FrameAt(double seconds)
    {
        int due = (int)(seconds * FrameRate);
        byte[]? result = null;
        while (true)
        {
            if (_next is null)
            {
                if (!_frames.TryTake(out var taken))
                    break;
                _next = taken;
            }
            if (_next.Value.Number > due)
                break;
            result = _next.Value.Rgba;
            _next = null;
        }
        return result;
    }

    public void Dispose()
    {
        _stop.Cancel();
        Sound?.Stop(0.2);
        _thread.Join(500);
        _frames.Dispose();
        _stop.Dispose();
    }
}
