namespace OpenDelulu.Engine.Audio;

/// <summary>
/// The sound effects (ld00 row 73: sb3_0600.pp se%02d; se36..38 of the Plus edition in sb3x_1600.pp). The exe loads
/// them into 38 slots of sb_SoundData and a scene asks for one by writing its slot (number − 1) to the sound object
/// (+0x28), played on the next update. Uses found in the exe:
/// <list type="bullet">
/// <item>Screens (ticket, lobby, map, memories, status, date menus): se01 cursor / pick, se02, se03 decide, se04 open a
/// page, se05 (date menus), se06 cancel / back, se08 (map).</item>
/// <item>H (sb_HScene 0x4B3E90, by the girl's clip): insert and the way to う！待機 se13 at three quarters of the clip; a
/// push stroke se13 when it starts; the pull back se17–19 (wet: se20–22) at its middle; ぐちゅぐちゅ / グラインド loops
/// se23–25 (wet: se26–28) at their middle; the climax inside se14 every 10 frames for 7.5 % of the clip; coming outside
/// se15; her squirt (0x4B2EF0, from her third own climax of the scene) se16. Which flag makes it "wet" is not decoded (here: excitement past half).</item>
/// </list>
/// </summary>
public static class SoundEffects
{
    public const int Pick = 1, Decide = 3, Open = 4, DateMenu = 5, Cancel = 6;
    public const int Push = 13, Climax = 14, Outside = 15, Squirt = 16;

    public static string File(int number) => $"se{number:00}_00_00_00_00.ogg";

    /// <summary>Plays se<paramref name="number"/> on the effects bus (nothing when it is not installed).</summary>
    public static void Play(this AudioSystem audio, GameFileSystem fs, int number)
    {
        foreach (string archive in new[] { "sb3_0600.pp", "sb3x_1600.pp" })
        {
            try
            {
                if (fs.HasArchive(archive) && fs.ExistsReferenced(archive, File(number)))
                {
                    audio.Play(fs, archive, File(number), AudioBus.Effects);
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return;
            }
        }
    }
}
