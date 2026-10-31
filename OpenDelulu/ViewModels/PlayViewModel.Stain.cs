using System;
using System.Linq;
using OpenDelulu.Engine.Characters;

namespace OpenDelulu.ViewModels;

/// <summary>
/// The swimsuit's wet stain (exe: sb_SukeBase): the oil and H scenes raise the crotch area with her arousal, and the
/// stain sk01 is stamped on the suit texture there (<see cref="SwimsuitStain"/>). It stays while she wears the suit.
/// What the exe's arousal counter adds up is not decoded: here the H scene's excitement and the oil's coverage.
/// </summary>
public sealed partial class PlayViewModel
{
    SwimsuitStain? _stain;
    Character? _stainGirl;
    float _wetness, _wetnessShown;

    /// <summary>
    /// Out of the oil and H scenes the stain dries (exe 0x413430: wetness = max(target, wetness − time × rate); the rate's
    /// value is not decoded: here a full stain dries in about two minutes).
    /// </summary>
    readonly Avalonia.Threading.DispatcherTimer _dryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _dryHooked;

    void Dry()
    {
        if (_oil is not null || _couple is not null || _wetness <= 0)
            return;
        _wetness = Math.Max(0, _wetness - 1f / 120);
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
