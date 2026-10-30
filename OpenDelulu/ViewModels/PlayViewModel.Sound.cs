using OpenDelulu.Engine.Audio;

namespace OpenDelulu.ViewModels;

/// <summary>
/// The screens' sound effects (<see cref="SoundEffects"/>): picks, decisions, cancels and the in-game menus' clicks,
/// and the H scenes' own sounds. CONFIG's F/X volume and mute apply (the effects bus).
/// </summary>
public sealed partial class PlayViewModel
{
    bool _sfxHooked;

    void Sfx(int number) => AudioSystem.Shared.Play(_fs, number);

    /// <summary>Hooks the in-game menus' click sound (called once the game starts).</summary>
    void HookMenuSounds()
    {
        if (_sfxHooked)
            return;
        _sfxHooked = true;
        DsHud.AnyPressed += () => Sfx(SoundEffects.Pick);
    }
}
