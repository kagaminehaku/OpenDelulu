using System.Numerics;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Engine.Characters;

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
/// An H scene (08_h): hwBB_PP.xa / hmBB_PP.xa of ld01 rows 121..124 (P = 1..4) on one of the stage's H points. The two
/// files have the same 170 clips. Each file holds two positions, A and B (the H menu's eight positions are the four files
/// × A/B), each with 1 = vagina and 2 = anus. The clips, found by name:
/// <list type="bullet">
/// <item>羞恥 / 通常 / 淫乱待機 (shy, usual, lewd: her mood at the start) → Z待機, the standby; touches from it
/// (キス, 乳もみ, ブラずらし, 股間いじり(前), パンツずらし, オナニー) loop until the way back.</item>
/// <item>Z待機→A1待機 inserts; A1待機→A2待機 / →B1待機 change hole or position; A1待機→Z待機 pulls out.</item>
/// <item>A1→→(ピストンつく大) is a stroke whose chain (＿←, ←＿) comes back to A1待機; A1ピストンつく→A1ぐちゅグラ
/// grinds (ぐちゅぐちゅループ, back to a stroke).</item>
/// <item>A1ピストンつく→A1う！待機 when he is about to come; then →A1絶頂(中だし) (inside: climax, 事後 loop, back to
/// A1待機 by itself) or →F待機 (outside: F待機→F待機(胸 / 尻 / 脚 / 顔 / アソコ), back to Z待機 by itself).</item>
/// </list>
/// Expressions and voices per clip: lh02_PP and lp02_LL (ph voices). Fluid: ek05_PP_00 after coming inside.
/// </summary>
public sealed class HSession : CoupleSession
{
    /// <summary>Where he comes outside (the F待機(…) clips).</summary>
    public static readonly string[] OutsideTargets = ["胸", "尻", "脚", "顔", "アソコ"];

    readonly string _mood;
    int _queuedStrokes, _inside;

    HSession(Parts parts, int file, string mood) : base(parts)
    {
        File = file;
        _mood = mood;
    }

    /// <summary>1..4: the hwBB_PP file (a pair of positions).</summary>
    public int File { get; }

    /// <summary>The position and hole now inserted ("A1"…), or null.</summary>
    public string? Inserted { get; private set; }

    /// <summary>Strokes keep going by themselves (the button held).</summary>
    public bool Continuous { get; set; }

    /// <summary>
    /// Speed of the stroke clips (ピストン…): their own speeds (×0.125..×0.5) make a stroke last 8 s at the characters'
    /// rate, so the exe must scale them by the player's stroke speed (not decoded); 4 gives about two strokes a second.
    /// </summary>
    public double StrokeSpeed { get; set; } = 4;

    /// <summary>How much one stroke adds to <see cref="CoupleSession.Excitement"/>.</summary>
    public double StrokeExcitement { get; set; } = 0.06;

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
    /// Loads the scene (can run on a worker thread); <see cref="CoupleSession.Attach"/> then starts it.
    /// <paramref name="file"/> is 1..4 (hwBB_01..04), <paramref name="level"/> her intimacy level (mood and voices).
    /// </summary>
    public static HSession Start(GameFileSystem fs, CharacterAssembler assembler, Character girl, int file, int level,
        VoiceLibrary? voices = null, AudioSystem? audio = null)
    {
        file = Math.Clamp(file, 1, 4);
        var parts = Load(fs, assembler, girl, 120 + file, $"lh02_{file:00}_00_00_00.lst", $"lp02_{Math.Clamp(level, 2, 5):00}_00_00_00.lst", voices, audio);
        // Her mood by level: shy at first, then usual, lewd at the last level. The exe's own rule is not decoded.
        string mood = level <= 2 ? "羞恥待機" : level >= 5 ? "淫乱待機" : "通常待機";
        var session = new HSession(parts, file, mood);
        session.LoadLiquid(fs, $"ek05_{file:00}_00_00_00.xx", $"ek05_{file:00}_00_00_00.xa");
        if (session.Liquid?.Animator.Body is { } liquid)
            liquid.HoldAtEnd = true;
        // Her climax: ek06 (ld00 row 35), the frame p01_N_shiohuki its .ptcl spray hangs on (潮吹き, clip 1).
        session.Squirt = LoadEffect(fs, "ek06_00_00_00_00.xx", "ek06_00_00_00_00.xa");
        // Coming outside: ek04 (ld00 row 36), a drop flying (飛びループ) then running down (垂れA..C).
        session.Drops = LoadEffect(fs, "ek04_00_00_00_00.xx", "ek04_00_00_00_00.xa");
        if (session.Drops?.Animator.Body is { } drops)
            drops.FollowNext = true;
        return session;
    }

    protected override void Begin()
    {
        if (!Play(_mood))
            Play("Z待機");
    }

    protected override double SpeedFor(AnimationClipInfo clip) =>
        clip.Name.Contains("ピストン", StringComparison.Ordinal) && !clip.Name.Contains("う！", StringComparison.Ordinal) ? StrokeSpeed : 1;

    // Sound effects: the clip they were last checked in and its time then.
    AnimationClipInfo? _soundClip;
    double _soundTime;

    /// <summary>
    /// The exe's H sounds by clip (sb_HScene 0x4B3E90), fired when the girl's clip time passes a point of the clip: see
    /// <see cref="SoundEffects"/>.
    /// </summary>
    public override void Update(double seconds)
    {
        // The spray follows her crotch while it plays.
        if (SquirtVisible && Squirt is { } squirt && Girl.Model.Rig.Find("o01_N_Sio") is int sio and >= 0)
        {
            squirt.Animator.Placement = Girl.Animator.WorldOf(sio);
            squirt.Animator.Invalidate();
        }
        if (Body.Current is not { IsStatic: false } clip)
            return;
        double now = Body.Time;
        double before = ReferenceEquals(clip, _soundClip) && now >= _soundTime ? _soundTime : clip.Start - 1e-3;
        _soundClip = clip;
        _soundTime = now;
        double length = clip.End - clip.Start;
        bool Passed(double fraction) => clip.Start + length * fraction is var at && at > before && at <= now;
        bool wet = Excitement >= 0.5;
        string name = clip.Name;
        if (name.StartsWith("(×)", StringComparison.Ordinal))
            return;
        if (name.Contains("→→(ピストンつく大)", StringComparison.Ordinal) || name.Contains("＿→(ピストンつく小)", StringComparison.Ordinal))
        {
            if (Passed(0))
                RaiseSoundEffect(SoundEffects.Push);
        }
        else if (name.Contains("＿←(ピストンひくA)", StringComparison.Ordinal) || name.Contains("淫乱ピストンループ", StringComparison.Ordinal))
        {
            if (Passed(0.5))
                RaiseSoundEffect((wet ? 20 : 17) + Random.Next(3));
        }
        else if (name.Contains("ぐちゅぐちゅループ", StringComparison.Ordinal) || name.Contains("グラインドループ", StringComparison.Ordinal)
                 || name.Contains("オナニーループ", StringComparison.Ordinal))
        {
            if (Passed(0.5))
                RaiseSoundEffect((wet ? 26 : 23) + Random.Next(3));
        }
        else if (name.Contains("絶頂(中だし)", StringComparison.Ordinal) || name.EndsWith("淫乱絶頂", StringComparison.Ordinal))
        {
            // Every 10 frames, 7.5 % of the clip's length times.
            int pulses = (int)(length * 0.075);
            for (int k = 0; k < pulses; k++)
                if (clip.Start + k * 10 is var at && at > before && at <= now)
                    RaiseSoundEffect(SoundEffects.Climax);
        }
        else if ((name.StartsWith("Z待機→", StringComparison.Ordinal) && name.EndsWith("1待機", StringComparison.Ordinal) | name.EndsWith("2待機", StringComparison.Ordinal))
                 || (name.Contains('→') && name.EndsWith("う！待機", StringComparison.Ordinal)))
        {
            if (Passed(0.75))
                RaiseSoundEffect(SoundEffects.Push);
        }
    }

    protected override void OnClip(AnimationClipInfo clip)
    {
        string name = clip.Name;
        if (name == "F待機")
            RaiseSoundEffect(SoundEffects.Outside);
        bool climax = name.Contains("アクション絶頂", StringComparison.Ordinal) || name.Contains("絶頂(中だし)", StringComparison.Ordinal)
            || name.EndsWith("淫乱絶頂", StringComparison.Ordinal);
        if (climax && !SquirtVisible && Squirt is { Animator.Body: { } spray } && Girl.Model.Rig.Find("o01_N_Sio") is int sio and >= 0)
        {
            Squirt.Animator.Placement = Girl.Animator.WorldOf(sio);
            Squirt.Animator.Invalidate();
            spray.Play(1);
        }
        SquirtVisible = climax;
        if (name.StartsWith("F待機(", StringComparison.Ordinal) && !name.Contains('→') && Drops is { Animator.Body: { } drops } && TargetFrame(name) is int frame)
        {
            // The drop lands on the target: its world position, standing up; the exe's placement is not decoded.
            var at = Girl.Animator.WorldOf(frame).Translation;
            Drops.Animator.Placement = Matrix4x4.CreateRotationX(-MathF.PI / 2) * Matrix4x4.CreateTranslation(at);
            Drops.Animator.Invalidate();
            drops.Play(1);
            DropsVisible = true;
            RaiseSoundEffect(36 + Random.Next(3));
        }
        else if (DropsVisible && name.StartsWith('Z'))
            DropsVisible = false;
        if (name.Length >= 2 && name[0] is 'A' or 'B' && name[1] is '1' or '2')
            Inserted = name[..2];
        else if (name.StartsWith('Z') || name.StartsWith('F'))
            Inserted = null;
        // After coming inside, the fluid runs out during the afterglow (ek05_0P: clips 1..3 for position A, 11..13 for
        // B; little, usual, a lot by how many times he came) and stays until she moves again.
        if (name.Contains("事後(中だし)ループ", StringComparison.Ordinal) && Liquid?.Animator.Body is { } liquid)
        {
            liquid.Play((name[0] == 'B' ? 10 : 0) + Math.Clamp(_inside, 1, 3));
            PutLiquidUnderCrotch();
            LiquidVisible = true;
        }
        else if (LiquidVisible && (name.Contains("ピストン", StringComparison.Ordinal) || name.StartsWith('Z') || name.Contains("→Z", StringComparison.Ordinal)))
            LiquidVisible = false;
        if (Inserted is { } p && name == $"{p}待機" && (_queuedStrokes > 0 || Continuous))
        {
            _queuedStrokes = Math.Max(0, _queuedStrokes - 1);
            Stroke();
        }
    }

    /// <summary>The spray of her climax (ek06: a frame and its particles, see the .ptcl beside it), or null.</summary>
    public Partner? Squirt { get; private set; }

    /// <summary>Whether the spray plays now: during her climaxes (アクション絶頂, 絶頂(中だし), 淫乱絶頂).</summary>
    public bool SquirtVisible { get; private set; }

    /// <summary>The girl's frame where he comes outside, by the F待機(…) target.</summary>
    int? TargetFrame(string clip)
    {
        string frame = clip switch
        {
            _ when clip.Contains("(胸)", StringComparison.Ordinal) => "o01_N_Mune",
            _ when clip.Contains("(尻)", StringComparison.Ordinal) => "o01_N_Siri03_L",
            _ when clip.Contains("(脚)", StringComparison.Ordinal) => "o01_N_Momo_L",
            _ when clip.Contains("(顔)", StringComparison.Ordinal) => "o01_N_Kuti_A05",
            _ => "o01_N_Sio",
        };
        int index = Girl.Model.Rig.Find(frame);
        return index >= 0 ? index : null;
    }

    /// <summary>From her mood standby: to Z待機 first, then <paramref name="then"/>; false when not in her mood standby.</summary>
    bool FromMood(string then)
    {
        if (Clip != _mood)
            return false;
        After = ("Z待機", then);
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
            After = ("Z待機", $"Z待機→{target}待機");
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
    /// When <see cref="CoupleSession.Excitement"/> is full the stroke ends at う！待機 instead.
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
        _inside++;
        RaiseCame(true);
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
            After = ("F待機", $"F待機→F待機({target})");
        RaiseCame(false);
        return true;
    }

    /// <summary>
    /// A press on her at standby, by the hidden touch area hit (the o01_A_* meshes listed in la02 like the oil scene's
    /// la01): face or head キス, breasts 乳もみ (a nipple while kneading: ブラずらし), crotch front 股間いじり(前),
    /// buttocks, hips or crotch back パンツずらし. Pressing the touch going on gives a small (sometimes big) reaction.
    /// The exe's own choice from la02 is not decoded. Returns false when nothing happened.
    /// </summary>
    public bool TouchAt(in Ray ray)
    {
        if (State != HState.Standby)
            return false;
        (string Frame, float Distance)? best = null;
        foreach (var mesh in Girl.Model.Meshes)
        {
            if (mesh.PartName != "body" || !mesh.FrameName.StartsWith("o01_A_", StringComparison.Ordinal))
                continue;
            if (MeshPicking.Raycast(ray, mesh, Girl.Animator.WorldOf(mesh.FrameIndex)) is { } hit && (best is null || hit.Distance < best.Value.Distance))
                best = (mesh.FrameName, hit.Distance);
        }
        if (best is not { Frame: var frame })
            return false;
        string? touch = frame.Contains("Kao", StringComparison.Ordinal) || frame.Contains("Atama", StringComparison.Ordinal) ? "キス"
            : frame.Contains("Titi", StringComparison.Ordinal) && Clip.StartsWith("乳もみ", StringComparison.Ordinal) ? "ブラずらし"
            : frame.Contains("Titi", StringComparison.Ordinal) || frame.Contains("Mune", StringComparison.Ordinal) ? "乳もみ"
            : frame.Contains("KokanF", StringComparison.Ordinal) ? "股間いじり(前)"
            : frame.Contains("KokanB", StringComparison.Ordinal) || frame.Contains("Siri", StringComparison.Ordinal) || frame.Contains("Kosi", StringComparison.Ordinal) ? "パンツずらし"
            : null;
        if (touch is null)
            return false;
        string c = Clip;
        if (c == $"{touch}ループ")
        {
            // Each reaction raises her; full, the touches that have one end in her climax (→アクション絶頂, then
            // アクション事後 back to Z待機 by themselves).
            Excitement = Math.Min(1, Excitement + StrokeExcitement * 2);
            if (Excitement >= 1 && Play($"{touch}→アクション絶頂"))
            {
                Excitement = 0;
                return true;
            }
            return Play($"{touch}{(Random.Next(4) == 0 ? "大" : "小")}反応");
        }
        if (c.EndsWith("ループ", StringComparison.Ordinal) && c != "Z待機")
        {
            // Another touch going on: back to standby first.
            string current = c[..^3];
            After = ("Z待機", $"Z待機→{touch}");
            return Play($"{current}→Z待機");
        }
        return Touch(touch);
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
