using System.Text.Json;
using System.Text.Json.Serialization;
using OpenDelulu.Engine.Script;

namespace OpenDelulu.Engine.Game;

/// <summary>How far the player got with one girl.</summary>
public sealed class GirlProgress
{
    /// <summary>1..5, like the game's 親密度 (intimacy).</summary>
    public int Intimacy { get; set; } = 1;

    /// <summary>The first-meeting script of the current level was seen.</summary>
    public bool MetAtLevel { get; set; }

    /// <summary>Row of the swimsuit she wears (lbNN table), or null for her first one.</summary>
    public int? SwimsuitRow { get; set; }

    /// <summary>Colours chosen in the wardrobe (nails, eyes, hair).</summary>
    public GirlLook Look { get; set; } = new();

    /// <summary>Sun tan, 0 (none) .. 1 (full): raised by the tan oils, lowered by the whitening creams.</summary>
    public double Tan { get; set; }

    /// <summary>The swimsuit she wore while tanning: its lines (lbNN column 4) stay on her skin.</summary>
    public int? TanSwimsuitRow { get; set; }

    /// <summary>The wardrobe's 12 favourite sets (null = empty).</summary>
    public List<FavoriteSet?> Favorites { get; set; } = [];

    /// <summary>
    /// Album pictures she unlocked (st03_03_GG_NN): 1..4 when her level rises from that number to the next, 5 for her
    /// H picture. The exe keeps the same flags in the item list save, 20 bytes per girl from offset 0x18.
    /// </summary>
    public List<int> AlbumPictures { get; set; } = [];

    /// <summary>Raises her level by one and unlocks the album picture of the level she leaves, like the exe does.</summary>
    public void LevelUp()
    {
        if (Intimacy is >= 1 and <= 4 && !AlbumPictures.Contains(Intimacy))
            AlbumPictures.Add(Intimacy);
        Intimacy++;
        MetAtLevel = false;
    }
}

/// <summary>
/// The wardrobe's Color and Hair pages: colour (0..255 per channel, multiplied with the texture), gloss and
/// transparency (0..100) of the hand nails, foot nails and eyes, and the hair colour.
/// </summary>
public sealed class GirlLook
{
    public int[] Hand { get; set; } = [255, 255, 255];
    public int[] HandGloss { get; set; } = [255, 255, 255];
    public int HandOpacity { get; set; } = 100;
    public int[] Feet { get; set; } = [255, 255, 255];
    public int[] FeetGloss { get; set; } = [255, 255, 255];
    public int FeetOpacity { get; set; } = 100;
    public int[] Eyes { get; set; } = [255, 255, 255];
    public int[] EyesGloss { get; set; } = [255, 255, 255];
    public int EyesOpacity { get; set; } = 100;
    public int[] Hair { get; set; } = [255, 255, 255];
    /// <summary>Worn accessories: place (lc00 row 1..15) → item row of its lcNN table.</summary>
    public Dictionary<int, int> Accessories { get; set; } = [];

    public GirlLook Clone() => new()
    {
        Hand = [.. Hand], HandGloss = [.. HandGloss], HandOpacity = HandOpacity,
        Feet = [.. Feet], FeetGloss = [.. FeetGloss], FeetOpacity = FeetOpacity,
        Eyes = [.. Eyes], EyesGloss = [.. EyesGloss], EyesOpacity = EyesOpacity,
        Hair = [.. Hair],
        Accessories = new(Accessories),
    };
}

/// <summary>A favourite set of the wardrobe: when it was saved, the swimsuit and the colours.</summary>
public sealed class FavoriteSet
{
    public DateTime Saved { get; set; }
    public int? SwimsuitRow { get; set; }
    public GirlLook Look { get; set; } = new();
}

/// <summary>
/// Open Delulu's own save (JSON): per-girl intimacy. The original save files are read-only for now
/// (see <see cref="Formats.Save.SaveFile"/>); most of their fields are not understood yet.
/// </summary>
public sealed class GameProgress
{
    public Dictionary<int, GirlProgress> Girls { get; set; } = [];

    /// <summary>The front desk's welcome (c99_00_00) was seen on the first visit to the lobby.</summary>
    public bool LobbyVisited { get; set; }

    /// <summary>Time played, shown on the ticket (hhh:mm).</summary>
    public double PlaySeconds { get; set; }

    /// <summary>Days on the island (1 on arrival, +1 for every night's sleep), shown on the ticket.</summary>
    public int Day { get; set; } = 1;

    /// <summary>Time of day (1 morning .. 5 late night) the game was left at.</summary>
    public int TimeOfDay { get; set; } = 2;

    public GirlProgress For(int girlId)
    {
        if (!Girls.TryGetValue(girlId, out var progress))
            Girls[girlId] = progress = new GirlProgress();
        return progress;
    }

    public static GameProgress Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), GameProgressJson.Default.GameProgress) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new GameProgress();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, GameProgressJson.Default.GameProgress));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSerializable(typeof(GameProgress))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class GameProgressJson : JsonSerializerContext;

/// <summary>The kinds of per-girl event scripts (cNN_LL_KK), told apart by their header titles.</summary>
public enum EventKind
{
    /// <summary>初回: first time at this level (出会い at level 1).</summary>
    FirstMeeting,
    /// <summary>キャラ選択後: picking her again later.</summary>
    Greeting,
    /// <summary>キャラ選択後（夜間）: the same at night (levels 3..5).</summary>
    GreetingNight,
    /// <summary>別れる時: saying goodbye.</summary>
    Parting,
    /// <summary>Ｎへの導入: lead-in to the next level (an H scene from level 2 on).</summary>
    LeadIn,
}

public static class GameEvents
{
    /// <summary>The script for a girl, level and kind; falls back from the night greeting to the day one.</summary>
    public static ScriptInfo? Find(ScriptLibrary library, int girl, int level, EventKind kind)
    {
        var scripts = library.Scripts.Where(s => s.GirlId == girl && s.Intimacy == level).ToList();
        ScriptInfo? Match(Func<string, bool> test) => scripts.FirstOrDefault(s => test(s.Title));
        return kind switch
        {
            EventKind.FirstMeeting => Match(t => t.Contains("初回")),
            EventKind.Greeting => Match(t => t.Contains("キャラ選択後") && !t.Contains("夜間")),
            EventKind.GreetingNight => Match(t => t.Contains("夜間")) ?? Find(library, girl, level, EventKind.Greeting),
            EventKind.Parting => Match(t => t.Contains("別れる時")),
            EventKind.LeadIn => Match(t => t.Contains("への導入")),
            _ => null,
        };
    }

    /// <summary>Levels that have any script for this girl (Fei only appears at level 5).</summary>
    public static IReadOnlyList<int> Levels(ScriptLibrary library, int girl) =>
        library.Scripts.Where(s => s.GirlId == girl && s.Intimacy is not null).Select(s => s.Intimacy!.Value).Distinct().Order().ToList();
}
