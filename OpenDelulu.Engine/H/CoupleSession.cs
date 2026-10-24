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

/// <summary>
/// What the H, fera and fetish scenes share: the girl's hwBB_….xa and the man's hm….xa from one ld01 row (columns 1/2 and
/// 3/4), played together; both files have the same clips, so the man plays the girl's clip index. The man is mb00 (ld01
/// row 101). Each clip the girl enters applies its row of the scene's lh table (column 2 expression, 14 voice row, 15
/// priority) and says a line of the lp table, like the oil scene.
/// </summary>
public abstract class CoupleSession
{
    public sealed record Partner(RenderModel Model, CharacterAnimator Animator);

    /// <summary>What <see cref="Load"/> reads for a scene.</summary>
    protected sealed record Parts(Character Girl, Partner Man, AnimationPlayer Body, AnimationPlayer ManBody,
        Dictionary<int, (int Expression, int VoiceRow, int Priority)> Events, Dictionary<int, IReadOnlyList<string>> Voices,
        VoiceLibrary? VoiceLibrary, AudioSystem? Audio);

    readonly Dictionary<int, (int Expression, int VoiceRow, int Priority)> _events;
    readonly Dictionary<int, IReadOnlyList<string>> _voices;
    readonly VoiceLibrary? _voiceLibrary;
    readonly AudioSystem? _audio;
    int _speakingPriority;

    protected readonly Random Random = new();

    /// <summary>Clip index by name (the first one of a name; static clips left out).</summary>
    protected readonly Dictionary<string, int> Clips;

    /// <summary>A clip to play once the girl reaches another one (an order given in the middle of a transition).</summary>
    protected (string When, string Then)? After;

    protected CoupleSession(Parts parts)
    {
        Girl = parts.Girl;
        Man = parts.Man;
        Body = parts.Body;
        ManBody = parts.ManBody;
        _events = parts.Events;
        _voices = parts.Voices;
        _voiceLibrary = parts.VoiceLibrary;
        _audio = parts.Audio;
        Clips = Body.Clips.Where(c => c.Name.Length > 0 && !c.IsStatic).GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First().Index);
        Body.ClipChanged += OnClipChanged;
    }

    public Character Girl { get; }
    public Partner Man { get; }
    public AnimationPlayer Body { get; }
    public AnimationPlayer ManBody { get; }

    public string Clip => Body.Current?.Name ?? "";

    /// <summary>0..1: how close he is to coming.</summary>
    public double Excitement { get; protected set; }

    /// <summary>Raised when he comes (true: inside / in her mouth).</summary>
    public event Action<bool>? Came;

    protected void RaiseCame(bool inside) => Came?.Invoke(inside);

    /// <summary>
    /// Reads the girl's and the man's animations of ld01 row <paramref name="row"/>, the man, the lh table
    /// <paramref name="events"/> and the lp table <paramref name="voices"/> (can run on a worker thread).
    /// </summary>
    protected static Parts Load(GameFileSystem fs, CharacterAssembler assembler, Character girl, int row, string events, string voices,
        VoiceLibrary? voiceLibrary, AudioSystem? audio)
    {
        string tables = assembler.Database.TableArchive;
        var cells = Row(fs, tables, girl.Girl.Id, row) ?? throw new InvalidDataException($"No ld01 row {row} for girl {girl.Girl.Id}.");
        var womanXa = AssetRef.FromRow(cells, 1) ?? throw new InvalidDataException($"No woman's animation in ld01 row {row}.");
        var manXa = AssetRef.FromRow(cells, 3) ?? throw new InvalidDataException($"No man's animation in ld01 row {row}.");
        if (!fs.ExistsReferenced(womanXa.Archive, womanXa.File) || !fs.ExistsReferenced(manXa.Archive, manXa.File))
            throw new FileNotFoundException($"{womanXa} / {manXa} is not installed.");
        var manRef = Row(fs, tables, girl.Girl.Id, 101) is { } manRow && AssetRef.FromRow(manRow, 1) is { } r ? r : new AssetRef("guy_uncensor.pp", "mb00_00_00_00_00.xx");

        var body = assembler.LoadBodyAnimation(girl.Model, womanXa) ?? throw new InvalidDataException($"{womanXa.File} has no animation.");
        body.FollowNext = true;
        var manModel = RenderModel.FromXx(XxReader.Read(fs.ReadReferenced(manRef.Archive, manRef.File)), manRef.File);
        var manAnimation = XaReader.Read(fs.ReadReferenced(manXa.Archive, manXa.File)).Animation
            ?? throw new InvalidDataException($"{manXa.File} has no animation.");
        // He follows her clip changes; on his own he only loops.
        var manBody = new AnimationPlayer(manModel.Rig, manAnimation, manXa.File) { FollowNext = false };
        var man = new Partner(manModel, new CharacterAnimator(manModel) { Body = manBody, EyesFollowTarget = false });

        var eventRows = new Dictionary<int, (int, int, int)>();
        foreach (var e in Table(fs, tables, events)?.Rows ?? [])
        {
            if (e.Length < 16 || !int.TryParse(e[0], out int clip))
                continue;
            int Int(int column) => int.TryParse(e[column], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
            eventRows[clip] = (Int(2), Int(14), Int(15));
        }
        var lines = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var v in Table(fs, tables, voices)?.Rows ?? [])
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
        return new Parts(girl, man, body, manBody, eventRows, lines, voiceLibrary, audio);
    }

    static LstTable? Table(GameFileSystem fs, string archive, string name)
    {
        try { return fs.ReadTable(archive, name); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException) { return null; }
    }

    static string[]? Row(GameFileSystem fs, string archive, int girl, int row) =>
        Table(fs, archive, $"ld01_{girl:00}_00_00_00.lst")?.Rows.FirstOrDefault(r => r.Length > 2 && r[0] == row.ToString(CultureInfo.InvariantCulture));

    /// <summary>Whether ld01 row <paramref name="row"/> of the girl names animations that are installed.</summary>
    public static bool IsInstalled(GameFileSystem fs, CharacterAssembler assembler, int girl, int row)
    {
        var cells = Row(fs, assembler.Database.TableArchive, girl, row);
        return cells is not null && AssetRef.FromRow(cells, 1) is { } w && AssetRef.FromRow(cells, 3) is { } m
            && fs.ExistsReferenced(w.Archive, w.File) && fs.ExistsReferenced(m.Archive, m.File);
    }

    /// <summary>Puts both on <paramref name="placement"/> and starts the scene (on the thread that renders).</summary>
    public void Attach(Matrix4x4 placement)
    {
        Girl.Animator.Body = Body;
        Girl.Animator.Placement = placement;
        Man.Animator.Placement = placement;
        Girl.Animator.Physics?.Reset();
        Begin();
    }

    /// <summary>The first clip.</summary>
    protected abstract void Begin();

    public virtual void Detach()
    {
        Body.ClipChanged -= OnClipChanged;
        Girl.Animator.Face?.SetExpression(0, 0);
    }

    /// <summary>Time going by (excitement that grows by itself, automatic choices).</summary>
    public virtual void Update(double seconds)
    {
    }

    /// <summary>Plays a clip by name (the man follows); false when the scene has no such clip.</summary>
    public bool Play(string name)
    {
        if (!Clips.TryGetValue(name, out int index))
            return false;
        Body.Play(index);
        return true;
    }

    public bool Has(string name) => Clips.ContainsKey(name);

    /// <summary>Speed scale of a clip for both players (1 by default).</summary>
    protected virtual double SpeedFor(AnimationClipInfo clip) => 1;

    /// <summary>A clip the girl entered, after the shared work (man, expression, voice, <see cref="After"/>).</summary>
    protected virtual void OnClip(AnimationClipInfo clip)
    {
    }

    void OnClipChanged(AnimationClipInfo clip)
    {
        if (ManBody.Current?.Index != clip.Index)
            ManBody.Play(clip.Index);
        Body.SpeedScale = ManBody.SpeedScale = SpeedFor(clip);
        if (_events.TryGetValue(clip.Index, out var e))
        {
            Girl.Animator.Face?.SetExpression(e.Expression, e.Expression);
            Girl.Animator.Invalidate();
            if (e.VoiceRow > 0)
                Say(e.VoiceRow, e.Priority);
        }
        if (After is { } after && after.When == clip.Name)
        {
            After = null;
            if (Play(after.Then))
                return;
        }
        OnClip(clip);
    }

    void Say(int row, int priority)
    {
        if (_voiceLibrary is null || _audio is null || !_voices.TryGetValue(row, out var files))
            return;
        if (Girl.Animator.Speech is not null && priority < _speakingPriority)
            return;
        string name = string.Format(CultureInfo.InvariantCulture, files[Random.Next(files.Count)], Girl.Girl.Id);
        var line = _voiceLibrary.For(Girl.Girl.Id).FirstOrDefault(l => string.Equals(l.File, name, StringComparison.OrdinalIgnoreCase));
        if (line is null)
            return;
        Girl.Animator.Speech = _voiceLibrary.Speak(line, _audio);
        _speakingPriority = priority;
    }
}
