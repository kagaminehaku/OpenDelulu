using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Engine.Characters;

namespace OpenDelulu.Engine.H;

/// <summary>
/// A fetish scene of the Plus edition (10_hap; the H menu's paizuri, footjob and 69): hwBB_00_0N.xa / hm…_00_0N.xa of
/// ld01 rows 132..134, nine clips in a chain: K待機 → ピストン01(ル) → ピストン02(ル) → 絶頂事後 → 事後(ル) → K待機
/// ("ル" = loop). The loops hold until the player goes on (a press: the next pace) or he comes (excitement, faster at
/// the second pace). Expressions and voices: lh04_00_0N, lp04_LL_01.
/// </summary>
public sealed class HapSession : CoupleSession
{
    const string Standby = "K待機", Slow = "ピストン01(ル)", Fast = "ピストン02(ル)", Afterglow = "事後(ル)";

    double _afterLeft;

    HapSession(Parts parts, int kind) : base(parts) => Kind = kind;

    /// <summary>1 paizuri, 2 footjob, 3 69 (ld01 rows 132..134).</summary>
    public int Kind { get; }

    /// <summary>Excitement per second at the first pace; the second counts 2.5 times.</summary>
    public double ExcitementRate { get; set; } = 0.03;

    /// <summary>0 waiting, 1 and 2 the paces, 3 the climax and afterglow.</summary>
    public int Pace => Clip switch
    {
        Standby => 0,
        Slow or "K待機→ピストン01(ル)" => 1,
        Fast or "ピストン01→ピストン02(ル)" => 2,
        _ => 3,
    };

    public static bool IsKindInstalled(GameFileSystem fs, CharacterAssembler assembler, int girl, int kind) =>
        IsInstalled(fs, assembler, girl, 131 + kind);

    public static HapSession Start(GameFileSystem fs, CharacterAssembler assembler, Character girl, int kind, int level,
        VoiceLibrary? voices = null, AudioSystem? audio = null)
    {
        kind = Math.Clamp(kind, 1, 3);
        var parts = Load(fs, assembler, girl, 131 + kind, $"lh04_00_{kind + 1:00}_00_00.lst", $"lp04_{Math.Clamp(level, 4, 5):00}_01_00_00.lst",
            voices, audio);
        return new HapSession(parts, kind);
    }

    protected override void Begin() => Play(Standby);

    protected override void OnClip(AnimationClipInfo clip)
    {
        // Loops loop; the clips between them follow "next".
        bool hold = clip.Name is Standby or Slow or Fast or Afterglow;
        Body.FollowNext = !hold;
        if (clip.Name == Afterglow)
            _afterLeft = 3;
    }

    /// <summary>The next pace (waiting → first → second).</summary>
    public bool Faster() => Clip switch
    {
        Standby => Play("K待機→ピストン01(ル)"),
        Slow => Play("ピストン01→ピストン02(ル)"),
        _ => false,
    };

    public override void Update(double seconds)
    {
        string c = Clip;
        if (c is Slow or Fast)
        {
            Excitement = Math.Min(1, Excitement + seconds * ExcitementRate * (c == Fast ? 2.5 : 1));
            if (Excitement >= 1)
            {
                if (c == Slow)
                    Faster();
                else if (Play("ピストン02→絶頂事後(ル)"))
                {
                    Excitement = 0;
                    RaiseCame(true);
                }
            }
        }
        // The afterglow loops a few seconds, then back to waiting.
        if (c == Afterglow && (_afterLeft -= seconds) <= 0)
            Play("事後(ル)→K待機");
    }
}
