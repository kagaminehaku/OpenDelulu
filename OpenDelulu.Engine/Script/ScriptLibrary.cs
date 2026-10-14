using System.Text.RegularExpressions;
using OpenDelulu.Formats.Script;

namespace OpenDelulu.Engine.Script;

/// <summary>One .sdt file of the install.</summary>
/// <param name="GirlId">NN of cNN_MM_KK scripts (the girl the event is about), else null.</param>
/// <param name="Intimacy">MM of cNN_MM_KK (intimacy level 1..5), else null.</param>
/// <param name="Title">From the header comment (e.g. "親密度１（初回（出会い））"), or the file name.</param>
public sealed record ScriptInfo(string Archive, string Name, int? GirlId, int? Intimacy, string Title)
{
    public string Display => $"{Name}  {ScriptLibrary.TranslateTitle(Title)}";
}

/// <summary>
/// All event/dialogue scripts (sb3_0700.pp, sb3x_1700.pp). When both editions have a script, the one from the
/// archive listed last (the add-on) wins.
/// </summary>
public sealed partial class ScriptLibrary
{
    readonly GameFileSystem _fs;
    readonly Dictionary<string, ScriptInfo> _byName = new(StringComparer.OrdinalIgnoreCase);

    public ScriptLibrary(GameFileSystem fs)
    {
        _fs = fs;
        foreach (var info in fs.Archives)
        {
            var archive = fs.GetArchive(info.Name);
            foreach (var entry in archive.Entries.Where(e => e.Extension == ".sdt"))
            {
                string name = Path.GetFileNameWithoutExtension(entry.Name);
                var m = CharacterScript().Match(name);
                string title = name;
                try
                {
                    title = TitleOf(SdtFile.Read(archive.ReadAllBytes(entry))) ?? name;
                }
                catch (InvalidDataException)
                {
                }
                _byName[name] = new ScriptInfo(info.Name, name,
                    m.Success ? int.Parse(m.Groups[1].Value) : null, m.Success ? int.Parse(m.Groups[2].Value) : null, title);
            }
        }
        Scripts = _byName.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    [GeneratedRegex("([１-５])への導入(Ｈ)?")]
    private static partial Regex LeadIn();

    [GeneratedRegex(@"^c(\d\d)_(\d\d)_\d\d$", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterScript();

    public IReadOnlyList<ScriptInfo> Scripts { get; }

    public ScriptInfo? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Loads a script by name (without .sdt); null if missing.</summary>
    public SdtFile? Load(string name) =>
        _byName.TryGetValue(name, out var info) ? SdtFile.Read(_fs.ReadFile(info.Archive, info.Name + ".sdt")) : null;

    /// <summary>The descriptive comment of the header block (the line after the girl's name).</summary>
    static string? TitleOf(SdtFile file)
    {
        var comments = file.Scenes.FirstOrDefault()?.Commands
            .TakeWhile(c => c.Op == SdtFile.OpComment)
            .Select(c => ((string)c.Args[0]).TrimStart('/').Trim('　', ' '))
            .Where(t => t.Length > 0 && !t.All(ch => ch is '-' or '=') && !char.IsDigit(t[0]) && !t.StartsWith('１'))
            .ToList();
        return comments is { Count: > 0 } ? comments[^1] : null;
    }

    /// <summary>
    /// Display names for the speakers used in the scripts (the scripts keep the Japanese names; the English
    /// patch only translated the lines).
    /// </summary>
    public static string SpeakerName(string speaker) => speaker switch
    {
        "俺" => "Tôi",
        "愛美" => "Manami",
        "麻矢" => "Maya",
        "イスカ" => "Esk",
        "イーオ" => "Eo",
        "麗子" => "Reiko",
        "マリア" => "Maria",
        "バエル" => "Bael",
        "フェイ" => "Fei",
        "フロント" => "Lễ tân",
        "設定２" => "",
        _ => speaker,
    };

    static readonly (string Japanese, string Vietnamese)[] TitleWords =
    [
        ("クリア後メッセージ", "Lời nhắn sau khi phá đảo"),
        ("セーブ確認", "Xác nhận lưu"),
        ("デートシーンからマップ選択に戻った時", "Từ buổi hẹn quay về chọn bản đồ"),
        ("ロビーでの親密度変更", "Đổi mức thân mật ở sảnh"),
        ("ロビーに戻った時", "Khi quay về sảnh"),
        ("島に到着後", "Sau khi tới đảo"),
        ("水着取得", "Nhận đồ bơi"),
        ("キャラ選択後", "sau khi chọn"),
        ("初回", "lần đầu"),
        ("出会い", "gặp mặt"),
        ("別れる時", "lúc chia tay"),
        ("夜間", "buổi tối"),
        ("親密度", "Thân mật "),
        ("（", " ("),
        ("）", ")"),
        ("１", "1"), ("２", "2"), ("３", "3"), ("４", "4"), ("５", "5"),
    ];

    /// <summary>Vietnamese rendering of a script title (the titles are a small fixed set).</summary>
    public static string TranslateTitle(string title)
    {
        // "２への導入Ｈ" = lead-in to level 2 (H scene).
        title = LeadIn().Replace(title, m => $"mở đầu mức {m.Groups[1].Value[0] - '０'}{(m.Groups[2].Success ? " (H)" : "")}");
        foreach (var (japanese, vietnamese) in TitleWords)
            title = title.Replace(japanese, vietnamese);
        return title;
    }

    /// <summary>Girl id from a voice file name (pe02_… → 2), or null.</summary>
    public static int? VoiceGirl(string? voice) =>
        voice is { Length: >= 4 } && voice.StartsWith("pe", StringComparison.OrdinalIgnoreCase) && int.TryParse(voice.AsSpan(2, 2), out int id) ? id : null;
}
