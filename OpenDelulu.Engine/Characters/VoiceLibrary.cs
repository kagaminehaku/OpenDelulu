using System.Text.RegularExpressions;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Formats.Voice;

namespace OpenDelulu.Engine.Characters;

/// <summary>A voice line: the sound file and, for .wav lines, the .vss lip-sync file next to it.</summary>
public sealed record VoiceLine(string Archive, string File, string? LipSyncFile)
{
    /// <summary>Two-letter group: po, ph, pd (voice archives sb3_060N / sb3x_160N), pe (event lines in the scripts).</summary>
    public string Group => File[..2].ToLowerInvariant();
}

/// <summary>
/// Voice lines per character, found by name: <c>poNN_*</c>, <c>phNN_*</c>, <c>pdNN_*</c> (.wav + .vss)
/// and <c>peNN_*</c> (.ogg, referenced by the .sdt scripts), NN = character id.
/// </summary>
public sealed partial class VoiceLibrary
{
    readonly GameFileSystem _fs;
    readonly Dictionary<int, List<VoiceLine>> _byGirl = [];

    public VoiceLibrary(GameFileSystem fs)
    {
        _fs = fs;
        foreach (var info in fs.Archives)
        {
            var archive = fs.GetArchive(info.Name);
            var names = archive.Entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                var m = VoiceName().Match(entry.Name);
                if (!m.Success)
                    continue;
                string vss = Path.ChangeExtension(entry.Name, ".vss");
                var line = new VoiceLine(info.Name, entry.Name, names.Contains(vss) ? vss : null);
                int girl = int.Parse(m.Groups[1].Value);
                if (!_byGirl.TryGetValue(girl, out var list))
                    _byGirl[girl] = list = [];
                list.Add(line);
            }
        }
    }

    [GeneratedRegex(@"^p[ohde](\d\d)_.*\.(wav|ogg)$", RegexOptions.IgnoreCase)]
    private static partial Regex VoiceName();

    public IReadOnlyList<VoiceLine> For(int girlId) => _byGirl.TryGetValue(girlId, out var list) ? list : [];

    public VssFile? ReadLipSync(VoiceLine line) =>
        line.LipSyncFile is { } vss ? VssFile.Read(_fs.ReadFile(line.Archive, vss)) : null;

    /// <summary>Plays a line on the voice bus and returns the lip sync that follows it.</summary>
    public LipSync Speak(VoiceLine line, AudioSystem audio)
    {
        var vss = ReadLipSync(line);
        audio.Mixer.StopBus(AudioBus.Voice, 0.05);
        var handle = audio.Play(_fs.ReadFile(line.Archive, line.File), AudioBus.Voice);
        return new LipSync(handle, vss);
    }
}
