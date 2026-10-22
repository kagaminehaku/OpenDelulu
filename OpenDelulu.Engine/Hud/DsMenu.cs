using System.Numerics;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Formats.Imaging;
using OpenDelulu.Formats.Xa;
using OpenDelulu.Formats.Xx;

namespace OpenDelulu.Engine.Hud;

/// <summary>A flat textured quad of a ds menu: its frame and the material's texture names (base, hover, alternate).</summary>
public sealed record DsQuad(string Frame, int FrameIndex, Vector3[] Corners, string Material, IReadOnlyList<string> Textures);

/// <summary>Where a quad is on the 1024×768 screen right now; zero-sized quads are folded away by the animation.</summary>
public readonly record struct DsQuadLayout(DsQuad Quad, double X, double Y, double Width, double Height)
{
    public bool IsVisible => Width >= 1 && Height >= 1;
    public bool Contains(double x, double y) => IsVisible && x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>
/// The in-game menus of the original (dsNN_MM .xx/.xa: ds01_01 system, 02 camera, 03 date, 04 oil, 05 H, 06/07 F,
/// ds02 title) are flat quads in an orthographic scene: one unit is 1000 pixels around the screen centre
/// (x = 512 + 1000·X, y = 384 − 1000·Y). Their clips fold and unfold the parts (a scale to zero hides a quad) and move
/// the whole menu between four corners (clip names end in A..D). Embedded textures of the item quads are 8×8
/// placeholders the exe replaces with dsNN_MM_GG_II_01 (labels) / _02 (icons) from sb3_0300.pp.
/// </summary>
public sealed class DsMenu
{
    readonly Rig _rig;
    readonly AnimationPlayer _player;
    readonly Matrix4x4[] _local, _world;
    readonly Dictionary<string, RgbaImage> _textures;

    DsMenu(string name, XxModel model, RenderModel render, AnimationPlayer player)
    {
        Name = name;
        _rig = render.Rig;
        _player = player;
        _local = new Matrix4x4[_rig.Count];
        _world = new Matrix4x4[_rig.Count];
        _textures = new Dictionary<string, RgbaImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var texture in model.Textures)
        {
            try { _textures.TryAdd(texture.Name, ImageDecoder.Decode(texture.GetImageFile(), texture.Name)); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException) { }
        }
        var quads = new List<DsQuad>();
        for (int i = 0; i < _rig.Count; i++)
        {
            var frame = _rig.Frames[i];
            if (frame.Mesh is not { Submeshes.Count: > 0 } mesh)
                continue;
            var submesh = mesh.Submeshes[0];
            var material = submesh.MaterialIndex >= 0 && submesh.MaterialIndex < model.Materials.Count ? model.Materials[submesh.MaterialIndex] : null;
            quads.Add(new DsQuad(frame.Name, i, submesh.Vertices.Select(v => v.Position).ToArray(), material?.Name ?? "",
                material?.Textures.Select(t => t.Name).ToList() ?? []));
        }
        Quads = quads;
        Evaluate();
    }

    public string Name { get; }
    public IReadOnlyList<DsQuad> Quads { get; }
    public IReadOnlyList<AnimationClipInfo> Clips => _player.Clips;

    /// <summary>True while a clip is still moving (call <see cref="Advance"/> and lay out again).</summary>
    public bool IsAnimating => _player.Current is { IsStatic: false } clip && (IsLooping || _player.Time < clip.End - 1e-3);

    /// <summary>The current clip repeats (selection pulses of the title menu).</summary>
    public bool IsLooping { get; private set; }

    /// <summary>Index of the clip last played.</summary>
    public int CurrentClip => _player.Current?.Index ?? -1;

    /// <summary>An embedded texture by name (hover fills, buttons, the check mark).</summary>
    public RgbaImage? Texture(string name) => _textures.GetValueOrDefault(name);

    /// <summary>
    /// Archives holding ds menus, newest first: the Bonus Disc's, then the base game's. The Gravure add-ons (sb3f/sb3g)
    /// carry their own title (ds02) for their separate executables, so they are not searched.
    /// </summary>
    public static readonly string[] Archives = ["sb3x_1100.pp", "sb3_0100.pp"];

    /// <summary>Loads dsNN_MM_00_00_00 from the newest of <see cref="Archives"/> that has it.</summary>
    public static DsMenu Load(GameFileSystem fs, string name)
    {
        string? Find(string file) => Archives.FirstOrDefault(a => fs.HasArchive(a) && fs.GetArchive(a).Find(file) is not null);
        string xxArchive = Find(name + ".xx") ?? throw new FileNotFoundException($"{name}.xx is not installed.");
        string xaArchive = Find(name + ".xa") ?? throw new FileNotFoundException($"{name}.xa is not installed.");
        var model = XxReader.Read(fs.ReadFile(xxArchive, name + ".xx"));
        var render = RenderModel.Build(name, [new RenderPart(name, model, model.Root)]);
        var animation = XaReader.Read(fs.ReadFile(xaArchive, name + ".xa")).Animation
            ?? throw new InvalidDataException($"{name}.xa has no animation.");
        var player = new AnimationPlayer(render.Rig, animation, name) { FollowNext = false };
        player.Play(0);
        return new DsMenu(name, model, render, player);
    }

    /// <summary>Plays a clip by name (e.g. "シーン表示A"); with <paramref name="instant"/> jumps to its last frame.</summary>
    public bool Play(string clipName, bool instant = false)
    {
        var clip = _player.Clips.FirstOrDefault(c => c.Name == clipName);
        return clip is not null && Play(clip.Index, instant);
    }

    /// <summary>Plays a clip by index (names repeat, e.g. the two もどる選択ループ of the title); <paramref name="loop"/> repeats it.</summary>
    public bool Play(int index, bool instant = false, bool loop = false)
    {
        var clip = _player.Clips.FirstOrDefault(c => c.Index == index);
        if (clip is null)
            return false;
        IsLooping = loop && !clip.IsStatic;
        _player.Play(clip.Index);
        if (instant)
            _player.Advance((clip.End - clip.Start) / AnimationPlayer.BaseFramesPerSecond / Math.Max(0.01, clip.Speed) - 1e-6);
        Evaluate();
        return true;
    }

    /// <summary>Moves the current clip on (it stops on its last frame).</summary>
    public void Advance(double seconds)
    {
        if (_player.Current is not { IsStatic: false } clip)
            return;
        if (IsLooping)
        {
            // Wrap inside the clip instead of following its "next" link.
            double frames = seconds * AnimationPlayer.BaseFramesPerSecond * Math.Max(0.01, clip.Speed);
            double time = clip.Start + ((_player.Time - clip.Start + frames) % Math.Max(1e-3, clip.End - clip.Start));
            _player.Play(clip.Index);
            _player.Advance((time - clip.Start) / AnimationPlayer.BaseFramesPerSecond / Math.Max(0.01, clip.Speed));
            Evaluate();
            return;
        }
        double remaining = (clip.End - 1e-4 - _player.Time) / AnimationPlayer.BaseFramesPerSecond / Math.Max(0.01, clip.Speed);
        _player.Advance(Math.Clamp(seconds, 0, Math.Max(0, remaining)));
        Evaluate();
    }

    void Evaluate()
    {
        _player.Evaluate(_local);
        _rig.ComputeWorld(_local, _world);
    }

    /// <summary>Every quad's screen rectangle in drawing order (frame order).</summary>
    public IReadOnlyList<DsQuadLayout> Layout()
    {
        var result = new List<DsQuadLayout>(Quads.Count);
        foreach (var quad in Quads)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var corner in quad.Corners)
            {
                var p = Vector3.Transform(corner, _world[quad.FrameIndex]);
                double sx = 512 + 1000 * p.X, sy = 384 - 1000 * p.Y;
                x0 = Math.Min(x0, sx); x1 = Math.Max(x1, sx);
                y0 = Math.Min(y0, sy); y1 = Math.Max(y1, sy);
            }
            result.Add(new DsQuadLayout(quad, Math.Round(x0), Math.Round(y0), Math.Round(x1 - x0), Math.Round(y1 - y0)));
        }
        return result;
    }
}
