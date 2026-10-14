using System.Numerics;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Formats.Character;

namespace OpenDelulu.Engine.Animation;

/// <summary>
/// Secondary motion for hair (.kys chains) and breasts/buttocks (.tty joints): each bone's tip is a particle that
/// trails the animated pose (Verlet with damping, a spring back to the pose, gravity), keeps the bone length and
/// stays within an angle of the pose; the bone is then turned towards it. Runs in world space at a fixed 60 Hz,
/// so moving or placing the character makes things swing.
/// <para>
/// The data gives per-bone values whose exact units are unknown (.kys: 0.002–0.003, 3.0, 0.45; .tty: 0.003, angle
/// limit 0.175 rad, gravity −5.8…−9.8); they are mapped to spring/damping/gravity below and tuned by eye.
/// </para>
/// </summary>
public sealed class SpringBones
{
    const double Step = 1.0 / 60;
    const float UnitsPerMeter = 12.5f; // characters are ~20 units tall

    sealed class Bone(int frame, Vector3 tipLocal, float pull, float damping, float gravity, float maxAngle)
    {
        public readonly int Frame = frame;
        public readonly Vector3 TipLocal = tipLocal;
        public readonly float Pull = pull, Damping = damping, Gravity = gravity, MaxAngle = maxAngle;
        public Vector3 Position, Previous;
        public bool Ready;
    }

    readonly List<List<Bone>> _chains = [];
    readonly int[] _subtreeEnd;
    readonly Rig _rig;
    double _accumulator;

    public SpringBones(Rig rig)
    {
        _rig = rig;
        // Frames are stored depth-first, so a frame's descendants follow it contiguously.
        var depth = new int[rig.Count];
        for (int i = 0; i < rig.Count; i++)
            depth[i] = rig.Parents[i] < 0 ? 0 : depth[rig.Parents[i]] + 1;
        _subtreeEnd = new int[rig.Count];
        for (int i = 0; i < rig.Count; i++)
        {
            int end = i + 1;
            while (end < rig.Count && depth[end] > depth[i])
                end++;
            _subtreeEnd[i] = end;
        }
    }

    public int BoneCount => _chains.Sum(c => c.Count);

    /// <summary>True while something still swings (keep rendering frames).</summary>
    public bool IsMoving { get; private set; }

    /// <summary>Hair: each chain runs from the root joint to the tip locator (the last entry has no bone of its own).</summary>
    public void AddHair(KysFile kys)
    {
        foreach (var chain in kys.Chains)
        {
            var bones = new List<Bone>();
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                int frame = _rig.Find(chain[i].Name), tip = _rig.Find(chain[i + 1].Name);
                if (frame < 0 || tip < 0 || TipOffset(frame, tip) is not { } offset)
                    continue;
                var b = chain[i];
                bones.Add(new Bone(frame, offset,
                    pull: Math.Clamp(b.Stiffness * 40f, 0.02f, 0.6f),
                    damping: Math.Clamp(b.Damping * 0.25f, 0.02f, 0.9f),
                    gravity: b.Param2 * 3f * UnitsPerMeter,
                    maxAngle: 0.6f));
            }
            if (bones.Count > 0)
                _chains.Add(bones);
        }
    }

    /// <summary>Breasts/buttocks: every J_ joint swings towards its first N_ locator, with the given parameter set.</summary>
    public void AddJiggle(TtyFile tty, int set = 1)
    {
        if (tty.Sets.Count == 0)
            return;
        var parameters = tty.Sets[Math.Clamp(set, 0, tty.Sets.Count - 1)];
        float stiffness = parameters.Float(0), maxAngle = parameters.Float(4), gravity = parameters.Float(10);
        if (!float.IsFinite(maxAngle) || maxAngle <= 0)
            maxAngle = 0.175f;
        var bones = new List<Bone>();
        for (int i = 0; i < tty.Joints.Count; i++)
        {
            if (!tty.Joints[i].StartsWith("o01_J_", StringComparison.Ordinal))
                continue;
            int frame = _rig.Find(tty.Joints[i]);
            int tip = i + 1 < tty.Joints.Count && tty.Joints[i + 1].StartsWith("o01_N_", StringComparison.Ordinal) ? _rig.Find(tty.Joints[i + 1]) : -1;
            if (frame < 0 || tip < 0 || TipOffset(frame, tip) is not { } offset)
                continue;
            bones.Add(new Bone(frame, offset,
                pull: Math.Clamp(stiffness * 60f, 0.05f, 0.6f),
                damping: 0.12f,
                gravity: Math.Abs(gravity) * UnitsPerMeter * 0.5f,
                maxAngle: maxAngle * 2.5f));
        }
        // Joints of one breast/buttock are independent: one chain each.
        foreach (var bone in bones)
            _chains.Add([bone]);
    }

    /// <summary>The tip's position in the bone frame's space, from the bind pose.</summary>
    Vector3? TipOffset(int frame, int tip)
    {
        var world = new Matrix4x4[_rig.Count];
        _rig.ComputeWorld(_rig.BindLocal, world);
        if (!Matrix4x4.Invert(world[frame], out var inverse))
            return null;
        var offset = Vector3.Transform(world[tip].Translation, inverse);
        return offset.LengthSquared() > 1e-8f ? offset : null;
    }

    /// <summary>Forgets the motion (after teleporting the character).</summary>
    public void Reset()
    {
        foreach (var bone in _chains.SelectMany(c => c))
            bone.Ready = false;
    }

    /// <summary>Advances the simulation and bends the bones in <paramref name="local"/>/<paramref name="world"/>.</summary>
    public void Apply(Span<Matrix4x4> local, Span<Matrix4x4> world, double seconds)
    {
        _accumulator = Math.Min(_accumulator + seconds, Step * 4);
        int steps = (int)(_accumulator / Step);
        _accumulator -= steps * Step;
        float dt = (float)Step;
        float motion = 0;

        foreach (var chain in _chains)
        {
            foreach (var bone in chain)
            {
                var joint = world[bone.Frame].Translation;
                var target = Vector3.Transform(bone.TipLocal, world[bone.Frame]);
                float length = Vector3.Distance(joint, target);
                if (!bone.Ready || Vector3.DistanceSquared(bone.Position, target) > length * length * 16)
                {
                    bone.Position = bone.Previous = target;
                    bone.Ready = true;
                }

                for (int s = 0; s < steps; s++)
                {
                    var velocity = (bone.Position - bone.Previous) * (1 - bone.Damping);
                    bone.Previous = bone.Position;
                    bone.Position += velocity + new Vector3(0, -bone.Gravity * dt * dt, 0);
                    bone.Position += (target - bone.Position) * bone.Pull;
                    // Keep the bone length and stay within the angle limit around the pose.
                    var direction = Vector3.Normalize(bone.Position - joint);
                    var rest = Vector3.Normalize(target - joint);
                    float angle = MathF.Acos(Math.Clamp(Vector3.Dot(direction, rest), -1f, 1f));
                    if (angle > bone.MaxAngle)
                        direction = Slerp(rest, direction, bone.MaxAngle / angle);
                    bone.Position = joint + direction * length;
                }
                motion = MathF.Max(motion, Vector3.Distance(bone.Position, bone.Previous));

                // Turn the bone so its tip points at the particle (rotation about the joint, in world space).
                var from = Vector3.Normalize(target - joint);
                var to = Vector3.Normalize(bone.Position - joint);
                if (Vector3.Dot(from, to) > 0.99999f)
                    continue;
                var rotation = Matrix4x4.CreateFromQuaternion(FromTo(from, to));
                var turned = world[bone.Frame] * Matrix4x4.CreateTranslation(-joint) * rotation * Matrix4x4.CreateTranslation(joint);
                int parent = _rig.Parents[bone.Frame];
                if (parent >= 0 && Matrix4x4.Invert(world[parent], out var toParent))
                    local[bone.Frame] = turned * toParent;
                else
                    local[bone.Frame] = turned;
                // Update this bone's subtree so the next bone of the chain starts from the bent pose.
                for (int i = bone.Frame; i < _subtreeEnd[bone.Frame]; i++)
                    world[i] = _rig.Parents[i] < 0 ? local[i] : local[i] * world[_rig.Parents[i]];
            }
        }
        IsMoving = motion > 0.0005f;
    }

    static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        var axis = Vector3.Cross(from, to);
        float w = 1 + Vector3.Dot(from, to);
        return Quaternion.Normalize(new Quaternion(axis, w));
    }

    static Vector3 Slerp(Vector3 a, Vector3 b, float t)
    {
        float dot = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        float theta = MathF.Acos(dot) * t;
        var relative = Vector3.Normalize(b - a * dot);
        return a * MathF.Cos(theta) + relative * MathF.Sin(theta);
    }
}
