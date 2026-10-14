using System.Text.RegularExpressions;

namespace OpenDelulu.Localization;

/// <summary>
/// Translations of the few Japanese strings of the game data that the UI shows: speaker names (the English patch
/// translated the lines but kept the names) and script titles from the header comments.
/// </summary>
public static partial class GameText
{
    /// <summary>Speaker name for the dialogue box; empty for system messages.</summary>
    public static string Speaker(string japanese) => japanese switch
    {
        "俺" => Translator.Get("speaker.player"),
        "愛美" => "Manami",
        "麻矢" => "Maya",
        "イスカ" => "Esk",
        "イーオ" => "Eo",
        "麗子" => "Reiko",
        "マリア" => "Maria",
        "バエル" => "Bael",
        "フェイ" => "Fei",
        "フロント" => Translator.Get("speaker.frontDesk"),
        "設定２" => "",
        _ => japanese,
    };

    // Japanese fragment → translation key, applied in order (longest phrases first).
    static readonly (string Japanese, string Key)[] TitleWords =
    [
        ("クリア後メッセージ", "title.afterClear"),
        ("セーブ確認", "title.saveConfirm"),
        ("デートシーンからマップ選択に戻った時", "title.backFromDate"),
        ("ロビーでの親密度変更", "title.lobbyIntimacy"),
        ("ロビーに戻った時", "title.backToLobby"),
        ("島に到着後", "title.arrival"),
        ("水着取得", "title.swimsuitUnlocked"),
        ("キャラ選択後", "title.greeting"),
        ("初回", "title.first"),
        ("出会い", "title.meeting"),
        ("別れる時", "title.parting"),
        ("夜間", "title.night"),
    ];

    [GeneratedRegex("親密度([１-５])")]
    private static partial Regex Level();

    [GeneratedRegex("([１-５])への導入(Ｈ)?")]
    private static partial Regex LeadIn();

    /// <summary>Translated script title, e.g. "親密度１（初回（出会い））" → "Intimacy 1 (first time (meeting))".</summary>
    public static string ScriptTitle(string title)
    {
        title = LeadIn().Replace(title, m => Translator.Format(m.Groups[2].Success ? "title.leadInH" : "title.leadIn", Digit(m.Groups[1].Value)));
        title = Level().Replace(title, m => Translator.Format("title.level", Digit(m.Groups[1].Value)));
        foreach (var (japanese, key) in TitleWords)
            title = title.Replace(japanese, Translator.Get(key));
        return title.Replace("（", " (").Replace("）", ")");
    }

    static int Digit(string fullWidth) => fullWidth[0] - '０';
}
