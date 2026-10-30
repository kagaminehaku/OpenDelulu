using System.Numerics;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Formats.Xa;
using OpenDelulu.Formats.Xx;

namespace OpenDelulu.Engine.Oil;

/// <summary>What a press on her does: rub with the hand, or pour oil from the bottle (the original's Z/X keys).</summary>
public enum OilMode
{
    Hand,
    Pour,
}

/// <summary>
/// The sun-oil scene (exe module 07_oil): the girl in one of her oil poses (onBB_PP.xa), the player's hands (te01/te02),
/// the oil drips (ek01..03) and oil spread on the skin.
/// <list type="bullet">
/// <item>Rubbing: a press on a hidden "o01_A_*" area of la01 makes her wait for touches there and react; breasts,
/// buttocks and crotch are refused until the intimacy level unlocks them. Unlocked special areas start the lf01 action
/// (kneading…): the hand then holds on to a body locator instead of following the pointer.</item>
/// <item>Pouring: the stream falls while the button is held and leaves a puddle; she reacts by body part (head, upper or
/// lower body) or refuses.</item>
/// <item>Each clip she enters applies its lh01 row: eye/mouth expressions and a voice line by priority.</item>
/// </list>
/// Clip numbers are those of the onBB_PP.xa files (the same ~198 clips for every girl and pose).
/// </summary>
public sealed class OilSession
{
    // onBB_PP.xa
    public const int ClipIdle = 7;                 // Kループ
    const int ClipRefuseBreasts = 85, ClipRefuseCrotch = 88, ClipRefuseButtocks = 91;
    const int ClipPourHead = 36, ClipPourUpper = 39, ClipPourLower = 42;
    // Refusing oil on the breasts / crotch / buttocks: start, then a loop until the way back.
    static readonly (int Start, int Loop, int Back) PourRefuseBreasts = (45, 46, 49), PourRefuseCrotch = (50, 51, 54),
        PourRefuseButtocks = (55, 56, 59);
    // te01/te02.xa
    const int HandRubBody = 9;
    // ek01..03.xa: the stream, then one of three puddles.
    const int DripStream = 1;
    static readonly int[] DripPuddles = [2, 3, 4];

    const float BrushRadius = 1.4f;
    /// <summary>A new area has to stay under the pointer this long before she changes what she does (area borders).</summary>
    const double SwitchDelay = 0.35;

    sealed class Prop(RenderModel model, CharacterAnimator animator)
    {
        public readonly RenderModel Model = model;
        public readonly CharacterAnimator Animator = animator;
        public bool Visible;
    }

    readonly OilTables _tables;
    readonly VoiceLibrary? _voices;
    readonly AudioSystem? _audio;
    readonly Dictionary<string, RenderMesh> _areaMeshes = new(StringComparer.Ordinal);
    readonly Random _random = new();
    readonly List<Prop> _props = [];
    readonly Prop? _rightHand, _leftHand;
    readonly List<Prop> _drips = [];

    OilTouchArea? _area;
    (int Start, int Loop, int Small, int Big, int Back)? _state;
    OilAction? _action;
    bool _actionRunning;
    Prop? _actionHand;
    int _actionLocator = -1;
    OilTouchArea? _pending;
    double _pendingTime;
    Vector3 _point, _lastPoint;
    double _nextReaction, _refusing, _puddleLeft;
    (int Start, int Loop, int Back)? _pourRefusal;
    bool _touching, _refused, _pouring;
    int _speakingPriority;

    public Character Girl { get; }
    public OilPose Pose { get; }
    public AnimationPlayer Body { get; }
    public OilCoating Coating { get; }
    public int Level { get; }
    public OilMode Mode { get; set; }

    /// <summary>Which oil (0..2 = ek01..03, the original's three oils).</summary>
    public int OilKind { get; set; }
    public int OilKinds => _drips.Count;

    /// <summary>Body type letter of the pose files (onBB: 01 = A, 02 = B, 03 = C), used to pick lf01 variants.</summary>
    public char BodyType { get; }

    /// <summary>Extra objects to draw with her: right hand, left hand, then the drips. Show only the visible ones.</summary>
    public IReadOnlyList<(RenderModel Model, CharacterAnimator Animator)> Props => _props.Select(p => (p.Model, p.Animator)).ToList();
    public bool IsPropVisible(int index) => _props[index].Visible;

    /// <summary>How many of <see cref="Props"/> are hands (the first ones).</summary>
    public int HandCount => (_rightHand is null ? 0 : 1) + (_leftHand is null ? 0 : 1);

    /// <summary>The touched area, or null.</summary>
    public OilTouchArea? Area => _touching ? _area : null;

    /// <summary>The special touch going on (kneading…), or null.</summary>
    public OilAction? Action => _touching ? _action : null;

    /// <summary>Raised when she refuses a touch or oil there (for a message).</summary>
    public event Action<OilTouchArea>? Refused;

    /// <summary>
    /// Raised when an unlocked special area is pressed: the actions possible there (the original shows their buttons,
    /// st00_01_*); start one with <see cref="StartAction"/>.
    /// </summary>
    public event Action<OilTouchArea, IReadOnlyList<OilAction>>? ActionsOffered;

    OilSession(Character girl, OilPose pose, AnimationPlayer body, OilTables tables, int level, VoiceLibrary? voices, AudioSystem? audio,
        Prop? rightHand, Prop? leftHand, IEnumerable<Prop> drips)
    {
        Girl = girl;
        Pose = pose;
        Body = body;
        _tables = tables;
        Level = level;
        _voices = voices;
        _audio = audio;
        _rightHand = rightHand;
        _leftHand = leftHand;
        _drips.AddRange(drips);
        if (rightHand is not null)
            _props.Add(rightHand);
        if (leftHand is not null)
            _props.Add(leftHand);
        _props.AddRange(_drips);
        Coating = new OilCoating(girl.Model);
        foreach (var mesh in girl.Model.Meshes)
            if (mesh.PartName == "body" && mesh.FrameName.StartsWith("o01_A_", StringComparison.Ordinal))
                _areaMeshes.TryAdd(mesh.FrameName, mesh);
        string file = pose.Animation.File;
        BodyType = file.Length >= 4 && int.TryParse(file.AsSpan(2, 2), out int type) && type is >= 1 and <= 3 ? (char)('A' + type - 1) : 'A';
        body.ClipChanged += OnClipChanged;
        body.Play(ClipIdle);
    }

    /// <summary>
    /// Loads an oil pose, the hands and the drips (can run on a worker thread); <see cref="Attach"/> then puts her in the
    /// pose. Throws if the pose's animation cannot be read.
    /// </summary>
    public static OilSession Start(GameFileSystem fs, CharacterAssembler assembler, Character girl, OilPose pose, int level,
        VoiceLibrary? voices = null, AudioSystem? audio = null)
    {
        var body = assembler.LoadBodyAnimation(girl.Model, pose.Animation)
            ?? throw new InvalidDataException($"{pose.Animation.File} has no animation.");
        var tables = OilTables.Load(fs, assembler.Database.TableArchive, level, pose.Number);

        Prop? Load(AssetRef? modelRef, AssetRef? xaRef)
        {
            if (modelRef is not { } m || !fs.ExistsReferenced(m.Archive, m.File))
                return null;
            var model = RenderModel.FromXx(XxReader.Read(fs.ReadReferenced(m.Archive, m.File)), m.File);
            var animator = new CharacterAnimator(model) { EyesFollowTarget = false };
            if (xaRef is { } x && fs.ExistsReferenced(x.Archive, x.File))
            {
                var xa = XaReader.Read(fs.ReadReferenced(x.Archive, x.File));
                if (xa.Animation is { } animation)
                    animator.Body = new AnimationPlayer(model.Rig, animation, x.File) { FollowNext = false };
                if (xa.Morphs is { } morphs)
                    animator.Morph = new TimelineMorph(model, morphs);
            }
            return new Prop(model, animator);
        }

        var right = Load(tables.Hand, tables.HandAnimation);
        var left = Load(tables.LeftHand, tables.LeftHandAnimation);
        var drips = tables.Drips.Select(d => Load(d.Model, d.Animation)).OfType<Prop>().ToList();
        // Breast / top / bottom deformations of her body variant that follow the hand during special actions.
        TimelineMorph? deform = null;
        if (girl.Girl.Deformations.ElementAtOrDefault(girl.Cut - 1) is { } xmRef)
        {
            try
            {
                var xm = XmFile.Read(fs.ReadReferenced(xmRef.Archive, xmRef.File));
                deform = new TimelineMorph(girl.Model, xm.Morphs, checkRest: true);
                if (deform.ChannelCount == 0)
                    deform = null;
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
            }
        }
        return new OilSession(girl, pose, body, tables, level, voices, audio, right, left, drips) { _deform = deform };
    }

    TimelineMorph? _deform;

    /// <summary>The .xm deformation follows the acting hand's timeline; without an action it rests.</summary>
    double DeformTime() => _action is not null && _actionHand?.Animator.Body is { } hand ? hand.Time : 0;

    /// <summary>
    /// The side an action works on, from its name: 両 both (A), 左 left (L), 右 right (R), 前 / 下 the front of the bottom
    /// (F), 後ろ its back (B). A side without keys in the current hand clip falls back to the "both" set (乳寄せ, the
    /// bottom's 左/右 actions).
    /// </summary>
    bool IsDeformActive(string set)
    {
        if (_action is not { } action || _actionHand?.Animator.Body is not { Current: { } clip } || _deform is not { } deform)
            return false;
        string name = action.Name;
        string wanted = name.Contains('両') ? "A" : name.StartsWith("前", StringComparison.Ordinal) || name.StartsWith("下", StringComparison.Ordinal) ? "F"
            : name.StartsWith("後ろ", StringComparison.Ordinal) ? "B" : name.Contains('左') ? "L" : name.Contains('右') ? "R" : "A";
        int cut = set.LastIndexOf('_');
        if (cut < 0)
            return false;
        string family = set[..cut], side = set[(cut + 1)..];
        double from = clip.Start - 1, to = clip.End + 1;
        if (!deform.HasDeformation(set, from, to))
            return false;
        if (side == wanted)
            return true;
        return side == "A" && !deform.HasDeformation($"{family}_{wanted}", from, to);
    }

    /// <summary>Gives her the pose's animation (on the thread that renders).</summary>
    public void Attach()
    {
        Girl.Animator.Body = Body;
        if (_deform is { } deform)
        {
            deform.ActiveSet = IsDeformActive;
            Girl.Animator.Morph = deform;
            Girl.Animator.MorphClock = DeformTime;
        }
        Girl.Animator.Physics?.Reset();
        if (Body.Current is { } clip)
            OnClipChanged(clip);
    }

    /// <summary>Takes the scene's deformation off her body (the session ends).</summary>
    public void Detach()
    {
        if (ReferenceEquals(Girl.Animator.Morph, _deform))
        {
            Girl.Animator.Morph = null;
            Girl.Animator.MorphClock = null;
        }
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

    // ------------------------------------------------------------------ input

    /// <summary>Starts touching (or pouring) at the ray; false when it misses her (the caller may orbit the camera).</summary>
    /// <param name="up">The screen's up direction in world space (the hand's fingers point that way).</param>
    public bool Begin(in Ray ray, Vector3 up)
    {
        // A press while an action runs stops it.
        if (_actionRunning)
        {
            End();
            return true;
        }
        if (PickArea(ray) is not { } picked)
            return false;
        _touching = true;
        _refused = false;
        _pending = null;
        _nextReaction = 2.5 + _random.NextDouble() * 2.5;
        var hit = PickBody(ray) ?? picked.Hit;
        if (Mode == OilMode.Pour)
            BeginPour(picked.Area, hit);
        else
            Touch(picked.Area, hit, up, immediate: true);
        _lastPoint = _point;
        return true;
    }

    public void Move(in Ray ray, Vector3 up)
    {
        // After a refusal the hand stays off until the player lets go; the stream follows the pointer.
        if (!_touching || _refused || PickArea(ray) is not { } picked)
            return;
        var hit = PickBody(ray) ?? picked.Hit;
        if (_pouring)
        {
            _point = hit.Point;
            PlaceDrip(hit);
            return;
        }
        Touch(picked.Area, hit, up, immediate: false);
    }

    public void End()
    {
        if (!_touching)
            return;
        _touching = false;
        if (_pouring)
        {
            _pouring = false;
            if (ActiveDrip is { } drip)
            {
                drip.Animator.Body?.Play(DripPuddles[_random.Next(DripPuddles.Length)]);
                _puddleLeft = 2.5;
            }
        }
        HideHands();
        if (_state is { } state)
            Body.Play(state.Back);
        _state = null;
        _area = null;
        _action = null;
        _actionHand = null;
        _actionRunning = false;
        _pending = null;
    }

    /// <summary>Starts an lf01 action chosen from <see cref="ActionsOffered"/>; it runs until the next press.</summary>
    public void StartAction(OilAction action)
    {
        End();
        _touching = true;
        _actionRunning = true;
        _refused = false;
        int start = action.GirlClip;
        _state = (start, start + 1, start + 2, start + 3, start + 4);
        Body.Play(start);
        _nextReaction = 2.5 + _random.NextDouble() * 2.5;
        StartHand(null, action);
        _lastPoint = _point;
    }

    /// <summary>The actions of an lf01 group that fit her body type and what she wears, one per button.</summary>
    public IReadOnlyList<OilAction> ActionsFor(int group)
    {
        var rows = _tables.Actions.Where(a => a.Group == group && a.HandClip > 0 && a.GirlClip > 0).ToList();
        string type = $"{BodyType}体型";
        if (rows.Any(a => a.Name.Contains("体型")))
            rows = rows.Where(a => a.Name.Contains(type)).ToList();
        // She still wears her swimsuit: no "without the top" or "nude" variants.
        rows = rows.Where(a => !a.Name.Contains("ブラなし") && !a.Name.Contains("全裸")).ToList();
        return rows.GroupBy(a => a.Button).Select(g => g.First()).ToList();
    }

    void HideHands()
    {
        foreach (var hand in new[] { _rightHand, _leftHand })
            if (hand is not null)
                hand.Visible = false;
    }

    // ------------------------------------------------------------------ rubbing

    void Touch(OilTouchArea area, MeshHit hit, Vector3 up, bool immediate)
    {
        // Sliding over a border into an area with another reaction: wait until the pointer stays there.
        if (!immediate && _area is { } current && (area.Refuses || StateFor(area).State != StateFor(current).State))
        {
            if (_pending != area)
            {
                _pending = area;
                _pendingTime = 0;
            }
            if (_pendingTime < SwitchDelay)
            {
                // Keep rubbing where she lets you; a place she would refuse gets no oil meanwhile.
                if (!area.Refuses && _action is null)
                {
                    PlaceHand(_rightHand, hit, up);
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
            HideHands();
            return;
        }

        // Pressing an unlocked special area offers its actions instead of rubbing.
        if (immediate && area.Unlocked && area.Special >= 0 && ActionsFor(area.Special) is { Count: > 0 } offered)
        {
            _touching = false;
            HideHands();
            ActionsOffered?.Invoke(area, offered);
            return;
        }

        var (state, action) = StateFor(area);
        if (_state != state)
        {
            _state = state;
            Body.Play(state.Start);
            _nextReaction = 2.5 + _random.NextDouble() * 2.5;
            StartHand(area, action);
        }
        _area = area;
        if (_action is null)
            PlaceHand(_rightHand, hit, up);
    }

    /// <summary>The hand for a touch: the right hand rubbing under the pointer, or the lf01 action's hand on its locator.</summary>
    void StartHand(OilTouchArea? area, OilAction? action)
    {
        HideHands();
        _action = action;
        _actionHand = null;
        _actionLocator = -1;
        if (action is not null)
        {
            var hand = action.Hand == 2 ? _leftHand ?? _rightHand : _rightHand;
            string locatorName = action.Hand == 2 && action.LeftLocator.StartsWith("o01_", StringComparison.Ordinal) ? action.LeftLocator : action.Locator;
            int locator = Girl.Model.Rig.Find(locatorName);
            if (hand is not null && locator >= 0)
            {
                _actionHand = hand;
                _actionLocator = locator;
                PlayLoop(hand, action.HandClip);
                FollowLocator();
                return;
            }
            _action = null;
        }
        _rightHand?.Animator.Body?.Play(area is { HandClip: > 0 } ? area.HandClip : HandRubBody);
    }

    /// <summary>Plays a hand clip and holds the loop it leads to ("…(ル)"), as long as the touch lasts.</summary>
    static void PlayLoop(Prop hand, int clip)
    {
        if (hand.Animator.Body is not { } player)
            return;
        var clips = player.Clips;
        var current = clips.FirstOrDefault(c => c.Index == clip);
        // Loops are "…(ル)" / "…ループ"; the hand-inside actions (ブラ入れ, パンツ入れ) hold "…中心" (centre) instead.
        static bool IsLoop(AnimationClipInfo c) => !c.Name.Contains('→')
            && (c.Name.EndsWith("(ル)", StringComparison.Ordinal) || c.Name.EndsWith("ループ", StringComparison.Ordinal)
                || c.Name.EndsWith("中心", StringComparison.Ordinal));
        for (int guard = 0; current is not null && guard < 6 && !IsLoop(current); guard++)
        {
            int next = current.Next;
            current = clips.FirstOrDefault(c => c.Index == next);
        }
        // No loop on the way: keep the action's own clip rather than ending on the default pose.
        player.Play(current is not null && IsLoop(current) ? current.Index : clip);
    }

    /// <summary>What she does while rubbed there: start, loop, small and big reaction, back to idle.</summary>
    static ((int Start, int Loop, int Small, int Big, int Back) State, OilAction? Action) StateFor(OilTouchArea area)
    {
        int wait = area.GirlClip > 0 ? area.GirlClip : 60;
        return ((wait, wait + 1, wait + 2, wait + 3, wait + 4), null);
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
        Refused?.Invoke(area);
    }

    static void PlaceHand(Prop? hand, MeshHit hit, Vector3 up)
    {
        if (hand is null)
            return;
        // te01: fingers along +Y, palm facing -Z, wrist near the origin. Palm onto the skin, fingers up the screen.
        var normal = hit.Normal;
        var fingers = up - normal * Vector3.Dot(up, normal);
        if (fingers.LengthSquared() < 1e-6f)
            fingers = Vector3.Cross(normal, Vector3.UnitX);
        fingers = Vector3.Normalize(fingers);
        var right = Vector3.Normalize(Vector3.Cross(fingers, normal));
        var position = hit.Point + normal * 0.12f - fingers * 0.2f;
        hand.Animator.Placement = new Matrix4x4(
            right.X, right.Y, right.Z, 0,
            fingers.X, fingers.Y, fingers.Z, 0,
            normal.X, normal.Y, normal.Z, 0,
            position.X, position.Y, position.Z, 1);
        hand.Visible = true;
    }

    /// <summary>An lf01 action's hand sits on its body locator (the hand model is made in the locator's space).</summary>
    void FollowLocator()
    {
        if (_actionHand is null || _actionLocator < 0)
            return;
        var world = Girl.Animator.WorldOf(_actionLocator);
        _actionHand.Animator.Placement = world;
        _actionHand.Visible = true;
        _point = world.Translation;
    }

    // ------------------------------------------------------------------ pouring

    Prop? ActiveDrip => _drips.Count > 0 ? _drips[Math.Clamp(OilKind, 0, _drips.Count - 1)] : null;

    void BeginPour(OilTouchArea area, MeshHit hit)
    {
        _point = hit.Point;
        _area = area;
        _state = null;
        if (area.Refuses)
        {
            var refusal = area.Region switch
            {
                OilRegion.Breasts => PourRefuseBreasts,
                OilRegion.Crotch => PourRefuseCrotch,
                _ => PourRefuseButtocks,
            };
            _pourRefusal = refusal;
            _refusing = 2.5;
            Body.Play(refusal.Start);
            _refused = true;
            Refused?.Invoke(area);
            return;
        }
        _pouring = true;
        Body.Play(area.Region switch
        {
            OilRegion.Head => ClipPourHead,
            OilRegion.LeftLeg or OilRegion.RightLeg or OilRegion.Buttocks or OilRegion.Crotch => ClipPourLower,
            _ => ClipPourUpper,
        });
        foreach (var drip in _drips)
            drip.Visible = false;
        if (ActiveDrip is { } active)
        {
            active.Animator.Body?.Play(DripStream);
            PlaceDrip(hit);
        }
    }

    /// <summary>The drip model stands on the skin: origin at the point, +Z along the normal (the stream rises 2.4 units).</summary>
    void PlaceDrip(MeshHit hit)
    {
        if (ActiveDrip is not { } drip)
            return;
        var normal = hit.Normal;
        var side = Vector3.Normalize(Vector3.Cross(normal, MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var forward = Vector3.Cross(side, normal);
        drip.Animator.Placement = new Matrix4x4(
            side.X, side.Y, side.Z, 0,
            forward.X, forward.Y, forward.Z, 0,
            normal.X, normal.Y, normal.Z, 0,
            hit.Point.X, hit.Point.Y, hit.Point.Z, 1);
        drip.Visible = true;
    }

    // ------------------------------------------------------------------ time

    /// <summary>Advances the scene: spreads oil, reactions, the action hand, puddles. Returns true if the skin changed.</summary>
    public bool Update(double seconds)
    {
        _pendingTime += seconds;

        if (_puddleLeft > 0 && (_puddleLeft -= seconds) <= 0 && !_pouring)
            foreach (var drip in _drips)
                drip.Visible = false;

        if (_pourRefusal is { } refusal && (_refusing -= seconds) <= 0)
        {
            if (Body.Current?.Index == refusal.Loop)
                Body.Play(refusal.Back);
            _pourRefusal = null;
        }

        if (_pouring)
            return Coating.Paint(_point, BrushRadius * 1.2f, (float)seconds * 2.5f);

        if (!_touching || _state is not { } state)
            return false;
        FollowLocator();
        float moved = Vector3.Distance(_point, _lastPoint);
        _lastPoint = _point;

        // Rubbing spreads more oil than holding still; a kneading hand spreads it around its locator.
        bool changed = Coating.Paint(_point, BrushRadius, (float)seconds * (_action is null ? 0.8f : 0.5f) + moved * 0.35f);

        _nextReaction -= seconds;
        if (_nextReaction <= 0 && Body.Current?.Index == state.Loop)
        {
            Body.Play(_random.NextDouble() < 0.25 ? state.Big : state.Small);
            _nextReaction = 3 + _random.NextDouble() * 3;
        }
        return changed;
    }

    // ------------------------------------------------------------------ lh01: expressions and voices

    void OnClipChanged(AnimationClipInfo clip)
    {
        if (!_tables.ClipEvents.TryGetValue(clip.Index, out var e))
            return;
        Girl.Animator.Face?.SetExpression(e.Eyes, e.Mouth);
        Girl.Animator.Invalidate();
        if (e.VoiceRow > 0)
            Say(e.VoiceRow, e.Priority);
    }

    /// <summary>Plays a random line of an lp row unless she is saying something more important.</summary>
    void Say(int row, int priority)
    {
        if (_voices is null || _audio is null || !_tables.Voices.TryGetValue(row, out var files))
            return;
        if (Girl.Animator.Speech is not null && priority < _speakingPriority)
            return;
        string name = string.Format(System.Globalization.CultureInfo.InvariantCulture, files[_random.Next(files.Count)], Girl.Girl.Id);
        var line = _voices.For(Girl.Girl.Id).FirstOrDefault(l => string.Equals(l.File, name, StringComparison.OrdinalIgnoreCase));
        if (line is null)
            return;
        Girl.Animator.Speech = _voices.Speak(line, _audio);
        _speakingPriority = priority;
    }
}
