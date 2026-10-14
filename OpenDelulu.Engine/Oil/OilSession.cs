using System.Numerics;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Formats.Xa;
using OpenDelulu.Formats.Xx;

namespace OpenDelulu.Engine.Oil;

/// <summary>
/// The sun-oil scene (exe module 07_oil): the girl in one of her oil poses (onGG_PP.xa), the player's hand (te01) and
/// oil spread by rubbing. Touching a hidden "o01_A_*" area of the la01 table makes her wait for touches on that part
/// of the body and react; breasts, buttocks and crotch are refused until the intimacy level unlocks them.
/// <para>
/// Clip numbers are those of the onGG_PP.xa files (the same ~198 clips for every girl and pose). How the exe times
/// reactions and picks voice rows (lh01 tables) is not decoded yet: reactions come every few seconds of rubbing, and
/// voices use lp rows 1..17 while rubbing, 21 when refusing and 91/92 (moans) with the reactions.
/// </para>
/// </summary>
public sealed class OilSession
{
    // onGG_PP.xa
    public const int ClipIdle = 7;                 // Kループ
    const int ClipToIdleFromUpperBody = 64;        // 触り待機(上半身)→K
    const int ClipRefuseBreasts = 85, ClipRefuseCrotch = 88, ClipRefuseButtocks = 91;
    const int ClipBreastMassage = 109, ClipButtocksMassage = 144, ClipCrotchTouch = 154;
    // te01.xa
    const int HandRubBody = 9, HandBreastMassage = 13;

    // Voice rows of lp01_LL.
    static readonly int[] RubbingVoices = Enumerable.Range(1, 17).ToArray();
    const int RefusalVoice = 21, SmallReactionVoice = 91, BigReactionVoice = 92;

    const float BrushRadius = 1.4f;
    /// <summary>A new area has to stay under the pointer this long before she changes what she does (area borders).</summary>
    const double SwitchDelay = 0.35;

    readonly OilTables _tables;
    readonly VoiceLibrary? _voices;
    readonly AudioSystem? _audio;
    readonly Dictionary<string, RenderMesh> _areaMeshes = new(StringComparer.Ordinal);
    readonly Random _random = new();

    OilTouchArea? _area;
    (int Start, int Loop, int Small, int Big, int Back)? _state;
    OilTouchArea? _pending;
    double _pendingTime;
    Vector3 _point, _lastPoint;
    double _nextReaction, _nextVoice;
    bool _touching, _refused;

    public Character Girl { get; }
    public OilPose Pose { get; }
    public AnimationPlayer Body { get; }
    public OilCoating Coating { get; }
    public int Level { get; }

    /// <summary>The player's hand (te01) and its animator (rub motions are morphs on the hand's timeline); null if missing.</summary>
    public RenderModel? Hand { get; }
    public CharacterAnimator? HandAnimator { get; }
    public bool HandVisible { get; private set; }

    /// <summary>The touched area, or null.</summary>
    public OilTouchArea? Area => _touching ? _area : null;

    /// <summary>Raised when she refuses a touch (for a message).</summary>
    public event Action<OilTouchArea>? Refused;

    OilSession(Character girl, OilPose pose, AnimationPlayer body, OilTables tables, int level, VoiceLibrary? voices, AudioSystem? audio,
        RenderModel? hand, CharacterAnimator? handAnimator)
    {
        Girl = girl;
        Pose = pose;
        Body = body;
        _tables = tables;
        Level = level;
        _voices = voices;
        _audio = audio;
        Hand = hand;
        HandAnimator = handAnimator;
        Coating = new OilCoating(girl.Model);
        foreach (var mesh in girl.Model.Meshes)
            if (mesh.PartName == "body" && mesh.FrameName.StartsWith("o01_A_", StringComparison.Ordinal))
                _areaMeshes.TryAdd(mesh.FrameName, mesh);
        body.Play(ClipIdle);
    }

    /// <summary>
    /// Loads an oil pose and the hand (can run on a worker thread); <see cref="Attach"/> then puts her in the pose.
    /// Throws if the pose's animation cannot be read.
    /// </summary>
    public static OilSession Start(GameFileSystem fs, CharacterAssembler assembler, Character girl, OilPose pose, int level,
        VoiceLibrary? voices = null, AudioSystem? audio = null)
    {
        var body = assembler.LoadBodyAnimation(girl.Model, pose.Animation)
            ?? throw new InvalidDataException($"{pose.Animation.File} has no animation.");
        var tables = OilTables.Load(fs, assembler.Database.TableArchive, level);

        RenderModel? hand = null;
        CharacterAnimator? handAnimator = null;
        if (tables.Hand is { } handRef && fs.ExistsReferenced(handRef.Archive, handRef.File))
        {
            hand = RenderModel.FromXx(XxReader.Read(fs.ReadReferenced(handRef.Archive, handRef.File)), handRef.File);
            handAnimator = new CharacterAnimator(hand) { EyesFollowTarget = false };
            if (tables.HandAnimation is { } xaRef && fs.ExistsReferenced(xaRef.Archive, xaRef.File))
            {
                var xa = XaReader.Read(fs.ReadReferenced(xaRef.Archive, xaRef.File));
                if (xa.Animation is { } animation)
                    handAnimator.Body = new AnimationPlayer(hand.Rig, animation, xaRef.File) { FollowNext = false };
                if (xa.Morphs is { } morphs)
                    handAnimator.Morph = new TimelineMorph(hand, morphs);
            }
        }
        return new OilSession(girl, pose, body, tables, level, voices, audio, hand, handAnimator);
    }

    /// <summary>Gives her the pose's animation (on the thread that renders).</summary>
    public void Attach()
    {
        Girl.Animator.Body = Body;
        Girl.Animator.Physics?.Reset();
    }

    /// <summary>The touch area under the ray (nearest), or null.</summary>
    public (OilTouchArea Area, MeshHit Hit)? PickArea(in Ray ray)
    {
        (OilTouchArea, MeshHit)? best = null;
        foreach (var area in _tables.Areas)
        {
            if (!_areaMeshes.TryGetValue(area.Frame, out var mesh))
                continue;
            if (MeshPicking.Raycast(ray, mesh, Girl.Animator.WorldOf(mesh.FrameIndex)) is { } hit
                && (best is null || hit.Distance < best.Value.Item2.Distance))
                best = (area, hit);
        }
        return best;
    }

    /// <summary>The visible body surface under the ray (skin or swimsuit), or null.</summary>
    public MeshHit? PickBody(in Ray ray)
    {
        MeshHit? best = null;
        foreach (var mesh in Girl.Model.Meshes)
        {
            if (mesh.PartName != "body" || !mesh.Visible || RenderModel.IsHelperMaterial(mesh.MaterialName))
                continue;
            if (MeshPicking.Raycast(ray, mesh) is { } hit && (best is null || hit.Distance < best.Value.Distance))
                best = hit;
        }
        return best;
    }

    /// <summary>Starts touching at the ray; false when it misses her (the caller may orbit the camera instead).</summary>
    /// <param name="up">The screen's up direction in world space (the hand's fingers point that way).</param>
    public bool Begin(in Ray ray, Vector3 up)
    {
        if (PickArea(ray) is not { } picked)
            return false;
        _touching = true;
        _refused = false;
        _pending = null;
        _nextReaction = 2.5 + _random.NextDouble() * 2.5;
        _nextVoice = 0.6;
        Touch(picked.Area, PickBody(ray) ?? picked.Hit, up, immediate: true);
        _lastPoint = _point;
        return true;
    }

    public void Move(in Ray ray, Vector3 up)
    {
        // After a refusal the hand stays off until the player lets go.
        if (!_touching || _refused || PickArea(ray) is not { } picked)
            return;
        Touch(picked.Area, PickBody(ray) ?? picked.Hit, up, immediate: false);
    }

    public void End()
    {
        if (!_touching)
            return;
        _touching = false;
        HandVisible = false;
        if (_state is { } state)
            Body.Play(state.Back);
        _state = null;
        _area = null;
        _pending = null;
    }

    void Touch(OilTouchArea area, MeshHit hit, Vector3 up, bool immediate)
    {
        // Sliding over a border into an area with another reaction: wait until the pointer stays there.
        if (!immediate && _area is { } current && (area.Refuses || StateFor(area) != StateFor(current)))
        {
            if (_pending != area)
            {
                _pending = area;
                _pendingTime = 0;
            }
            if (_pendingTime < SwitchDelay)
            {
                // Keep rubbing where she lets you; a place she would refuse gets no oil meanwhile.
                if (!area.Refuses)
                {
                    PlaceHand(hit, up);
                    _point = hit.Point;
                }
                return;
            }
        }
        _pending = null;
        _point = hit.Point;
        if (area.Refuses)
        {
            Refuse(area);
            _refused = true;
            _area = area;
            HandVisible = false;
            return;
        }

        var state = StateFor(area);
        if (_state != state)
        {
            _state = state;
            Body.Play(state.Start);
            _nextReaction = 2.5 + _random.NextDouble() * 2.5;
        }
        if (_area?.Special != area.Special || _area?.HandClip != area.HandClip || !HandVisible)
            HandAnimator?.Body?.Play(area.Special >= 0 && area.Region == OilRegion.Breasts ? HandBreastMassage : area.HandClip > 0 ? area.HandClip : HandRubBody);
        _area = area;
        PlaceHand(hit, up);
    }

    /// <summary>What she does while touched there: start, loop, small and big reaction, back to idle.</summary>
    static (int Start, int Loop, int Small, int Big, int Back) StateFor(OilTouchArea area)
    {
        if (area.Unlocked && area.Special >= 0)
        {
            int start = area.Region switch
            {
                OilRegion.Breasts => ClipBreastMassage,
                OilRegion.Buttocks => ClipButtocksMassage,
                OilRegion.Crotch => ClipCrotchTouch,
                _ => 0,
            };
            if (start > 0)
                return (start, start + 1, start + 2, start + 3, ClipToIdleFromUpperBody);
        }
        int wait = area.GirlClip > 0 ? area.GirlClip : 60;
        return (wait, wait + 1, wait + 2, wait + 3, wait + 4);
    }

    void Refuse(OilTouchArea area)
    {
        _state = null;
        Body.Play(area.Region switch
        {
            OilRegion.Breasts => ClipRefuseBreasts,
            OilRegion.Crotch => ClipRefuseCrotch,
            _ => ClipRefuseButtocks,
        });
        Say(RefusalVoice, interrupt: true);
        Refused?.Invoke(area);
    }

    void PlaceHand(MeshHit hit, Vector3 up)
    {
        if (HandAnimator is null)
            return;
        // te01: fingers along +Y, palm facing -Z, wrist near the origin. Palm onto the skin, fingers up the screen.
        var normal = hit.Normal;
        var fingers = up - normal * Vector3.Dot(up, normal);
        if (fingers.LengthSquared() < 1e-6f)
            fingers = Vector3.Cross(normal, Vector3.UnitX);
        fingers = Vector3.Normalize(fingers);
        var right = Vector3.Normalize(Vector3.Cross(fingers, normal));
        var position = hit.Point + normal * 0.12f - fingers * 0.2f;
        HandAnimator.Placement = new Matrix4x4(
            right.X, right.Y, right.Z, 0,
            fingers.X, fingers.Y, fingers.Z, 0,
            normal.X, normal.Y, normal.Z, 0,
            position.X, position.Y, position.Z, 1);
        HandVisible = true;
    }

    /// <summary>Advances the scene: spreads oil under the hand, reactions and voices. Returns true if the skin changed.</summary>
    public bool Update(double seconds)
    {
        _pendingTime += seconds;
        if (!_touching || _state is not { } state)
            return false;
        float moved = Vector3.Distance(_point, _lastPoint);
        _lastPoint = _point;

        // Rubbing spreads more oil than holding still.
        bool changed = Coating.Paint(_point, BrushRadius, (float)seconds * 0.8f + moved * 0.35f);

        _nextReaction -= seconds;
        if (_nextReaction <= 0 && Body.Current?.Index == state.Loop)
        {
            bool big = _random.NextDouble() < 0.25;
            Body.Play(big ? state.Big : state.Small);
            Say(big ? BigReactionVoice : SmallReactionVoice, interrupt: false);
            _nextReaction = 3 + _random.NextDouble() * 3;
        }
        _nextVoice -= seconds;
        if (_nextVoice <= 0)
        {
            Say(RubbingVoices[_random.Next(RubbingVoices.Length)], interrupt: false);
            _nextVoice = 6 + _random.NextDouble() * 4;
        }
        return changed;
    }

    /// <summary>Plays a random line of an lp row, unless she is already speaking (or <paramref name="interrupt"/>).</summary>
    void Say(int row, bool interrupt)
    {
        if (_voices is null || _audio is null || !_tables.Voices.TryGetValue(row, out var files))
            return;
        if (!interrupt && Girl.Animator.Speech is not null)
            return;
        string name = string.Format(System.Globalization.CultureInfo.InvariantCulture, files[_random.Next(files.Count)], Girl.Girl.Id);
        var line = _voices.For(Girl.Girl.Id).FirstOrDefault(l => string.Equals(l.File, name, StringComparison.OrdinalIgnoreCase));
        if (line is not null)
            Girl.Animator.Speech = _voices.Speak(line, _audio);
    }
}
