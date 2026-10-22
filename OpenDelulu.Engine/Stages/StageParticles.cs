using System.Numerics;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Formats.Scene;

namespace OpenDelulu.Engine.Stages;

/// <summary>
/// The keyframed sprites of a stage's .ptcl: every emitter is one textured quad on a frame of the stage (o01_N_sun, the
/// splash points of the falls, the steam of the hot spring, lamp glows, stars), facing the camera, looping over its keys.
/// Its second curve holds the offset (xyz, in the frame's space), the turn around the view axis (radians) and the size
/// (xyz); the first holds the colour, whose fourth value is the opacity. The quads are drawn additively like their black-
/// backed textures need; colour × opacity goes through the lit vertex colours.
/// </summary>
public sealed class StageParticles
{
    sealed record Emitter(int Mesh, string Frame, ParticleKey[] Color, ParticleKey[] Transform, bool Billboard, Vector2[] Corners);

    readonly List<Emitter> _emitters = [];
    readonly int _loop;

    StageParticles(RenderModel model, List<Emitter> emitters)
    {
        Model = model;
        _emitters = emitters;
        _loop = Math.Max(1, emitters.SelectMany(e => e.Color.Concat(e.Transform)).Select(k => k.Frame).DefaultIfEmpty(1).Max());
    }

    /// <summary>One mesh per emitter, vertices rewritten by <see cref="Evaluate"/>.</summary>
    public RenderModel Model { get; }

    /// <summary>Key frames per second. The file does not say; 30 makes a splash puff last about 0.4 s.</summary>
    public double FramesPerSecond { get; set; } = 30;

    public static StageParticles? Load(PtclFile file, string name)
    {
        if (file.Emitters.Count == 0)
            return null;
        // A texture is stored once, in the first emitter that uses it; the others only name it.
        var shared = file.Emitters.SelectMany(e => e.Model.Textures).GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var e in file.Emitters)
            foreach (var textureName in e.Model.Materials.SelectMany(m => m.Textures).Select(t => t.Name).Where(n => n.Length > 0).Distinct())
                if (e.Model.FindTexture(textureName) is null && shared.TryGetValue(textureName, out var texture))
                    e.Model.Textures.Add(texture);
        var parts = file.Emitters.Select((e, i) => new RenderPart($"{name}#{i}", e.Model, e.Model.Root)).ToList();
        var model = RenderModel.Build(name, parts, _ => true);
        var emitters = new List<Emitter>();
        for (int i = 0; i < file.Emitters.Count && i < model.Meshes.Count; i++)
        {
            var e = file.Emitters[i];
            var mesh = model.Meshes[i];
            var corners = new Vector2[mesh.Vertices.Length / RenderModel.VertexStride];
            for (int v = 0; v < corners.Length; v++)
                corners[v] = new Vector2(mesh.Vertices[v * RenderModel.VertexStride], mesh.Vertices[v * RenderModel.VertexStride + 1]);
            emitters.Add(new Emitter(i, e.AttachFrame, [.. e.Curve17.OrderBy(k => k.Frame)], [.. e.Curve7.OrderBy(k => k.Frame)],
                e.Flags.Length == 0 || e.Flags[0] != 0, corners));
        }
        return new StageParticles(model, emitters);
    }

    /// <summary>Frames of the stage the emitters sit on.</summary>
    public IEnumerable<string> AttachFrames => _emitters.Select(e => e.Frame).Distinct();

    /// <summary>
    /// Places every quad for <paramref name="seconds"/> of play: writes the meshes' vertices (world space) and their
    /// colours (RGBA per vertex, A = 1). <paramref name="attach"/> gives a stage frame's world matrix (null = not found).
    /// </summary>
    public void Evaluate(double seconds, Matrix4x4 cameraToWorld, Func<string, Matrix4x4?> attach, IReadOnlyList<float[]> colors)
    {
        float frame = (float)(seconds * FramesPerSecond % _loop);
        var right = Vector3.Normalize(new Vector3(cameraToWorld.M11, cameraToWorld.M12, cameraToWorld.M13));
        var up = Vector3.Normalize(new Vector3(cameraToWorld.M21, cameraToWorld.M22, cameraToWorld.M23));
        var facing = -Vector3.Normalize(new Vector3(cameraToWorld.M31, cameraToWorld.M32, cameraToWorld.M33));
        Span<float> color = stackalloc float[17];
        Span<float> transform = stackalloc float[7];
        foreach (var e in _emitters)
        {
            var mesh = Model.Meshes[e.Mesh];
            var vertices = mesh.Vertices;
            var world = attach(e.Frame) ?? Matrix4x4.Identity;
            Sample(e.Color, frame, color, defaults: 1);
            Sample(e.Transform, frame, transform, defaults: 0);
            if (e.Transform.Length == 0)
                transform[4] = transform[5] = transform[6] = 1;
            var center = Vector3.Transform(new Vector3(transform[0], transform[1], transform[2]), world);
            float turn = transform[3], cos = MathF.Cos(turn), sin = MathF.Sin(turn);
            for (int v = 0; v < e.Corners.Length; v++)
            {
                var c = e.Corners[v];
                var turned = new Vector2(c.X * cos - c.Y * sin, c.X * sin + c.Y * cos);
                Vector3 position = e.Billboard
                    ? center + right * (turned.X * transform[4]) + up * (turned.Y * transform[5])
                    : Vector3.Transform(new Vector3(turned.X * transform[4], turned.Y * transform[5], 0), world) - world.Translation + center;
                int o = v * RenderModel.VertexStride;
                vertices[o] = position.X;
                vertices[o + 1] = position.Y;
                vertices[o + 2] = position.Z;
                vertices[o + 3] = facing.X;
                vertices[o + 4] = facing.Y;
                vertices[o + 5] = facing.Z;
            }
            float alpha = Math.Clamp(color[3], 0, 1);
            var rgb = colors[e.Mesh];
            for (int v = 0; v < e.Corners.Length; v++)
            {
                rgb[v * 4] = Math.Clamp(color[0], 0, 1) * alpha;
                rgb[v * 4 + 1] = Math.Clamp(color[1], 0, 1) * alpha;
                rgb[v * 4 + 2] = Math.Clamp(color[2], 0, 1) * alpha;
                rgb[v * 4 + 3] = 1;
            }
        }
    }

    static void Sample(ParticleKey[] keys, float frame, Span<float> values, float defaults)
    {
        if (keys.Length == 0)
        {
            values.Fill(defaults);
            return;
        }
        int hi = 0;
        while (hi < keys.Length && keys[hi].Frame < frame)
            hi++;
        var a = keys[Math.Max(0, hi - 1)];
        var b = keys[Math.Min(hi, keys.Length - 1)];
        float t = b.Frame > a.Frame ? Math.Clamp((frame - a.Frame) / (b.Frame - a.Frame), 0, 1) : 0;
        for (int i = 0; i < values.Length; i++)
        {
            float va = i < a.Values.Length ? a.Values[i] : defaults, vb = i < b.Values.Length ? b.Values[i] : defaults;
            values[i] = va + (vb - va) * t;
        }
    }
}
