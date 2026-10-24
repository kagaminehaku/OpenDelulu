using System.Globalization;
using System.Numerics;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Formats.Lst;
using OpenDelulu.Formats.Xa;
using OpenDelulu.Formats.Xx;

namespace OpenDelulu.Engine.H;

/// <summary>What the H scene is doing, from the girl's current clip.</summary>
public enum HState
{
    /// <summary>Her mood standby at the start (羞恥 / 通常 / 淫乱待機), Z待機 and the touches from it.</summary>
    Standby,
    /// <summary>A1/A2/B1/B2待機: inserted, waiting for strokes.</summary>
    Inserted,
    /// <summary>Strokes, grinding, changes of position.</summary>
    Moving,
    /// <summary>う！待機: about to come, waiting for inside / outside.</summary>
    Ready,
    /// <summary>The climax and its afterglow.</summary>
    Climax,
    /// <summary>F待機…: pulled out, coming outside.</summary>
    Outside,
}

/// <summary>
/// An H scene (08_h): the girl's hwBB_PP.xa and the man's hmBB_PP.xa (ld01 rows 121..124) played together on one of the
/// stage's H points; the man is mb00 (ld01 row 101). The two files have the same 170 clips, so the man simply follows the
/// girl's clip index. Each file holds two positions, A and B (the H menu's eight positions are the four files × A/B),
/// each with 1 = vagina and 2 = anus. The clips, found by name:
/// <list type="bullet">
/// <item>羞恥 / 通常 / 淫乱待機 (shy, usual, lewd: her mood at the start) → Z待機, the standby; touches from it
/// (キス, 乳もみ, ブラずらし, 股間いじり(前), パンツずらし, オナニー) loop until the way back.</item>
/// <item>Z待機→A1待機 inserts; A1待機→A2待機 / →B1待機 change hole or position; A1待機→Z待機 pulls out.</item>
/// <item>A1→→(ピストンつく大) is a stroke whose chain (＿←, ←＿) comes back to A1待機; A1ピストンつく→A1ぐちゅグラ
/// grinds (ぐちゅぐちゅループ, back to a stroke).</item>
/// <item>A1ピストンつく→A1う！待機 when he is about to come; then →A1絶頂(中だし) (inside: climax, 事後 loop, back to
/// A1待機 by itself) or →F待機 (outside: F待機→F待機(胸 / 尻 / 脚 / 顔 / アソコ), back to Z待機 by itself).</item>
/// </list>
/// Expressions and voices per clip come from lh02_PP (columns 2, 14, 15) and lp02_LL (ph voices), like the oil scene.
/// </summary>
public sealed class HSession
{
    public sealed record Partner(RenderModel Model, CharacterAnimator Animator);

    /// <summary>Where he comes outside (the F待機(…) clips).</summary>
    public static readonly string[] OutsideTargets = ["胸", "尻", "脚", "顔", "アソコ"];

    readonly Dictionary<string, int> _clips;
    readonly Dictionary<int, (int Expression, int VoiceRow, int Priority)> _events;
    readonly Dictionary<int, IReadOnlyList<string>> _voices;
    readonly VoiceLibrary? _voiceLibrary;
    readonly AudioSystem? _audio;
    readonly Random _random = new();
    readonly string _mood;
    int _speakingPriority;
    int _queuedStrokes;
    // A clip to play once the girl reaches another one (an order given in the middle of a transition).
    (string When, string Then)? _after;

    HSession(Character girl, Partner man, AnimationPlayer body, AnimationPlayer manBody, int file, string mood,
        Dictionary<int, (int, int, int)> events, Dictionary<int, IReadOnlyList<string>> voices, VoiceLibrary? voiceLibrary, AudioSystem? audio)
    {
        Girl = girl;
        Man = man;
        Body = body;
        ManBody = manBody;
        File = file;
        _mood = mood;
        _events = events;
        _voices = voices;
        _voiceLibrary = voiceLibrary;
        _audio = audio;
        _clips = body.Clips.Where(c => c.Name.Length > 0 && !c.IsStatic).GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First().Index);
        body.ClipChanged += OnClipChanged;
    }

    public Character Girl { get; }
    public Partner Man { get; }
    public AnimationPlayer Body { get; }
    public AnimationPlayer ManBody { get; }

    /// <summary>1..4: the hwBB_PP file (a pair of positions).</summary>
    public int File { get; }

    /// <summary>The position and hole now inserted ("A1"…), or null.</summary>
    public string? Inserted { get; private set; }

    /// <summary>0..1: how close he is; strokes raise it, and when it is full the next stroke ends at う！待機.</summary>
    public double Excitement { get; private set; }

    /// <summary>Strokes keep going by themselves (the button held).</summary>
    public bool Continuous { get; set; }

    /// <summary>
    /// Speed of the stroke clips (ピストン…): their own speeds (×0.125..×0.5) make a stroke last 8 s at the characters'
    /// rate, so the exe must scale them by the player's stroke speed (not decoded); 4 gives about two strokes a second.
    /// </summary>
    public double StrokeSpeed { get; set; } = 4;

    /// <summary>How much one stroke adds to <see cref="Excitement"/>.</summary>
    public double StrokeExcitement { get; set; } = 0.06;

    /// <summary>Raised when he has come (true inside, false outside).</summary>
    public event Action<bool>? Came;

    public string Clip => Body.Current?.Name ?? "";

    public HState State
    {
        get
        {
            string c = Clip;
            if (c.StartsWith('F'))
                return HState.Outside;
            if (c.Contains("絶頂", StringComparison.Ordinal) || c.Contains("事後", StringComparison.Ordinal))
                return HState.Climax;
            if (Inserted is { } p && c == $"{p}う！待機")
                return HState.Ready;
            if (Inserted is null)
                return HState.Standby;
            return c == $"{Inserted}待機" ? HState.Inserted : HState.Moving;
        }
    }

    /// <summary>
    /// Loads the scene's animations and the man (can run on a worker thread); <see cref="Attach"/> then starts it.
    /// <paramref name="file"/> is 1..4 (hwBB_01..04), <paramref name="level"/> her intimacy level (mood and voices).
    /// </summary>
    public static HSession Start(GameFileSystem fs, CharacterAssembler assembler, Character girl, int file, int level,
        VoiceLibrary? voices = null, AudioSystem? audio = null)
    {
        string tables = assembler.Database.TableArchive;
        file = Math.Clamp(file, 1, 4);
        var row = Row(fs, tables, girl.Girl.Id, 120 + file) ?? throw new InvalidDataException($"No H scene {file} for girl {girl.Girl.Id}.");
        var womanXa = AssetRef.FromRow(row, 1) ?? throw new InvalidDataException("No woman's H animation.");
        var manXa = AssetRef.FromRow(row, 3) ?? throw new InvalidDataException("No man's H animation.");
        var manRef = Row(fs, tables, girl.Girl.Id, 101) is { } manRow && AssetRef.FromRow(manRow, 1) is { } r ? r : new AssetRef("guy_uncensor.pp", "mb00_00_00_00_00.xx");

        var body = assembler.LoadBodyAnimation(girl.Model, womanXa) ?? throw new InvalidDataException($"{womanXa.File} has no animation.");
        body.FollowNext = true;
        var manModel = RenderModel.FromXx(XxReader.Read(fs.ReadReferenced(manRef.Archive, manRef.File)), manRef.File);
        var manAnimation = XaReader.Read(fs.ReadReferenced(manXa.Archive, manXa.File)).Animation
            ?? throw new InvalidDataException($"{manXa.File} has no animation.");
        // He follows her clip changes; on his own he only loops.
        var manBody = new AnimationPlayer(manModel.Rig, manAnimation, manXa.File) { FollowNext = false };
        var man = new Partner(manModel, new CharacterAnimator(manModel) { Body = manBody, EyesFollowTarget = false });

        var events = new Dictionary<int, (int, int, int)>();
        foreach (var e in Table(fs, tables, $"lh02_{file:00}_00_00_00.lst")?.Rows ?? [])
        {
            if (e.Length < 16 || !int.TryParse(e[0], out int clip))
                continue;
            int Int(int column) => int.TryParse(e[column], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
            events[clip] = (Int(2), Int(14), Int(15));
        }
        var lines = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var v in Table(fs, tables, $"lp02_{Math.Clamp(level, 2, 5):00}_00_00_00.lst")?.Rows ?? [])
        {
            if (v.Length < 2 || !int.TryParse(v[0], out int n) || !int.TryParse(v[1], out int count) || count <= 0)
                continue;
            var files = new List<string>();
            for (int i = 0; i < count && 3 + i * 2 < v.Length; i++)
                if (v[3 + i * 2] is { Length: > 1 } name && name != "0")
                    files.Add(name);
            if (files.Count > 0)
                lines[n] = files;
        }
        // Her mood by level: shy at first, then usual, lewd at the last level. The exe's own rule is not decoded.
        string mood = level <= 2 ? "羞恥待機" : level >= 5 ? "淫乱待機" : "通常待機";
        return new HSession(girl, man, body, manBody, file, mood, events, lines, voices, audio);
    }

    static LstTable? Table(GameFileSystem fs, string archive, string name)
    {
        try { return fs.ReadTable(archive, name); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException) { return null; }
    }

    static string[]? Row(GameFileSystem fs, string archive, int girl, int row) =>
        Table(fs, archive, $"ld01_{girl:00}_00_00_00.lst")?.Rows.FirstOrDefault(r => r.Length > 2 && r[0] == row.ToString(CultureInfo.InvariantCulture));

    /// <summary>Puts both on the H point and starts at her mood standby (on the thread that renders).</summary>
    public void Attach(Matrix4x4 placement)
    {
        Girl.Animator.Body = Body;
        Girl.Animator.Placement = placement;
        Man.Animator.Placement = placement;
        Girl.Animator.Physics?.Reset();
        if (!Play(_mood))
            Play("Z待機");
    }

    public void Detach()
    {
        Body.ClipChanged -= OnClipChanged;
        Girl.Animator.Face?.SetExpression(0, 0);
    }

    /// <summary>Plays a clip by name (the man follows); false when the scene has no such clip.</summary>
    public bool Play(string name)
    {
        if (!_clips.TryGetValue(name, out int index))
            return false;
        Body.Play(index);
        return true;
    }

    public bool Has(string name) => _clips.ContainsKey(name);

    void OnClipChanged(AnimationClipInfo clip)
    {
        if (ManBody.Current?.Index != clip.Index)
            ManBody.Play(clip.Index);
        string name = clip.Name;
        double speed = name.Contains("ピストン", StringComparison.Ordinal) && !name.Contains("う！", StringComparison.Ordinal) ? StrokeSpeed : 1;
        Body.SpeedScale = ManBody.SpeedScale = speed;
        if (name.Length >= 2 && name[0] is 'A' or 'B' && name[1] is '1' or '2')
            Inserted = name[..2];
        else if (name.StartsWith('Z') || name.StartsWith('F'))
            Inserted = null;
        if (_events.TryGetValue(clip.Index, out var e))
        {
            Girl.Animator.Face?.SetExpression(e.Expression, e.Expression);
            Girl.Animator.Invalidate();
            if (e.VoiceRow > 0)
                Say(e.VoiceRow, e.Priority);
        }
        if (_after is { } after && after.When == name)
        {
            _after = null;
            Play(after.Then);
            return;
        }
        if (Inserted is { } p && name == $"{p}待機" && (_queuedStrokes > 0 || Continuous))
        {
            _queuedStrokes = Math.Max(0, _queuedStrokes - 1);
            Stroke();
        }
    }

    void Say(int row, int priority)
    {
        if (_voiceLibrary is null || _audio is null || !_voices.TryGetValue(row, out var files))
            return;
        if (Girl.Animator.Speech is not null && priority < _speakingPriority)
            return;
        string name = string.Format(CultureInfo.InvariantCulture, files[_random.Next(files.Count)], Girl.Girl.Id);
        var line = _voiceLibrary.For(Girl.Girl.Id).FirstOrDefault(l => string.Equals(l.File, name, StringComparison.OrdinalIgnoreCase));
        if (line is null)
            return;
        Girl.Animator.Speech = _voiceLibrary.Speak(line, _audio);
        _speakingPriority = priority;
    }

    /// <summary>From her mood standby: to Z待機 first, then <paramref name="then"/>; false when not in her mood standby.</summary>
    bool FromMood(string then)
    {
        if (Clip != _mood)
            return false;
        _after = ("Z待機", then);
        return Play($"{_mood}→Z待機");
    }

    // ------------------------------------------------------------------ commands

    /// <summary>
    /// Inserts in position <paramref name="letter"/> (A / B) and hole <paramref name="hole"/> (1 vagina, 2 anus), from
    /// standby or from another inserted standby (the scene's own A1→A2, A1→B1… clips; otherwise through Z待機).
    /// </summary>
    public bool Insert(char letter, int hole)
    {
        string target = $"{letter}{hole}";
        if (FromMood($"Z待機→{target}待機"))
            return true;
        if (Clip == "Z待機")
            return Play($"Z待機→{target}待機");
        if (State == HState.Inserted && Inserted is { } now && now != target)
        {
            if (Play($"{now}待機→{target}待機"))
                return true;
            _after = ("Z待機", $"Z待機→{target}待機");
            return Play($"{now}待機→Z待機");
        }
        return false;
    }

    /// <summary>Pulls out: back to Z待機.</summary>
    public bool PullOut()
    {
        _queuedStrokes = 0;
        Continuous = false;
        return State == HState.Inserted && Inserted is { } now && Play($"{now}待機→Z待機");
    }

    /// <summary>
    /// One stroke from an inserted standby (the clip chain comes back to it; a stroke asked in the middle of one waits).
    /// When <see cref="Excitement"/> is full the stroke ends at う！待機 instead.
    /// </summary>
    public bool Stroke()
    {
        if (Inserted is not { } p)
            return false;
        if (State == HState.Moving)
        {
            _queuedStrokes = Math.Min(_queuedStrokes + 1, 2);
            return true;
        }
        if (State != HState.Inserted)
            return false;
        Excitement = Math.Min(1, Excitement + StrokeExcitement);
        if (Excitement >= 1 && Has($"{p}ピストンつく→{p}う！待機"))
        {
            _queuedStrokes = 0;
            Continuous = false;
            return Play($"{p}ピストンつく→{p}う！待機");
        }
        return Play($"{p}→→(ピストンつく大)");
    }

    /// <summary>Grinding from an inserted standby (ぐちゅぐちゅ loop, back through a stroke).</summary>
    public bool Grind()
    {
        if (State != HState.Inserted || Inserted is not { } p)
            return false;
        Excitement = Math.Min(1, Excitement + StrokeExcitement);
        return Play($"{p}ピストンつく→{p}ぐちゅグラ");
    }

    /// <summary>Inside, from う！待機: the climax, then the afterglow back to the standby.</summary>
    public bool ComeInside()
    {
        if (State != HState.Ready || Inserted is not { } p || !Play($"{p}う！待機→{p}絶頂(中だし)"))
            return false;
        Excitement = 0;
        Came?.Invoke(true);
        return true;
    }

    /// <summary>Outside, from う！待機: pulled out (F待機), then on <paramref name="target"/> (see <see cref="OutsideTargets"/>).</summary>
    public bool ComeOutside(string target = "胸")
    {
        if (State != HState.Ready || Inserted is not { } p)
            return false;
        if (!Play($"{p}う！待機→F待機"))
            return false;
        Excitement = 0;
        if (Has($"F待機→F待機({target})"))
            _after = ("F待機", $"F待機→F待機({target})");
        Came?.Invoke(false);
        return true;
    }

    /// <summary>A touch from standby (キス, 乳もみ, ブラずらし, 股間いじり(前), パンツずらし, オナニー); again stops it.</summary>
    public bool Touch(string name)
    {
        if (FromMood($"Z待機→{name}"))
            return true;
        string c = Clip;
        if (c == "Z待機")
            return Play($"Z待機→{name}");
        if (c.StartsWith(name, StringComparison.Ordinal) && c.EndsWith("ループ", StringComparison.Ordinal))
            return Play($"{name}→Z待機");
        return false;
    }
}
