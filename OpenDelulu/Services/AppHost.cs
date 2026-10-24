using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenDelulu.Services;

/// <summary>Hooks each platform head fills in before the app starts.</summary>
public static class AppHost
{
    /// <summary>
    /// Where the game data is expected when nothing was chosen yet. Android sets this to the
    /// app-specific external storage folder (readable without permissions).
    /// </summary>
    public static string? SuggestedGameFolder { get; set; }

    /// <summary>Short hint shown in the UI about where to put the game data on this platform.</summary>
    public static string? StorageHint { get; set; }

    /// <summary>Translation key of the storage hint (overrides <see cref="StorageHint"/>), with its arguments.</summary>
    public static string? StorageHintKey { get; set; }

    public static object?[] StorageHintArgs { get; set; } = [];

    /// <summary>Optional startup override (desktop: <c>--game &lt;folder&gt;</c>).</summary>
    public static string? StartupGameFolder { get; set; }

    /// <summary>Optional file to select after loading (desktop: <c>--open &lt;archive.pp&gt; &lt;file&gt;</c>).</summary>
    public static (string Archive, string File)? StartupFile { get; set; }

    /// <summary>Optional girl id to show first in the character maker (desktop: <c>--character &lt;id&gt;</c>).</summary>
    public static int? StartupCharacter { get; set; }

    /// <summary>Optional swimsuit row to select first (desktop: <c>--swimsuit &lt;row&gt;</c>).</summary>
    public static int? StartupSwimsuit { get; set; }

    /// <summary>Optional body variant override (desktop: <c>--cut &lt;n&gt;</c>).</summary>
    public static int? StartupCut { get; set; }

    /// <summary>The head can start the compositor on Vulkan (desktop Windows/Linux).</summary>
    public static bool SupportsVulkanCompositor { get; set; }

    /// <summary>Optional render backend override (desktop: <c>--backend auto|gl|vulkan</c>).</summary>
    public static string? StartupBackend { get; set; }

    /// <summary>Speak a random line of this group once the character is shown (desktop: <c>--speak po|ph|pd|pe|all</c>).</summary>
    public static string? StartupSpeak { get; set; }

    /// <summary>Open this script in the script player and start it (desktop: <c>--script c01_01_00</c>).</summary>
    public static string? StartupScript { get; set; }

    /// <summary>Enter the game right away (desktop: <c>--play &lt;girl&gt; [stage] [time 1..5]</c>).</summary>
    public static (int Girl, int Stage, int Time)? StartupPlay { get; set; }

    /// <summary>UI language for this run, not saved (desktop: <c>--lang vi</c>).</summary>
    public static string? StartupLanguage { get; set; }

    /// <summary>Start this date activity after entering the game (desktop: <c>--activity &lt;n&gt;</c>).</summary>
    public static int? StartupActivity { get; set; }

    /// <summary>Start the sun oil scene in this pose after entering the game (desktop: <c>--oil &lt;pose&gt;</c>).</summary>
    public static int? StartupOil { get; set; }

    /// <summary>Start the H scene in this position (1..8) after entering the game (desktop: <c>--h &lt;position&gt;</c>).</summary>
    public static int? StartupH { get; set; }

    /// <summary>
    /// Test hook: a drag to replay on the 3D view once it accepts touches, as fractions of its size
    /// (desktop: <c>--touch 0.4,0.5;0.6,0.5 [seconds]</c>).
    /// </summary>
    public static (double X, double Y)[]? StartupTouch { get; set; }

    /// <summary>How long the <see cref="StartupTouch"/> drag lasts before letting go.</summary>
    public static double StartupTouchSeconds { get; set; } = 4;

    /// <summary>Play the game's movies (desktop: <c>--no-movies</c> turns them off, e.g. for automated tests).</summary>
    public static bool PlayMovies { get; set; } = true;

    /// <summary>Start with all sound muted (desktop: <c>--mute</c>), e.g. for automated screenshots.</summary>
    public static bool StartupMute { get; set; }

    /// <summary>Ends the app ("End the game" in the system menu); set by heads that can quit.</summary>
    public static Action? RequestExit { get; set; }

    /// <summary>Optional initial camera zoom for the 3D viewer (desktop: <c>--zoom 0.3</c>).</summary>
    public static float? StartupZoom { get; set; }
}

public sealed class AppSettings
{
    public string? LastGameFolder { get; set; }

    /// <summary>"Auto", "OpenGl" or "Vulkan".</summary>
    public string? RenderBackend { get; set; }

    /// <summary>UI language code (en, vi…); null = English.</summary>
    public string? Language { get; set; }

    /// <summary>Volumes in percent; null = default.</summary>
    public int? MasterVolume { get; set; }
    public int? MusicVolume { get; set; }
    public int? AmbientVolume { get; set; }
    public int? EffectsVolume { get; set; }
    public int? VoiceVolume { get; set; }

    /// <summary>The other options of the original CONFIG screen.</summary>
    public GameConfig Config { get; set; } = new();

    /// <summary>Another settings file (headless tests must not touch the user's).</summary>
    public static string? PathOverride { get; set; }

    static string FilePath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenDelulu", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.AppSettings) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJsonContext.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class SettingsJsonContext : JsonSerializerContext;
