using System.Numerics;
using OpenDelulu.Engine.Assets;

namespace OpenDelulu.Engine.Animation;

/// <summary>
/// Turns the eyeballs towards a target, like the game's "目で見る" (look with the eyes) behaviour from the
/// face's .eyes file: the eye frames <c>o01_N_Me_L/R</c> rotate inside fixed limits (20° sideways, 10° up/down,
/// the first values stored in .eyes).
/// <para>
/// The iris is not on the eyeball's +Z axis (about 12° outwards and 9° down for girl 01), so its direction
/// is located once: the point of the front surface whose UV is the texture centre, seen from the eye frame's
/// origin (the pivot).
/// </para>
/// </summary>
public sealed class EyeGaze
{
    const float MaxYaw = 0.349f;   // 20°
    const float MaxPitch = 0.1745f; // 10°

    sealed record Eye(int Frame, int Parent, Vector3 RestIris);

    readonly List<Eye> _eyes = [];

    EyeGaze() { }

    /// <summary>Null when the model has no eye frames.</summary>
    public static EyeGaze? TryCreate(RenderModel model)
    {
        var gaze = new EyeGaze();
        var rig = model.Rig;
        foreach (string side in new[] { "L", "R" })
        {
            int frame = rig.Find($"o01_N_Me_{side}");
            var mesh = model.Meshes.FirstOrDefault(m => m.FrameName == $"o01_P_Me_{side}");
            if (frame < 0 || mesh is null || rig.Parents[frame] < 0)
                continue;

            // The eyeball rotates about the eye frame's origin. Some faces model the eyeball around the mesh
            // origin (girl 01), others in head coordinates with the offset in the mesh frame (girl 03), so the
            // centre is the eye frame's origin seen from mesh space.
            if (!Matrix4x4.Invert(rig.BindLocal[mesh.FrameIndex], out var parentToMesh))
                continue;
            var centre = parentToMesh.Translation;
            // Iris = the point whose UV is the texture centre, on the front of the eyeball.
            if (IrisPoint(mesh, centre) is not { } iris)
                continue;

            // Express its direction in the eye frame's parent (head) space: mesh frame * eye frame, rotation only.
            var meshToParent = rig.BindLocal[mesh.FrameIndex] * rig.BindLocal[frame];
            var rest = Vector3.Normalize(Vector3.TransformNormal(iris - centre, meshToParent));
            gaze._eyes.Add(new Eye(frame, rig.Parents[frame], rest));
        }
        return gaze._eyes.Count > 0 ? gaze : null;
    }

    /// <summary>
    /// Rotates the eye frames in <paramref name="local"/> towards <paramref name="target"/> (model space).
    /// <paramref name="world"/> must hold the world matrices of the current pose.
    /// </summary>
    public void Apply(Span<Matrix4x4> local, ReadOnlySpan<Matrix4x4> world, Vector3 target)
    {
        foreach (var eye in _eyes)
        {
            if (!Matrix4x4.Invert(world[eye.Parent], out var toParent))
                continue;
            var eyePosition = (local[eye.Frame] * world[eye.Parent]).Translation;
            var wanted = Vector3.TransformNormal(target - eyePosition, toParent);
            if (wanted.LengthSquared() < 1e-8f)
                continue;
            wanted = Vector3.Normalize(wanted);

            float yaw = Math.Clamp(Yaw(wanted) - Yaw(eye.RestIris), -MaxYaw, MaxYaw);
            float pitch = Math.Clamp(Pitch(wanted) - Pitch(eye.RestIris), -MaxPitch, MaxPitch);

            // Direct3D row vectors: RotationY(+a) turns +Z towards +X; RotationX(+b) turns +Z towards -Y.
            var turn = Matrix4x4.CreateRotationX(-pitch) * Matrix4x4.CreateRotationY(yaw);
            var bind = local[eye.Frame];
            var rotation = bind with { M41 = 0, M42 = 0, M43 = 0 };
            local[eye.Frame] = rotation * turn * Matrix4x4.CreateTranslation(bind.Translation);
        }
    }

    /// <summary>Barycentric point of the front-facing triangle containing UV (0.5, 0.5), in mesh space.</summary>
    static Vector3? IrisPoint(RenderMesh mesh, Vector3 centre)
    {
        var v = mesh.SourceVertices;
        var target = new Vector2(0.5f);
        Vector3? best = null;
        float bestZ = float.MinValue;
        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            ref readonly var a = ref v[mesh.Indices[i]];
            ref readonly var b = ref v[mesh.Indices[i + 1]];
            ref readonly var c = ref v[mesh.Indices[i + 2]];
            // Barycentric coordinates of the target UV.
            var v0 = b.UV - a.UV;
            var v1 = c.UV - a.UV;
            var v2 = target - a.UV;
            float den = v0.X * v1.Y - v1.X * v0.Y;
            if (MathF.Abs(den) < 1e-9f)
                continue;
            float wb = (v2.X * v1.Y - v1.X * v2.Y) / den;
            float wc = (v0.X * v2.Y - v2.X * v0.Y) / den;
            float wa = 1 - wb - wc;
            if (wa < -1e-4f || wb < -1e-4f || wc < -1e-4f)
                continue;
            var point = a.Position * wa + b.Position * wb + c.Position * wc;
            float z = point.Z - centre.Z;
            if (z > bestZ) // the back of the eyeball can share UVs; keep the front-most hit
            {
                bestZ = z;
                best = point;
            }
        }
        return bestZ > 0 ? best : null;
    }

    static float Yaw(Vector3 d) => MathF.Atan2(d.X, d.Z);

    static float Pitch(Vector3 d) => MathF.Asin(Math.Clamp(d.Y, -1f, 1f));
}
