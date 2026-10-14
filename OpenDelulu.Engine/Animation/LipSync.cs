using OpenDelulu.Engine.Audio;
using OpenDelulu.Formats.Voice;

namespace OpenDelulu.Engine.Animation;

/// <summary>
/// Opens the mouth while a voice plays. With a .vss the vowel of the current mora sets how wide; the live
/// level of the sound gates it so the mouth closes in pauses (the .vss has no per-mora timing). Voices without
/// a .vss (event .ogg lines) follow the level alone.
/// </summary>
public sealed class LipSync(SoundHandle voice, VssFile? vss)
{
    const float OpenSpeed = 18f, CloseSpeed = 10f;
    // The level is the peak of one 23 ms mixer block; an envelope that decays over ~0.3 s bridges the gaps
    // between syllables so the mouth does not snap shut mid-word.
    const float EnvelopeDecay = 3f, Sensitivity = 6f, Silence = 0.015f;
    float _mouth, _envelope;

    public SoundHandle Voice { get; } = voice;
    public VssFile? Script { get; } = vss;

    /// <summary>False once the voice has finished and the mouth is closed again.</summary>
    public bool IsActive => Voice.IsPlaying || _mouth > 0.01f;

    /// <summary>Advances the smoothing and returns the mouth openness (0..1).</summary>
    public float Update(double seconds)
    {
        float target = 0;
        _envelope = Math.Max(Voice.IsPlaying ? Voice.Level : 0, _envelope - EnvelopeDecay * (float)seconds);
        if (Voice.IsPlaying && _envelope > Silence)
        {
            float level = Math.Clamp((_envelope - Silence) * Sensitivity, 0, 1);
            target = Script is { } script
                ? VssFile.Openness(script.ShapeAt(Voice.Position * 1000)) * (0.4f + 0.6f * level)
                : level;
        }
        float speed = target > _mouth ? OpenSpeed : CloseSpeed;
        _mouth += (target - _mouth) * Math.Min(1f, (float)seconds * speed);
        return _mouth;
    }
}
