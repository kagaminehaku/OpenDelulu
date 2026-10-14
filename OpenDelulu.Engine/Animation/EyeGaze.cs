using System.Numerics;
using OpenDelulu.Engine.Assets;

namespace OpenDelulu.Engine.Animation;

/// <summary>
/// Turns the eyeballs towards a target, like the game's "目で見る" (look with the eyes) behaviour from the
/// face's .eyes file: the eye frames <c>o01_N_Me_L/R</c> rotate inside fixed limits (20° sideways, 10° down as
/// stored in .eyes; only ~3° up, see <see cref="MaxPitchUp"/>).
/// <para>
/// The bind pose is the straight-ahead look: the eyeballs are modelled with the iris (texture centre) about
/// 20° below and 10° outside the eye frame's axis, and that is how the face is meant to look at someone in
/// front of it. Aiming the texture centre itself at the target turned the eyes up by ~30° ("rolled back").
/// So the eyes only turn by the target's angle from the face's forward direction, clamped to the limits.
/// </para>
/// </summary>
public sealed class EyeGaze
{
    const float MaxYaw = 0.349f;      // 20°
    const float MaxPitchDown = 0.1745f; // 10°
    // Up is limited much more: these eyes sit high under the upper lid, and even 10° up shows white below
    // the iris (reads as eyes rolled back).
    const float MaxPitchUp = 0.05f;   // ~3°

    /// <param name="Forward">The face's forward direction (model +Z) in the eye frame's parent space, bind pose.</param>
    sealed record Eye(int Frame, int Parent, Vector3 Forward);

    readonly List<Eye> _eyes = [];

    EyeGaze() { }

    /// <summary>Null when the model has no eye frames.</summary>
    public static EyeGaze? TryCreate(RenderModel model)
    {
        var gaze = new EyeGaze();
        var rig = model.Rig;
        var world = new Matrix4x4[rig.Count];
        rig.ComputeWorld(rig.BindLocal, world);
        foreach (string side in new[] { "L", "R" })
        {
            int frame = rig.Find($"o01_N_Me_{side}");
            if (frame < 0 || rig.Parents[frame] < 0 || !Matrix4x4.Invert(world[rig.Parents[frame]], out var toParent))
                continue;
            var forward = Vector3.TransformNormal(Vector3.UnitZ, toParent);
            if (forward.LengthSquared() < 1e-8f)
                continue;
            gaze._eyes.Add(new Eye(frame, rig.Parents[frame], Vector3.Normalize(forward)));
        }
        return gaze._eyes.Count > 0 ? gaze : null;
    }

    /// <summary>How much each eye turns towards the target itself instead of the common direction (0 = parallel).</summary>
    const float Convergence = 0.25f;

    /// <summary>
    /// Rotates the eye frames in <paramref name="local"/> towards <paramref name="target"/> (model space).
    /// <paramref name="world"/> must hold the world matrices of the current pose.
    /// Both eyes share one gaze direction (from between the eyes), with only a little convergence:
    /// full convergence looks cross-eyed on these large anime eyes at close range.
    /// </summary>
    public void Apply(Span<Matrix4x4> local, ReadOnlySpan<Matrix4x4> world, Vector3 target)
    {
        var middle = Vector3.Zero;
        foreach (var eye in _eyes)
            middle += (local[eye.Frame] * world[eye.Parent]).Translation;
        middle /= _eyes.Count;

        foreach (var eye in _eyes)
        {
            if (!Matrix4x4.Invert(world[eye.Parent], out var toParent))
                continue;
            var eyePosition = (local[eye.Frame] * world[eye.Parent]).Translation;
            var shared = Vector3.Normalize(target - middle);
            var own = Vector3.Normalize(target - eyePosition);
            var wanted = Vector3.TransformNormal(Vector3.Lerp(shared, own, Convergence), toParent);
            if (wanted.LengthSquared() < 1e-8f)
                continue;
            wanted = Vector3.Normalize(wanted);

            // Angles of the target from the face's forward direction; behind the head the eyes stay put.
            if (Vector3.Dot(wanted, eye.Forward) <= 0)
                continue;
            float yaw = Math.Clamp(WrapAngle(Yaw(wanted) - Yaw(eye.Forward)), -MaxYaw, MaxYaw);
            float pitch = Math.Clamp(Pitch(wanted) - Pitch(eye.Forward), -MaxPitchDown, MaxPitchUp);

            // Direct3D row vectors: RotationY(+a) turns +Z towards +X; RotationX(+b) turns +Z towards -Y.
            var turn = Matrix4x4.CreateRotationX(-pitch) * Matrix4x4.CreateRotationY(yaw);
            var bind = local[eye.Frame];
            var rotation = bind with { M41 = 0, M42 = 0, M43 = 0 };
            local[eye.Frame] = rotation * turn * Matrix4x4.CreateTranslation(bind.Translation);
        }
    }

    static float WrapAngle(float a) => MathF.IEEERemainder(a, 2 * MathF.PI);

    static float Yaw(Vector3 d) => MathF.Atan2(d.X, d.Z);

    static float Pitch(Vector3 d) => MathF.Asin(Math.Clamp(d.Y, -1f, 1f));
}
