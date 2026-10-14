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
}

/// <summary>
/// Open Delulu's own save (JSON): per-girl intimacy. The original save files are read-only for now
/// (see <see cref="Formats.Save.SaveFile"/>); most of their fields are not understood yet.
/// </summary>
public sealed class GameProgress
{
    public Dictionary<int, GirlProgress> Girls { get; set; } = [];

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
