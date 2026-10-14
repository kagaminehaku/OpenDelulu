using System.Numerics;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Formats.Scene;
using OpenDelulu.Formats.Xa;
using OpenDelulu.Formats.Xx;

namespace OpenDelulu.Engine.Stages;

/// <summary>A loaded stage: the drawable model, its lights and the named standing points.</summary>
public sealed class Stage
{
    public required StageVariant Variant { get; init; }
    public required RenderModel Model { get; init; }
    public XlFile? Lights { get; init; }

    /// <summary>The stage's looping background animation (waves, water, flags), or null.</summary>
    public CharacterAnimator? Animator { get; init; }

    /// <summary>World matrices of the points in moNN.xx (o01_N_Point_Date01…, o01_N_Point_Oil…, o01_N_Point_H…).</summary>
    public IReadOnlyDictionary<string, Matrix4x4> Points { get; init; } = new Dictionary<string, Matrix4x4>();

    /// <summary>Points of a group in order ("Date" → Point_Date01, Point_Date02…).</summary>
    public IReadOnlyList<(string Name, Matrix4x4 Transform)> PointGroup(string group) =>
        Points.Where(p => p.Key.StartsWith($"o01_N_Point_{group}", StringComparison.Ordinal) && char.IsDigit(p.Key[^1]))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => (p.Key, p.Value))
            .ToList();

    /// <summary>
    /// Stage clips play ten times slower than character clips: their speeds (×3 fans, ×8 waves, ×80 the pool's turning
    /// sculpture) only look right at one timeline frame per second per unit of speed. At the characters' rate the waves
    /// looped every 0.3 s and the fans spun like propellers. The exe's own time base is not decoded yet.
    /// </summary>
    public const double AnimationSpeedScale = 0.1;

    public static Stage Load(GameFileSystem fs, StageVariant variant)
    {
        var main = XxReader.Read(fs.ReadReferenced(variant.Model.Archive, variant.Model.File));
        var parts = new List<RenderPart> { new(variant.Model.File, main, main.Root) };
        if (variant.Extra is { } extra)
        {
            var model = XxReader.Read(fs.ReadReferenced(extra.Archive, extra.File));
            parts.Add(new RenderPart(extra.File, model, model.Root));
        }
        var render = RenderModel.Build($"mp{variant.Stage:00} {variant.Time}", parts);

        var points = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        if (variant.Positions is { } positions)
        {
            var model = XxReader.Read(fs.ReadReferenced(positions.Archive, positions.File));
            Collect(model.Root, Matrix4x4.Identity, points);
        }

        XlFile? lights = null;
        if (variant.Lights is { } xl)
        {
            try { lights = XlFile.Read(fs.ReadReferenced(xl.Archive, xl.File)); }
            catch (InvalidDataException) { }
        }
        CharacterAnimator? animator = null;
        if (variant.Animation is { } xa)
        {
            try
            {
                if (XaReader.Read(fs.ReadReferenced(xa.Archive, xa.File)).Animation is { } animation)
                {
                    var player = new AnimationPlayer(render.Rig, animation, xa.File) { FollowNext = false, SpeedScale = AnimationSpeedScale };
                    if (player.BoundTracks > 0)
                        animator = new CharacterAnimator(render) { Body = player, SkinAnimatedOnly = true, EyesFollowTarget = false };
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
            }
        }
        return new Stage { Variant = variant, Model = render, Lights = lights, Points = points, Animator = animator };
    }

    static void Collect(XxFrame frame, Matrix4x4 parent, Dictionary<string, Matrix4x4> points)
    {
        var world = frame.Matrix * parent;
        if (frame.Name.StartsWith("o01_N_Point", StringComparison.Ordinal))
            points.TryAdd(frame.Name, world);
        foreach (var child in frame.Children)
            Collect(child, world, points);
    }
}
