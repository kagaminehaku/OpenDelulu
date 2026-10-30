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

    /// <summary>
    /// The scene's fluid (ek05_…: o01_P_shiru meshes whose shape is a morph timeline), placed like the girl; null when
    /// the scene has none.
    /// </summary>
    public Partner? Liquid { get; private set; }

    /// <summary>Whether <see cref="Liquid"/> is to be drawn now.</summary>
    public bool LiquidVisible { get; protected set; }

    /// <summary>The fluid plays the girl's clip index (fera, fetish); otherwise the scene drives it (H).</summary>
    protected bool LiquidFollowsClips { get; set; }

    /// <summary>
    /// Loads a fluid (ld00 row 34: sb3_0100.pp ek05_%02d_%02d; the Plus ones are in sb3x_1100.pp), or null when it is not
    /// installed.
    /// </summary>
    protected void LoadLiquid(GameFileSystem fs, string xx, string xa)
    {
        string? Find(string file) => new[] { "sb3_0100.pp", "sb3x_1100.pp" }.FirstOrDefault(a => fs.HasArchive(a) && fs.ExistsReferenced(a, file));
        if (Find(xx) is not { } xxArchive || Find(xa) is not { } xaArchive)
            return;
        var xxModel = XxReader.Read(fs.ReadReferenced(xxArchive, xx));
        var file = XaReader.Read(fs.ReadReferenced(xaArchive, xa));
        if (file.Animation is not { } animation)
            return;
        // Materials keyed in the .xa fade the meshes in and out (opacity by timeline frame, see LiquidOpacity); their
        // own alpha (often 0) is replaced by the keys.
        _liquidFades.Clear();
        foreach (var keyed in file.Materials?.Materials ?? [])
        {
            if (keyed.Colors.Count == 0 || xxModel.Materials.FirstOrDefault(m => m.Name == keyed.Name) is not { } material)
                continue;
            material.Diffuse = material.Diffuse with { W = 1 };
            var keys = keyed.Colors.Select(c => (Frame: c.Unknown1.Length >= 4 ? BitConverter.ToInt32(c.Unknown1, 0) : 0, Alpha: c.Diffuse.W))
                .OrderBy(k => k.Frame).ToArray();
            _liquidFades[keyed.Name] = keys;
        }
        var model = RenderModel.FromXx(xxModel, xx);
        var animator = new CharacterAnimator(model)
        {
            Body = new AnimationPlayer(model.Rig, animation, xa) { FollowNext = false },
            EyesFollowTarget = false,
        };
        if (file.Morphs is { } morphs)
            animator.Morph = new TimelineMorph(model, morphs);
        Liquid = new Partner(model, animator);
    }

    readonly Dictionary<string, (int Frame, float Alpha)[]> _liquidFades = new(StringComparer.Ordinal);

    /// <summary>
    /// Opacity of a mesh of <see cref="Liquid"/> now: the .xa's material keys (frame, alpha), linear between keys; 1 for
    /// materials without keys.
    /// </summary>
    public float LiquidOpacity(RenderMesh mesh)
    {
        if (!_liquidFades.TryGetValue(mesh.MaterialName, out var keys) || Liquid?.Animator.Body is not { } body)
            return 1;
        double t = body.Time;
        if (t <= keys[0].Frame)
            return keys[0].Alpha;
        for (int i = 1; i < keys.Length; i++)
            if (t <= keys[i].Frame)
            {
                var (f0, a0) = keys[i - 1];
                var (f1, a1) = keys[i];
                return (float)(a0 + (a1 - a0) * (t - f0) / Math.Max(1, f1 - f0));
            }
        return keys[^1].Alpha;
    }

    /// <summary>The girl's body type 1..3, from her idle animation (kiBB): the files of some scenes depend on it.</summary>
    protected static int BodyTypeOf(Character girl)
    {
        string idle = girl.Girl.BodyAnimations.FirstOrDefault().File ?? "";
        return idle.Length >= 4 && int.TryParse(idle.AsSpan(2, 2), out int t) && t is >= 1 and <= 3 ? t : 1;
    }

    /// <summary>0..1: how close he is to coming.</summary>
    public double Excitement { get; protected set; }

    /// <summary>Raised when he comes (true: inside / in her mouth).</summary>
    public event Action<bool>? Came;

    protected void RaiseCame(bool inside) => Came?.Invoke(inside);

    /// <summary>A sound effect to play (se number, see <see cref="Audio.SoundEffects"/>).</summary>
    public event Action<int>? SoundEffect;

    protected void RaiseSoundEffect(int number) => SoundEffect?.Invoke(number);

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
        Placement = placement;
        Girl.Animator.Body = Body;
        Girl.Animator.Placement = placement;
        Man.Animator.Placement = placement;
        if (Liquid is { } liquid)
            liquid.Animator.Placement = placement;
        Girl.Animator.Physics?.Reset();
        Begin();
    }

    /// <summary>Where the scene stands (the H point).</summary>
    public Matrix4x4 Placement { get; private set; }

    /// <summary>
    /// Moves the fluid sideways so that its root (o01_N_shiru, at the current clip's start) is under the girl's crotch
    /// locator o01_N_Sio. The data puts it at a fixed spot of the scene, which does not meet her crotch with the H points'
    /// placement here (the exe's own placement of the effect is not decoded).
    /// </summary>
    protected void PutLiquidUnderCrotch()
    {
        if (Liquid is not { } liquid || liquid.Animator.Body is not { } body)
            return;
        var rig = liquid.Model.Rig;
        int root = rig.Find("o01_N_shiru"), crotch = Girl.Model.Rig.Find("o01_N_Sio");
        if (root < 0 || crotch < 0)
            return;
        var local = new Matrix4x4[rig.Count];
        body.Evaluate(local);
        var world = new Matrix4x4[rig.Count];
        rig.ComputeWorld(local, world);
        var from = Vector3.Transform(world[root].Translation, Placement);
        var to = Girl.Animator.WorldOf(crotch).Translation;
        liquid.Animator.Placement = Placement * Matrix4x4.CreateTranslation(to.X - from.X, 0, to.Z - from.Z);
        liquid.Animator.Invalidate();
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
        if (LiquidFollowsClips && Liquid?.Animator.Body is { } liquid)
        {
            liquid.Play(clip.Index);
            liquid.SpeedScale = Body.SpeedScale;
        }
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
