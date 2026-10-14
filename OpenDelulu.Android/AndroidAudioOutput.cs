using System;
using System.Threading;
using Android.Media;
using OpenDelulu.Engine.Audio;

namespace OpenDelulu.Android;

/// <summary>Streams the mixer to an AudioTrack (float PCM, 44.1 kHz stereo) from a background thread.</summary>
sealed class AndroidAudioOutput : IAudioOutput
{
    const int BlockFrames = 1024;
    AudioTrack? _track;
    Thread? _thread;
    volatile bool _running;

    public string Name => "Android AudioTrack";

    public void Start(AudioMixer mixer)
    {
        int minimum = AudioTrack.GetMinBufferSize(AudioMixer.OutputRate, ChannelOut.Stereo, Encoding.PcmFloat);
        _track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Game)!
                .SetContentType(AudioContentType.Music)!
                .Build()!)
            .SetAudioFormat(new AudioFormat.Builder()
                .SetSampleRate(AudioMixer.OutputRate)!
                .SetEncoding(Encoding.PcmFloat)!
                .SetChannelMask(ChannelOut.Stereo)!
                .Build()!)
            .SetBufferSizeInBytes(Math.Max(minimum, BlockFrames * AudioMixer.OutputChannels * sizeof(float) * 3))
            .SetTransferMode(AudioTrackMode.Stream)
            .Build();
        _track.Play();

        _running = true;
        _thread = new Thread(() =>
        {
            var block = new float[BlockFrames * AudioMixer.OutputChannels];
            while (_running)
            {
                mixer.Render(block, BlockFrames);
                _track.Write(block, 0, block.Length, WriteMode.Blocking);
            }
        }) { IsBackground = true, Name = "Audio (AudioTrack)" };
        _thread.Start();
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(500);
        _track?.Stop();
        _track?.Release();
        _track = null;
    }
}
