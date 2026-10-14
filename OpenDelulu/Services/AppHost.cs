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

    /// <summary>Start with all sound muted (desktop: <c>--mute</c>), e.g. for automated screenshots.</summary>
    public static bool StartupMute { get; set; }

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

    static string FilePath => Path.Combine(
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
