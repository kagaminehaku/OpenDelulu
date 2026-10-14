namespace OpenDelulu.Engine.Audio;

/// <summary>Volume groups, like the game's config (BGM, ambience, effects, voices).</summary>
public enum AudioBus
{
    Music,
    Ambient,
    Effects,
    Voice,
}

/// <summary>A playing sound. Properties are safe to read from any thread.</summary>
public sealed class SoundHandle
{
    internal SoundHandle(AudioBus bus, TimeSpan? duration)
    {
        Bus = bus;
        Duration = duration;
    }

    public AudioBus Bus { get; }
    public TimeSpan? Duration { get; }

    volatile bool _playing = true;
    public bool IsPlaying { get => _playing; internal set => _playing = value; }

    double _position;
    /// <summary>Seconds into the source (wraps when looping); used to sync lips with .vss timings.</summary>
    public double Position { get => Volatile.Read(ref _position); internal set => Volatile.Write(ref _position, value); }

    float _level;
    /// <summary>Peak level of the last mixed block (0..1), for lip sync without a .vss.</summary>
    public float Level { get => Volatile.Read(ref _level); internal set => Volatile.Write(ref _level, value); }

    internal volatile float Volume = 1;
    internal volatile float FadeSeconds = -1; // >= 0: fade out over this time, then stop

    public void SetVolume(float volume) => Volume = Math.Clamp(volume, 0, 1);

    /// <summary>Stops now, or after fading out.</summary>
    public void Stop(double fadeSeconds = 0) => FadeSeconds = (float)Math.Max(0, fadeSeconds);
}

/// <summary>
/// Software mixer: any number of sources at any rate/channel count, resampled (linear) to 44.1 kHz stereo.
/// Platform outputs only pull blocks from <see cref="Render"/>, so the same mixer runs everywhere.
/// </summary>
public sealed class AudioMixer
{
    public const int OutputRate = 44100;
    public const int OutputChannels = 2;

    readonly Lock _lock = new();
    readonly List<Voice> _voices = [];
    readonly float[] _busVolume = [0.8f, 0.8f, 1f, 1f];

    public float MasterVolume { get; set; } = 1f;

    public float GetBusVolume(AudioBus bus) => _busVolume[(int)bus];
    public void SetBusVolume(AudioBus bus, float volume) => _busVolume[(int)bus] = Math.Clamp(volume, 0, 1);

    /// <summary>Starts a source; the mixer owns and disposes it.</summary>
    public SoundHandle Play(IAudioSource source, AudioBus bus, bool loop = false, float volume = 1, double fadeInSeconds = 0)
    {
        var handle = new SoundHandle(bus, source.Duration) { Volume = Math.Clamp(volume, 0, 1) };
        var voice = new Voice(source, handle, loop, fadeInSeconds);
        lock (_lock)
            _voices.Add(voice);
        return handle;
    }

    public void StopBus(AudioBus bus, double fadeSeconds = 0)
    {
        lock (_lock)
            foreach (var voice in _voices)
                if (voice.Handle.Bus == bus)
                    voice.Handle.Stop(fadeSeconds);
    }

    public void StopAll()
    {
        lock (_lock)
            foreach (var voice in _voices)
                voice.Handle.Stop();
    }

    public int ActiveCount
    {
        get { lock (_lock) return _voices.Count; }
    }

    /// <summary>Mixes <paramref name="frames"/> interleaved stereo frames into <paramref name="output"/> (overwrites).</summary>
    public void Render(float[] output, int frames)
    {
        Array.Clear(output, 0, frames * OutputChannels);
        lock (_lock)
        {
            for (int i = _voices.Count - 1; i >= 0; i--)
            {
                var voice = _voices[i];
                float gain = MasterVolume * _busVolume[(int)voice.Handle.Bus];
                if (!voice.Mix(output, frames, gain))
                {
                    voice.Handle.IsPlaying = false;
                    voice.Handle.Level = 0;
                    voice.Source.Dispose();
                    _voices.RemoveAt(i);
                }
            }
        }
        for (int i = 0; i < frames * OutputChannels; i++)
            output[i] = Math.Clamp(output[i], -1f, 1f);
    }

    sealed class Voice
    {
        const int ChunkFrames = 1024;

        public readonly IAudioSource Source;
        public readonly SoundHandle Handle;
        readonly bool _loop;
        readonly int _channels;
        readonly double _step;
        readonly float[] _buffer;
        int _bufferFrames;
        double _cursor;       // fractional frame index into _buffer
        long _bufferStart;    // source frame of _buffer[0]
        bool _ended;
        float _fade;          // current fade gain
        readonly float _fadeInStep;

        public Voice(IAudioSource source, SoundHandle handle, bool loop, double fadeIn)
        {
            Source = source;
            Handle = handle;
            _loop = loop;
            _channels = Math.Max(1, source.Channels);
            _step = (double)source.SampleRate / OutputRate;
            _buffer = new float[(ChunkFrames + 2) * _channels];
            _fade = fadeIn > 0 ? 0 : 1;
            _fadeInStep = fadeIn > 0 ? (float)(1 / (fadeIn * OutputRate)) : 1;
        }

        /// <summary>Adds this voice into the output; false when it has finished.</summary>
        public bool Mix(float[] output, int frames, float busGain)
        {
            float fadeOut = Handle.FadeSeconds;
            float fadeOutStep = fadeOut < 0 ? 0 : fadeOut == 0 ? 1 : 1 / (fadeOut * OutputRate);
            float peak = 0;
            for (int f = 0; f < frames; f++)
            {
                if (!Ensure())
                {
                    Handle.Level = peak;
                    return false;
                }
                int index = (int)_cursor;
                float t = (float)(_cursor - index);
                int a = index * _channels, b = index + 1 < _bufferFrames ? a + _channels : a;
                float left = _buffer[a] + (_buffer[b] - _buffer[a]) * t;
                float right = _channels > 1 ? _buffer[a + 1] + (_buffer[b + 1] - _buffer[a + 1]) * t : left;

                if (fadeOutStep > 0)
                {
                    _fade -= fadeOutStep;
                    if (_fade <= 0)
                    {
                        Handle.Level = peak;
                        return false;
                    }
                }
                else if (_fade < 1)
                    _fade = Math.Min(1, _fade + _fadeInStep);

                float gain = busGain * Handle.Volume * _fade;
                output[f * 2] += left * gain;
                output[f * 2 + 1] += right * gain;
                peak = Math.Max(peak, Math.Max(Math.Abs(left), Math.Abs(right)) * _fade * Handle.Volume);
                _cursor += _step;
            }
            Handle.Level = peak;
            Handle.Position = (_bufferStart + _cursor) / Source.SampleRate;
            return true;
        }

        /// <summary>Makes the frame under the cursor (and the next one, unless at the end) available.</summary>
        bool Ensure()
        {
            while ((int)_cursor + 1 >= _bufferFrames && !_ended)
                Refill();
            return (int)_cursor < _bufferFrames;
        }

        void Refill()
        {
            // Keep the frame under the cursor (needed for interpolation), read after it.
            int from = Math.Min((int)_cursor, _bufferFrames);
            int keep = _bufferFrames - from;
            if (keep > 0)
                Array.Copy(_buffer, from * _channels, _buffer, 0, keep * _channels);
            _bufferStart += from;
            _cursor -= from;
            _bufferFrames = keep;

            int read = Source.Read(_buffer, _bufferFrames * _channels, ChunkFrames + 2 - _bufferFrames);
            if (read == 0 && _loop)
            {
                Source.Rewind();
                _bufferStart = -_bufferFrames; // positions restart at the loop point
                read = Source.Read(_buffer, _bufferFrames * _channels, ChunkFrames + 2 - _bufferFrames);
            }
            if (read == 0)
                _ended = true;
            _bufferFrames += read;
        }
    }
}
