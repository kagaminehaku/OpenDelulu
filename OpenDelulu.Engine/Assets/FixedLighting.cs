using System.Numerics;
using OpenDelulu.Formats.Scene;

namespace OpenDelulu.Engine.Assets;

/// <summary>One Direct3D 9 light of an .xl file, in world space (or camera space when <see cref="CameraRelative"/>).</summary>
public sealed record LightSource(LightType Type, Vector3 Diffuse, Vector3 Ambient, Vector3 Position, Vector3 Direction,
    float Range, float Attenuation0, float Attenuation1, float Attenuation2, bool CameraRelative);

/// <summary>The lights an object is drawn with: the render state's ambient colour and up to eight lights.</summary>
public sealed record LightSet(Vector3 Ambient, IReadOnlyList<LightSource> Lights)
{
    public bool HasCameraLights => Lights.Any(l => l.CameraRelative);

    /// <summary>
    /// The lights of an .xl file as the exe sets them (gi_lib, 0x545345): position from the light frame's matrix, direction
    /// its +Z, range and attenuation as stored. Stages use point lights far away (the sun, a sky light); characters use the
    /// four camera-relative directional lights of bo00_NN.xl.
    /// </summary>
    public static LightSet FromXl(XlFile xl) => new(
        new Vector3(xl.SceneAmbient.X, xl.SceneAmbient.Y, xl.SceneAmbient.Z),
        xl.Lights.Select(l => new LightSource(l.Type,
            new Vector3(l.Diffuse.X, l.Diffuse.Y, l.Diffuse.Z), new Vector3(l.Ambient.X, l.Ambient.Y, l.Ambient.Z),
            l.Matrix.IsIdentity ? l.Position : l.Matrix.Translation,
            l.Direction.LengthSquared() > 1e-8f ? Vector3.Normalize(l.Direction) : Vector3.UnitZ,
            l.Range, l.Attenuation0, l.Attenuation1, l.Attenuation2, l.CameraRelative)).ToList());
}

/// <summary>
/// Direct3D 9 fixed-function vertex lighting, as the original draws its models:
/// <c>colour = emissive + ambient_mat × (render-state ambient + Σ att·ambient_light) + Σ att·diffuse_mat·diffuse_light·max(0, N·L)</c>,
/// clamped to 0..1 per vertex, then multiplied with the texture. Point lights fade by 1 / (a0 + a1·d + a2·d²) and stop at
/// their range. Specular is left out (its render state is not known).
/// </summary>
public static class FixedLighting
{
    /// <summary>
    /// Fills <paramref name="colors"/> (RGBA per vertex, A = 1 meaning "lit") from the mesh's current vertices (world
    /// space, <see cref="RenderModel.VertexStride"/> floats each). <paramref name="tint"/> multiplies the result (stage
    /// light colour, wardrobe colours).
    /// </summary>
    public static void Light(RenderMesh mesh, LightSet lights, Matrix4x4 cameraToWorld, Vector3 tint, Span<float> colors)
    {
        var vertices = mesh.Vertices;
        int count = vertices.Length / RenderModel.VertexStride;
        var emissive = new Vector3(mesh.Emissive.X, mesh.Emissive.Y, mesh.Emissive.Z);
        var ambient = new Vector3(mesh.Ambient.X, mesh.Ambient.Y, mesh.Ambient.Z);
        var diffuse = new Vector3(mesh.Diffuse.X, mesh.Diffuse.Y, mesh.Diffuse.Z);

        // World-space light parameters for this frame.
        Span<Vector3> direction = stackalloc Vector3[Math.Min(8, lights.Lights.Count)];
        Span<Vector3> position = stackalloc Vector3[direction.Length];
        for (int l = 0; l < direction.Length; l++)
        {
            var light = lights.Lights[l];
            direction[l] = light.CameraRelative ? Vector3.Normalize(Vector3.TransformNormal(light.Direction, cameraToWorld)) : light.Direction;
            position[l] = light.CameraRelative ? Vector3.Transform(light.Position, cameraToWorld) : light.Position;
        }

        for (int v = 0; v < count; v++)
        {
            int o = v * RenderModel.VertexStride;
            var p = new Vector3(vertices[o], vertices[o + 1], vertices[o + 2]);
            var n = new Vector3(vertices[o + 3], vertices[o + 4], vertices[o + 5]);
            float nl = n.LengthSquared();
            if (nl > 1e-12f)
                n /= MathF.Sqrt(nl);
            var ambientSum = lights.Ambient;
            var diffuseSum = Vector3.Zero;
            for (int l = 0; l < direction.Length; l++)
            {
                var light = lights.Lights[l];
                Vector3 toLight;
                float attenuation = 1;
                if (light.Type == LightType.Directional)
                {
                    toLight = -direction[l];
                }
                else
                {
                    toLight = position[l] - p;
                    float d = toLight.Length();
                    if (light.Range > 0 && d > light.Range)
                        continue;
                    float denominator = light.Attenuation0 + light.Attenuation1 * d + light.Attenuation2 * d * d;
                    attenuation = denominator > 1e-6f ? 1 / denominator : 1;
                    toLight = d > 1e-6f ? toLight / d : Vector3.UnitY;
                }
                ambientSum += light.Ambient * attenuation;
                float lambert = Vector3.Dot(n, toLight);
                if (lambert > 0)
                    diffuseSum += light.Diffuse * (lambert * attenuation);
            }
            var color = Vector3.Clamp(emissive + ambient * ambientSum + diffuse * diffuseSum, Vector3.Zero, Vector3.One) * tint;
            int c = v * 4;
            colors[c] = color.X;
            colors[c + 1] = color.Y;
            colors[c + 2] = color.Z;
            colors[c + 3] = 1;
        }
    }
}
