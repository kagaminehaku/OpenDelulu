using System;
using AudioToolbox;
using AVFoundation;
using OpenDelulu.Engine.Audio;

namespace OpenDelulu.iOS;

/// <summary>Plays the mixer through an AudioQueue (16-bit PCM, 44.1 kHz stereo, three rotating buffers).</summary>
sealed unsafe class IosAudioOutput : IAudioOutput
{
    const int BufferCount = 3, BlockFrames = 1024;
    const int BlockBytes = BlockFrames * AudioMixer.OutputChannels * sizeof(short);

    OutputAudioQueue? _queue;
    AudioMixer? _mixer;
    readonly float[] _mix = new float[BlockFrames * AudioMixer.OutputChannels];

    public string Name => "iOS AudioQueue";

    public void Start(AudioMixer mixer)
    {
        _mixer = mixer;
        var session = AVAudioSession.SharedInstance();
        session.SetCategory(AVAudioSessionCategory.Playback);
        session.SetActive(true, out _);

        var format = AudioStreamBasicDescription.CreateLinearPCM(AudioMixer.OutputRate, AudioMixer.OutputChannels, 16, false);
        _queue = new OutputAudioQueue(format);
        _queue.BufferCompleted += (_, e) => Fill(e.IntPtrBuffer);
        for (int i = 0; i < BufferCount; i++)
        {
            _queue.AllocateBuffer(BlockBytes, out IntPtr buffer);
            Fill(buffer);
        }
        if (_queue.Start() != AudioQueueStatus.Ok)
            throw new InvalidOperationException("AudioQueue could not start.");
    }

    void Fill(IntPtr buffer)
    {
        if (_queue is null || _mixer is null)
            return;
        _mixer.Render(_mix, BlockFrames);
        var header = (AudioQueueBuffer*)buffer;
        var pcm = new Span<short>((void*)header->AudioData, _mix.Length);
        for (int i = 0; i < _mix.Length; i++)
            pcm[i] = (short)(_mix[i] * 32767f);
        header->AudioDataByteSize = BlockBytes;
        _queue.EnqueueBuffer(buffer, BlockBytes, null);
    }

    public void Dispose()
    {
        _queue?.Stop(true);
        _queue?.Dispose();
        _queue = null;
    }
}
