using System.Numerics;

namespace OpenDelulu.Engine.Assets;

/// <summary>A ray in world space (e.g. from the camera through the pointer).</summary>
public readonly record struct Ray(Vector3 Origin, Vector3 Direction)
{
    public Vector3 At(float t) => Origin + Direction * t;
}

/// <summary>Where a ray hit a mesh: distance along the ray, point, interpolated normal, triangle.</summary>
public readonly record struct MeshHit(float Distance, Vector3 Point, Vector3 Normal, int Triangle);

/// <summary>Ray tests against triangle meshes (two-sided, like the game's meshes).</summary>
public static class MeshPicking
{
    /// <summary>Nearest hit on a mesh in its current (skinned) pose, or null.</summary>
    public static MeshHit? Raycast(in Ray ray, RenderMesh mesh) => Raycast(ray, mesh.Vertices, RenderModel.VertexStride, mesh.Indices);

    /// <summary>Nearest hit on a rigid mesh placed by <paramref name="world"/> (meshes that are not skinned, such as hidden helpers).</summary>
    public static MeshHit? Raycast(in Ray ray, RenderMesh mesh, Matrix4x4 world)
    {
        var source = mesh.SourceVertices;
        var vertices = new float[source.Length * 6];
        for (int v = 0; v < source.Length; v++)
        {
            var p = Vector3.Transform(source[v].Position, world);
            var n = Vector3.TransformNormal(source[v].Normal, world);
            vertices[v * 6] = p.X; vertices[v * 6 + 1] = p.Y; vertices[v * 6 + 2] = p.Z;
            vertices[v * 6 + 3] = n.X; vertices[v * 6 + 4] = n.Y; vertices[v * 6 + 5] = n.Z;
        }
        return Raycast(ray, vertices, 6, mesh.Indices);
    }

    /// <param name="vertices">Interleaved floats, position (3) then normal (3) at the start of each vertex.</param>
    static MeshHit? Raycast(in Ray ray, float[] vertices, int stride, ushort[] indices)
    {
        float best = float.MaxValue;
        int bestTriangle = -1;
        float bestU = 0, bestV = 0;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            var a = Position(vertices, stride, indices[t]);
            var b = Position(vertices, stride, indices[t + 1]);
            var c = Position(vertices, stride, indices[t + 2]);
            if (Intersect(ray, a, b, c, out float distance, out float u, out float v) && distance < best)
            {
                best = distance;
                bestTriangle = t / 3;
                bestU = u;
                bestV = v;
            }
        }
        if (bestTriangle < 0)
            return null;
        int i0 = indices[bestTriangle * 3], i1 = indices[bestTriangle * 3 + 1], i2 = indices[bestTriangle * 3 + 2];
        var normal = Normal(vertices, stride, i0) * (1 - bestU - bestV) + Normal(vertices, stride, i1) * bestU + Normal(vertices, stride, i2) * bestV;
        normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : -ray.Direction;
        // Two-sided meshes: report the side facing the ray.
        if (Vector3.Dot(normal, ray.Direction) > 0)
            normal = -normal;
        return new MeshHit(best, ray.At(best), normal, bestTriangle);
    }

    static Vector3 Position(float[] v, int stride, int i) => new(v[i * stride], v[i * stride + 1], v[i * stride + 2]);
    static Vector3 Normal(float[] v, int stride, int i) => new(v[i * stride + 3], v[i * stride + 4], v[i * stride + 5]);

    /// <summary>Möller–Trumbore, both faces.</summary>
    static bool Intersect(in Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance, out float u, out float v)
    {
        distance = u = v = 0;
        var ab = b - a;
        var ac = c - a;
        var p = Vector3.Cross(ray.Direction, ac);
        float det = Vector3.Dot(ab, p);
        if (MathF.Abs(det) < 1e-9f)
            return false;
        float inverse = 1 / det;
        var s = ray.Origin - a;
        u = Vector3.Dot(s, p) * inverse;
        if (u < 0 || u > 1)
            return false;
        var q = Vector3.Cross(s, ab);
        v = Vector3.Dot(ray.Direction, q) * inverse;
        if (v < 0 || u + v > 1)
            return false;
        distance = Vector3.Dot(ac, q) * inverse;
        return distance > 1e-4f;
    }
}
