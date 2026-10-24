using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Engine.Characters;

namespace OpenDelulu.Engine.H;

/// <summary>Where the fera menu's Part list (ds01_06 Zentai…Omakase) has her work.</summary>
public enum FeraPart
{
    Whole,
    Glans,
    Underside,
    Tip,
    Balls,
    /// <summary>Omakase: she changes by herself.</summary>
    Auto,
}

/// <summary>
/// The fera scene (09_fera): hwBB_00_01.xa / hm00_00_01.xa of ld01 row 131. Ten loops, by part (whole, glans, underside,
/// tip, balls) and hand or mouth, each with a calm "P" loop and a strong "G" one:
/// 片手コキ / フェラ, 亀頭手コキ / 亀頭含み, 裏筋なで / 裏筋舐め, 尿道擦り(グッ) / 尿道舐め(ペロ)(突付き),
/// 玉手コキ(転がし) / 玉舐め(ふくみ). フェラ前羞恥 / 淫乱 (her mood) leads to フェラ前待機, the standby. The clips
/// between loops are named "from→to" and end on the target by their "next", so the scene keeps a graph of them and walks
/// it towards the loop asked for. When he comes: 手コキF / フェラF → 男絶頂 → 口含み待機, then 飲み (swallow) or 手出し
/// (spit out into her hand), each with two endings, back to フェラ前待機. Expressions and voices: lh04_00_01, lp04_LL_01
/// (levels 4 and 5).
/// </summary>
public sealed class FeraSession : CoupleSession
{
    // P and G loops of each part, by hand / mouth.
    static readonly (string P, string G)[,] Loops =
    {
        { ("片手コキP", "片手コキG"), ("フェラP", "フェラG") },
        { ("亀頭手コキP", "亀頭手コキG"), ("亀頭含みP", "亀頭含みG") },
        { ("裏筋なでP", "裏筋なでG"), ("裏筋舐めP", "裏筋舐めG") },
        { ("尿道擦り", "尿道(グッ"), ("尿道舐め(ペロ", "尿道突付き") },
        { ("玉手コキ", "玉転がし"), ("玉舐め", "玉ふくみ") },
    };
    const string Standby = "フェラ前待機", HandFinish = "手コキF（片手", MouthFinish = "フェラF", InMouth = "口含み待機";

    // Loop index → (transition clip, loop it ends on).
    readonly Dictionary<int, List<(int Clip, int To)>> _graph = [];
    readonly HashSet<int> _loops = [];
    readonly string _mood;
    string? _target;
    bool _finishing, _holding;
    double _autoLeft;

    FeraSession(Parts parts, string mood) : base(parts)
    {
        _mood = mood;
        var loops = Body.Clips.Where(c => !c.IsStatic && c.Name.Length > 0 && !c.Name.Contains('→')).ToList();
        foreach (var l in loops)
            _loops.Add(l.Index);
        foreach (var t in Body.Clips.Where(c => !c.IsStatic && c.Name.Contains('→')))
        {
            string from = t.Name[..t.Name.IndexOf('→')];
            if (SourceLoop(loops, from) is not { } source || !_loops.Contains(t.Next))
                continue;
            if (!_graph.TryGetValue(source.Index, out var list))
                _graph[source.Index] = list = [];
            list.Add((t.Index, t.Next));
        }
    }

    /// <summary>
    /// The loop a transition starts from, from the part of its name before "→" (the names are not always the loop's
    /// exactly: 亀頭含み for 亀頭含みP, 裏筋舐めP(ペロ for 裏筋舐めP, フェラ前 for フェラ前待機).
    /// </summary>
    static AnimationClipInfo? SourceLoop(List<AnimationClipInfo> loops, string from)
    {
        if (loops.FirstOrDefault(l => l.Name == from) is { } exact)
            return exact;
        var longer = loops.Where(l => l.Name.StartsWith(from, StringComparison.Ordinal)).ToList();
        if (longer.Count > 0)
            return longer.FirstOrDefault(l => l.Name.EndsWith("待機", StringComparison.Ordinal)) ?? longer[0];
        return loops.Where(l => from.StartsWith(l.Name, StringComparison.Ordinal)).MaxBy(l => l.Name.Length);
    }

    public FeraPart Part { get; private set; } = FeraPart.Whole;
    public bool Mouth { get; private set; }

    /// <summary>The strong loop (the button held).</summary>
    public bool Strong { get; set; }

    /// <summary>At the end she swallows (Nomasu) or spits it out into her hand (Dasasu).</summary>
    public bool Swallow { get; set; } = true;

    /// <summary>She is working (not at the standby, not finishing).</summary>
    public bool Working => _target is not null && !_finishing;

    /// <summary>Excitement per second in a calm loop; the strong loops count 2.5 times.</summary>
    public double ExcitementRate { get; set; } = 0.025;

    public static FeraSession Start(GameFileSystem fs, CharacterAssembler assembler, Character girl, int level,
        VoiceLibrary? voices = null, AudioSystem? audio = null)
    {
        var parts = Load(fs, assembler, girl, 131, "lh04_00_01_00_00.lst", $"lp04_{Math.Clamp(level, 4, 5):00}_01_00_00.lst", voices, audio);
        return new FeraSession(parts, level >= 5 ? "フェラ前淫乱" : "フェラ前羞恥");
    }

    protected override void Begin()
    {
        if (!Play(_mood))
            Play(Standby);
    }

    /// <summary>Works on <paramref name="part"/> with the hand or the mouth (Omakase: a part she picks, changed now and then).</summary>
    public void Choose(FeraPart part, bool mouth)
    {
        Part = part;
        Mouth = mouth;
        _autoLeft = 0;
        if (part == FeraPart.Auto)
            PickAuto();
        else
            Aim(LoopName((int)part, mouth));
    }

    void PickAuto()
    {
        _autoLeft = 6 + Random.NextDouble() * 6;
        Aim(LoopName(Random.Next(5), Random.Next(2) == 1));
    }

    string LoopName(int part, bool mouth)
    {
        var (p, g) = Loops[part, mouth ? 1 : 0];
        return Strong ? g : p;
    }

    /// <summary>Back to フェラ前待機.</summary>
    public void Rest()
    {
        Part = FeraPart.Whole;
        _autoLeft = 0;
        _target = null;
        if (!_finishing)
            Walk(Standby);
    }

    void Aim(string loop)
    {
        _target = loop;
        if (!_finishing)
            Walk(loop);
    }

    /// <summary>One step from the current loop towards <paramref name="goal"/> (nothing in the middle of a transition).</summary>
    void Walk(string goal)
    {
        if (Body.Current is not { } now || !_loops.Contains(now.Index) || !Clips.TryGetValue(goal, out int target) || now.Index == target)
            return;
        // Breadth-first over the transitions; the first one of the shortest path.
        var first = new Dictionary<int, int> { [now.Index] = -1 };
        var queue = new Queue<int>([now.Index]);
        while (queue.Count > 0)
        {
            int loop = queue.Dequeue();
            foreach (var (clip, to) in _graph.GetValueOrDefault(loop) ?? [])
            {
                if (first.ContainsKey(to))
                    continue;
                first[to] = first[loop] < 0 ? clip : first[loop];
                if (to == target)
                {
                    Body.Play(first[to]);
                    return;
                }
                queue.Enqueue(to);
            }
        }
    }

    public override void Update(double seconds)
    {
        if (_finishing || _target is null || Body.Current is not { } now)
            return;
        string current = now.Name;
        bool inPart = false, strong = false;
        for (int part = 0; part < 5; part++)
            for (int m = 0; m < 2; m++)
            {
                var (p, g) = Loops[part, m];
                inPart |= current == p || current == g;
                strong |= current == g;
            }
        if (inPart)
            Excitement = Math.Min(1, Excitement + seconds * ExcitementRate * (strong ? 2.5 : 1));
        if (Excitement >= 1 && inPart)
        {
            Finish(now.Name);
            return;
        }
        if (Part == FeraPart.Auto && (_autoLeft -= seconds) <= 0)
            PickAuto();
        // Strong or calm follows the button.
        if (inPart && _target != (Strong ? StrongOf(_target) : CalmOf(_target)))
            Aim(Strong ? StrongOf(_target) : CalmOf(_target));
    }

    static string StrongOf(string loop)
    {
        foreach (var (p, g) in Loops)
            if (loop == p)
                return g;
        return loop;
    }

    static string CalmOf(string loop)
    {
        foreach (var (p, g) in Loops)
            if (loop == g)
                return p;
        return loop;
    }

    /// <summary>He comes: the loop's way to 手コキF / フェラF, then 男絶頂 and 口含み待機 by themselves.</summary>
    void Finish(string loop)
    {
        bool mouth = false;
        for (int part = 0; part < 5; part++)
            mouth |= Loops[part, 1].P == loop || Loops[part, 1].G == loop;
        _finishing = true;
        _target = null;
        Excitement = 0;
        Walk(mouth ? MouthFinish : HandFinish);
        RaiseCame(mouth);
    }

    protected override void OnClip(AnimationClipInfo clip)
    {
        string name = clip.Name;
        if (name == InMouth)
        {
            _holding = true;
            return;
        }
        if (_holding)
        {
            // 口含み待機 is over ("next" goes straight to 飲み後待機): swallow (飲み) or spit out (手出し); each has two
            // endings, chosen at random.
            _holding = false;
            if (Clips.TryGetValue(Swallow ? "口含み待機（ループ→飲み" : "口含み待機（ループ→手出し", out int choice))
            {
                Body.Play(choice);
                return;
            }
        }
        if (name == "飲み後待機(ループ" && Random.Next(2) == 1 && Clips.TryGetValue("飲み(ループ→舌舐めずり", out int lick))
        {
            Body.Play(lick);
            return;
        }
        if (name == "手出し(ループ" && Random.Next(2) == 1 && Clips.TryGetValue("手出し(ループ→舐める", out int lap))
        {
            Body.Play(lap);
            return;
        }
        if (name == Standby && _finishing)
        {
            // Done: she waits, or goes on with what was chosen meanwhile.
            _finishing = false;
            if (_target is { } next)
                Walk(next);
            return;
        }
        if (!_finishing && _target is { } goal && _loops.Contains(clip.Index) && name != goal)
            Walk(goal);
    }
}
