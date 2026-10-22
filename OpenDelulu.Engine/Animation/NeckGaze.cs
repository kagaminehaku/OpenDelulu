using System.Numerics;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Formats.Character;

namespace OpenDelulu.Engine.Animation;

/// <summary>
/// Turns the head (o01_J_Head) towards a target, like the "首と目で見る" (look with head and eyes) behaviour of the body's
/// .neck file (bo00_00_00_00_00.neck): up to 50° sideways and 40° down (maxYaw / maxPitch), 30° up (its second value), and
/// only while the target is within 140° of her front (its first value); behind that she looks ahead again. The default
/// behaviour of the .neck file does not move the head, so <see cref="Active"/> starts off; dialogue turns it on.
/// The turn eases in over about a quarter of a second (the behaviour's 250 ms timings) and is added to the animated pose.
/// </summary>
public sealed class NeckGaze
{
    readonly int _head;
    readonly int _parent;
    readonly Vector3 _forward;
    float _yaw, _pitch;

    NeckGaze(int head, int parent, Vector3 forward)
    {
        _head = head;
        _parent = parent;
        _forward = forward;
    }

    public float MaxYaw { get; init; } = 0.873f;
    public float MaxPitchDown { get; init; } = 0.698f;
    public float MaxPitchUp { get; init; } = 0.524f;
    /// <summary>Largest angle of the target from her front she still turns to.</summary>
    public float Range { get; init; } = 2.443f;
    /// <summary>Time to cover most of a turn.</summary>
    public float TurnSeconds { get; init; } = 0.25f;

    /// <summary>The head follows the target (else it eases back to the animation).</summary>
    public bool Active { get; set; }

    /// <summary>The head is still turning (or turned): keep re-skinning.</summary>
    public bool IsMoving => Active || MathF.Abs(_yaw) > 1e-3f || MathF.Abs(_pitch) > 1e-3f;

    /// <summary>Null when the model has no head joint.</summary>
    public static NeckGaze? TryCreate(RenderModel model, LookFile? look = null)
    {
        var rig = model.Rig;
        int head = rig.Find(look?.Frames.FirstOrDefault() ?? "o01_J_Head");
        if (head < 0 || rig.Parents[head] < 0)
            return null;
        var world = new Matrix4x4[rig.Count];
        rig.ComputeWorld(rig.BindLocal, world);
        if (!Matrix4x4.Invert(world[rig.Parents[head]], out var toParent))
            return null;
        var forward = Vector3.TransformNormal(Vector3.UnitZ, toParent);
        if (forward.LengthSquared() < 1e-8f)
            return null;
        // Behaviour 3 首と目で見る: limits and range; its 250 ms timings give the easing.
        var behavior = look?.Behaviors.FirstOrDefault(b => b.Id == 3);
        return new NeckGaze(head, rig.Parents[head], Vector3.Normalize(forward))
        {
            MaxYaw = behavior?.MaxYaw ?? 0.873f,
            MaxPitchDown = behavior?.MaxPitch ?? 0.698f,
            MaxPitchUp = look?.Default?.Value2 is > 0 and var up ? up : 0.524f,
            Range = behavior?.Value1 is > 0 and var range ? range : 2.443f,
        };
    }

    /// <summary>
    /// Adds the head turn to <paramref name="local"/>; <paramref name="world"/> holds the current pose's world matrices.
    /// </summary>
    public void Apply(Span<Matrix4x4> local, ReadOnlySpan<Matrix4x4> world, Vector3? target, double seconds)
    {
        var parentWorld = world[_parent];
        var pivot = (local[_head] * parentWorld).Translation;
        // Her front, in world space (the model's +Z through the neck): angles are measured from it, around the world's
        // vertical, so a sideways look does not tip the head down with a tilted neck.
        var front = Vector3.TransformNormal(_forward, parentWorld);
        front = front.LengthSquared() > 1e-8f ? Vector3.Normalize(front) : Vector3.UnitZ;
        float wantYaw = 0, wantPitch = 0;
        if (Active && target is { } t && Vector3.DistanceSquared(t, pivot) > 1e-8f)
        {
            var wanted = Vector3.Normalize(t - pivot);
            float yaw = WrapAngle(Yaw(wanted) - Yaw(front));
            if (MathF.Abs(yaw) <= Range)
            {
                wantYaw = Math.Clamp(yaw, -MaxYaw, MaxYaw);
                wantPitch = Math.Clamp(Pitch(wanted) - Pitch(front), -MaxPitchDown, MaxPitchUp);
            }
        }
        float k = 1 - MathF.Exp(-(float)seconds * 3f / MathF.Max(0.01f, TurnSeconds));
        _yaw += (wantYaw - _yaw) * k;
        _pitch += (wantPitch - _pitch) * k;
        if (MathF.Abs(_yaw) < 1e-4f && MathF.Abs(_pitch) < 1e-4f)
            return;
        // World rotation about the head's pivot: yaw around the vertical, then pitch around the turned face's side axis.
        var yawTurn = Matrix4x4.CreateRotationY(_yaw);
        var turnedFront = Vector3.TransformNormal(front, yawTurn);
        var side = Vector3.Cross(Vector3.UnitY, turnedFront);
        var turn = yawTurn;
        if (side.LengthSquared() > 1e-8f)
            turn *= Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(side), -_pitch);
        var aroundPivot = Matrix4x4.CreateTranslation(-pivot) * turn * Matrix4x4.CreateTranslation(pivot);
        if (Matrix4x4.Invert(parentWorld, out var toParent))
            local[_head] = local[_head] * parentWorld * aroundPivot * toParent;
    }

    static float WrapAngle(float a) => MathF.IEEERemainder(a, 2 * MathF.PI);

    static float Yaw(Vector3 d) => MathF.Atan2(d.X, d.Z);

    static float Pitch(Vector3 d) => MathF.Asin(Math.Clamp(d.Y, -1f, 1f));
}
