using OpenDelulu.Engine.Stages;

namespace OpenDelulu.Engine.Audio;

/// <summary>The screens of the exe that start music (the callers of its BGM / ambience loaders 0x42D840 / 0x42DB50).</summary>
public enum MusicScene
{
    Ticket,
    Title,
    /// <summary>Girl and stage selection on the island map (03_chrsel, 04_mapsel).</summary>
    Map,
    Date,
    Oil,
    H,
    Fera,
    Hap,
    Logo,
    EventCg,
    Memories,
    Opening,
    Lobby,
    PhotoSelect,
    Photo,
}

/// <summary>
/// Which background music (bgNN in sb3_0600, ld00 row 71) and ambience (esNN, row 72) a screen plays, from the tables
/// the exe builds on the stack: 0x45F390 by time of day for the map, 0x45F7A0 by stage and time for the date, H, Fera,
/// happening and photo scenes, 0x45F410 by stage and time for the oil scene; the other screens pass constants. 0 = none.
/// </summary>
public static class SceneMusic
{
    // [stage - 1][time - 1], stages 1..9, times morning .. late night.
    static readonly int[,] DateBgm = Repeat([0, 16, 17, 18, 0], 6, [16, 16, 17, 18, 18], 3);
    static readonly int[,] DateAmbience =
    {
        { 2, 0, 0, 0, 6 }, { 3, 0, 0, 0, 3 }, { 4, 0, 0, 0, 8 }, { 5, 0, 0, 0, 7 }, { 5, 0, 0, 0, 7 }, { 2, 0, 0, 0, 6 },
        { 0, 0, 0, 0, 0 }, { 0, 0, 0, 0, 0 }, { 0, 0, 0, 0, 0 },
    };
    static readonly int[,] OilBgm =
    {
        { 0, 6, 10, 12, 0 }, { 0, 0, 0, 0, 0 }, { 0, 8, 10, 14, 0 }, { 0, 7, 11, 13, 0 }, { 0, 7, 11, 13, 0 },
        { 0, 6, 10, 12, 0 }, { 9, 9, 9, 9, 9 }, { 9, 9, 9, 9, 9 }, { 9, 9, 9, 9, 9 },
    };
    static readonly int[,] OilAmbience =
    {
        { 2, 2, 2, 6, 6 }, { 3, 3, 3, 3, 3 }, { 4, 4, 4, 8, 8 }, { 5, 5, 5, 7, 7 }, { 5, 5, 5, 7, 7 }, { 2, 2, 2, 6, 6 },
        { 0, 0, 0, 0, 0 }, { 0, 0, 0, 0, 0 }, { 0, 0, 0, 0, 0 },
    };
    static readonly int[] MapBgm = [2, 3, 4, 5, 0];
    static readonly int[] MapAmbience = [0, 0, 0, 0, 6];

    /// <summary>(music, ambience) numbers for a screen; <paramref name="stage"/> and <paramref name="time"/> matter where the tables use them.</summary>
    public static (int Music, int Ambience) For(MusicScene scene, int stage = 1, StageTime time = StageTime.Noon)
    {
        int s = Math.Clamp(stage, 1, 9) - 1, t = Math.Clamp((int)time, 1, 5) - 1;
        return scene switch
        {
            MusicScene.Ticket => (0, 1),
            MusicScene.Title or MusicScene.EventCg or MusicScene.Memories or MusicScene.PhotoSelect => (1, 0),
            MusicScene.Map => (MapBgm[t], MapAmbience[t]),
            MusicScene.Date or MusicScene.H or MusicScene.Fera or MusicScene.Hap or MusicScene.Photo => (DateBgm[s, t], DateAmbience[s, t]),
            MusicScene.Oil => (OilBgm[s, t], OilAmbience[s, t]),
            MusicScene.Lobby => (15, 0),
            _ => (0, 0),
        };
    }

    public static string MusicFile(int number) => $"bg{number:00}_00_00_00_00.ogg";
    public static string AmbienceFile(int number) => $"es{number:00}_00_00_00_00.ogg";

    /// <summary>The archive of both (ld00 rows 71, 72).</summary>
    public const string Archive = "sb3_0600.pp";

    static int[,] Repeat(int[] first, int firstCount, int[] second, int secondCount)
    {
        var table = new int[firstCount + secondCount, 5];
        for (int r = 0; r < firstCount + secondCount; r++)
            for (int c = 0; c < 5; c++)
                table[r, c] = r < firstCount ? first[c] : second[c];
        return table;
    }
}
