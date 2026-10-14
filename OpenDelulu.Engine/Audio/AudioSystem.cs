namespace OpenDelulu.Engine.Audio;

/// <summary>
/// The app-wide mixer plus the platform output. Platform heads set <see cref="OutputFactory"/> before first use
/// (Android: AudioTrack, iOS: AudioQueue); Windows uses waveOut; elsewhere sound is silent but still timed.
/// </summary>
public sealed class AudioSystem : IDisposable
{
    static readonly Lazy<AudioSystem> SharedInstance = new(() => new AudioSystem(CreateOutput()));

    /// <summary>Creates the device output; null means "use the default for this OS".</summary>
    public static Func<IAudioOutput>? OutputFactory { get; set; }

    public static AudioSystem Shared => SharedInstance.Value;

    readonly IAudioOutput _output;
    SoundHandle? _music;
    SoundHandle? _ambient;
    (string Archive, string File)? _musicName;

    AudioSystem(IAudioOutput output)
    {
        Mixer = new AudioMixer();
        try
        {
            output.Start(Mixer);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            output.Dispose();
            OutputError = ex.Message;
            output = new NullAudioOutput();
            output.Start(Mixer);
        }
        _output = output;
    }

    public AudioMixer Mixer { get; }
    public string OutputName => _output.Name;
    /// <summary>Why the platform output could not start (sound then plays silently), or null.</summary>
    public string? OutputError { get; }

    static IAudioOutput CreateOutput()
    {
        if (OutputFactory is { } factory)
            return factory();
        if (OperatingSystem.IsWindows())
            return new WinMmAudioOutput();
        return new NullAudioOutput();
    }

    /// <summary>Plays a .wav/.ogg once (or looping) on a bus.</summary>
    public SoundHandle Play(byte[] data, AudioBus bus, bool loop = false, float volume = 1) =>
        Mixer.Play(AudioSources.Open(data), bus, loop, volume);

    public SoundHandle Play(GameFileSystem fs, string archive, string file, AudioBus bus, bool loop = false, float volume = 1) =>
        Play(fs.ReadReferenced(archive, file), bus, loop, volume);

    /// <summary>Switches the looping background music with a cross-fade; the same track keeps playing.</summary>
    public SoundHandle PlayMusic(GameFileSystem fs, string archive, string file, double fadeSeconds = 1)
    {
        if (_music is { IsPlaying: true } current && _musicName == (archive, file))
            return current;
        _music?.Stop(fadeSeconds);
        _music = Mixer.Play(AudioSources.Open(fs.ReadReferenced(archive, file)), AudioBus.Music, loop: true, fadeInSeconds: fadeSeconds);
        _musicName = (archive, file);
        return _music;
    }

    public void StopMusic(double fadeSeconds = 1)
    {
        _music?.Stop(fadeSeconds);
        _music = null;
        _musicName = null;
    }

    /// <summary>Looping ambience (waves, wind…), one at a time.</summary>
    public SoundHandle PlayAmbient(GameFileSystem fs, string archive, string file, double fadeSeconds = 1)
    {
        _ambient?.Stop(fadeSeconds);
        _ambient = Mixer.Play(AudioSources.Open(fs.ReadReferenced(archive, file)), AudioBus.Ambient, loop: true, fadeInSeconds: fadeSeconds);
        return _ambient;
    }

    public void Dispose()
    {
        Mixer.StopAll();
        _output.Dispose();
    }
}
