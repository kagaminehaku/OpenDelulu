using System;
using System.Linq;
using OpenDelulu.Engine.Characters;

namespace OpenDelulu.ViewModels;

/// <summary>
/// The swimsuit's wet stain (exe: sb_SukeBase): the oil and H scenes raise the crotch area with her arousal, and the
/// stain sk01 is stamped on the suit texture there (<see cref="SwimsuitStain"/>). It stays while she wears the suit.
/// The exe's target is her arousal gauge (character +0xD4: min(counter +0x54, max) / max; the counter grows by frame time
/// × lg00 row 21 column 5 or 6 during H actions, full at column 0 × column 1: <see cref="Engine.H.HSession.Arousal"/>);
/// in the oil scene the oil's coverage stands for it (what the oil scene adds to the counter is not decoded).
/// </summary>
public sealed partial class PlayViewModel
{
    SwimsuitStain? _stain;
    Character? _stainGirl;
    float _wetness, _wetnessShown;

    /// <summary>
    /// The stain dries (exe 0x413430: wetness = max(target, wetness − ms × rate), the rate set by 0x4A9B20 to lg00 row 21
    /// column 17 × 0.00001 in H (0.4: a full stain dries in 250 s) and row 11 column 20 × 0.00001 in the oil scene (0.0075:
    /// it hardly dries there). The exe keeps the stain inside those scenes; here it dries out of them at the H rate.
    /// </summary>
    readonly Avalonia.Threading.DispatcherTimer _dryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _dryHooked;

    /// <summary>lg00 row 21 column 17 (0.4) × 0.00001 per ms.</summary>
    const float DryPerSecond = 0.4f * 0.00001f * 1000;

    void Dry()
    {
        if (_oil is not null || _couple is not null || _wetness <= 0)
            return;
        _wetness = Math.Max(0, _wetness - DryPerSecond);
        if (Math.Abs(_wetness - _wetnessShown) >= 0.05f || _wetness == 0 && _wetnessShown > 0)
            ShowStain();
    }

    /// <summary>Raises the crotch's wetness to <paramref name="level"/> (it dries later) and updates the texture.</summary>
    void Wet(double level)
    {
        if (!_dryHooked)
        {
            _dryHooked = true;
            _dryTimer.Tick += (_, _) => Dry();
            _dryTimer.Start();
        }
        float wanted = Math.Max(_wetness, (float)Math.Clamp(level, 0, 1));
        if (!ReferenceEquals(_stainGirl, _girl))
        {
            _stainGirl = _girl;
            _stain = _girl is null ? null : SwimsuitStain.Load(_fs, _assembler.Database.TableArchive, _girl);
            _wetness = _wetnessShown = 0;
            wanted = (float)Math.Clamp(level, 0, 1);
        }
        _wetness = wanted;
        // The texture is rebuilt in steps (a 512×512 copy each time).
        if (Math.Abs(_wetness - _wetnessShown) >= 0.05f)
            ShowStain();
    }

    /// <summary>Stamps the current wetness on the suit of the scene's girl (also after the scene is rebuilt).</summary>
    void ShowStain()
    {
        if (_stain is null || _girl is not { } girl || Scene is not { } scene || !ReferenceEquals(_stainGirl, girl))
            return;
        bool wasWet = _wetnessShown > 0;
        _stain[SwimsuitStain.Crotch] = _wetness;
        _wetnessShown = _wetness;
        if (_wetness <= 0 && !wasWet)
            return;
        foreach (var image in girl.SwimsuitImages)
            scene.ReplaceTexture(1, image, _wetness <= 0 ? image : _stain.Apply(image));
        if (_stain.IsSeeThrough)
        {
            // See-through areas: the suit's meshes go with the blended ones (drawn over her skin, alpha from the texture).
            var suitTextures = girl.SwimsuitImages.Select(image => girl.Model.Textures.ToList().IndexOf(image)).ToHashSet();
            scene.SetObjectOpacity(1, m => suitTextures.Contains(m.TextureIndex), 0.999f);
        }
    }
}
