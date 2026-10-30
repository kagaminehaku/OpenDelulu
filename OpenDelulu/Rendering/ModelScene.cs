using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Engine.Stages;
using OpenDelulu.Formats.Imaging;

namespace OpenDelulu.Rendering;

/// <summary>Where the camera orbits: the subject's centre and size, its close-up point and how far the scene reaches.</summary>
/// <param name="FacingYaw">Where the subject faces (radians about Y, 0 = +Z); the default view is from the front.</param>
public readonly record struct CameraSubject(Vector3 Center, float Radius, Vector3 Aim, float SceneRadius, float FacingYaw = 0);

/// <summary>
/// A camera position relative to the subject: pitch (radians), zoom (1 = whole model), yaw offset from her front, and
/// optionally the joints to aim at (their average; e.g. the breasts or the crotch) instead of her face.
/// </summary>
public readonly record struct CameraView(float Pitch, float Zoom, float Yaw = 0, string[]? Aim = null)
{
    /// <summary>The original's camera points (ds01_02 menu, keys W/E/R and the back view): face, chest, crotch, behind.</summary>
    public static readonly CameraView Face = new(0.05f, 0.25f);
    public static readonly CameraView Chest = new(0.05f, 0.3f, 0, ["o01_J_Mune01_L", "o01_J_Mune01_R"]);
    public static readonly CameraView Crotch = new(-0.2f, 0.3f, 0, ["o01_J_Kokan01"]);
    public static readonly CameraView Behind = new(0.1f, 0.4f, MathF.PI, ["o01_J_Siri01_L", "o01_J_Siri01_R"]);
}

/// <summary>
/// What the <see cref="ModelViewer"/> draws: one or more models (e.g. a stage and a character standing on it),
/// per-mesh visibility and optional animators. Meshes and textures of all objects are flattened into one list,
/// so renderers index them the same way as for a single model.
/// </summary>
public sealed class ModelScene
{
    sealed record SceneObject(RenderModel Model, CharacterAnimator? Animator, int MeshOffset, int TextureOffset)
    {
        public Vector3 Light = Vector3.One;
    }

    readonly List<SceneObject> _objects = [];
    readonly List<RenderMesh> _meshes = [];
    readonly List<RgbaImage?> _textures = [];
    readonly List<int> _owner = [];
    readonly bool[] _visible;
    readonly int _subject;

    public ModelScene(RenderModel model, CharacterAnimator? animator = null) : this([(model, animator)], 0)
    {
    }

    /// <param name="subject">Index of the object the camera frames (the character on a stage).</param>
    public ModelScene(IReadOnlyList<(RenderModel Model, CharacterAnimator? Animator)> objects, int subject)
    {
        foreach (var (model, animator) in objects)
        {
            _objects.Add(new SceneObject(model, animator, _meshes.Count, _textures.Count));
            foreach (var mesh in model.Meshes)
            {
                _meshes.Add(mesh);
                _owner.Add(_objects.Count - 1);
            }
            _textures.AddRange(model.Textures);
        }
        _visible = _meshes.Select(m => m.Visible).ToArray();
        _subject = Math.Clamp(subject, 0, _objects.Count - 1);
        _objectLights = new LightSet?[_objects.Count];
        _colors = new float[]?[_meshes.Count];
        _colorVersion = new int[_meshes.Count];
        _colorDirty = new bool[_meshes.Count];
        _layers = new float[]?[_meshes.Count];
        _layerVersion = new int[_meshes.Count];
    }

    // ------------------------------------------------------------------ Direct3D 9 fixed-function lighting

    readonly LightSet?[] _objectLights;
    readonly Dictionary<int, (StageParticles Particles, Func<string, Matrix4x4?> Attach)> _particles = [];
    double _effectTime;
    readonly float[]?[] _colors;
    readonly int[] _colorVersion;
    readonly bool[] _colorDirty;
    Matrix4x4? _lightCamera;

    /// <summary>
    /// Lights an object like the original (per-vertex Direct3D 9 lighting from its .xl lights, see
    /// <see cref="FixedLighting"/>); null = the viewer's simple lighting.
    /// </summary>
    public void SetObjectLights(int objectIndex, LightSet? lights)
    {
        _objectLights[objectIndex] = lights;
        MarkColorsDirty(objectIndex);
        Changed?.Invoke();
    }

    void MarkColorsDirty(int objectIndex)
    {
        var o = _objects[objectIndex];
        for (int m = o.MeshOffset; m < o.MeshOffset + o.Model.Meshes.Count; m++)
            _colorDirty[m] = true;
    }

    /// <summary>
    /// Makes an object the stage's keyframed sprites (.ptcl): its meshes are rewritten every frame (camera-facing quads on
    /// the stage frames <paramref name="attach"/> resolves) and coloured by the sprites' own colour and opacity.
    /// </summary>
    public void SetObjectParticles(int objectIndex, StageParticles particles, Func<string, Matrix4x4?> attach)
    {
        _particles[objectIndex] = (particles, attach);
        var o = _objects[objectIndex];
        for (int m = o.MeshOffset; m < o.MeshOffset + o.Model.Meshes.Count; m++)
            _colors[m] = new float[_meshes[m].Vertices.Length / RenderModel.VertexStride * 4];
        Changed?.Invoke();
    }

    readonly float[]?[] _layers;
    readonly int[] _layerVersion;

    /// <summary>Index into <see cref="Textures"/> of a mesh's second texture stage (<see cref="RenderMesh.Layer"/>), or -1.</summary>
    public int LayerTextureIndex(int mesh) =>
        _meshes[mesh].Layer is { } layer ? layer.TextureIndex + _objects[_owner[mesh]].TextureOffset : -1;

    /// <summary>
    /// Per-vertex data of a mesh's second texture stage: u, v, blend (<see cref="LayerBlend"/>), 1; null without one.
    /// </summary>
    public float[]? LayerData(int mesh) => _layers[mesh];

    /// <summary>Changes whenever <see cref="LayerData"/> of the mesh changes.</summary>
    public int LayerVersion(int mesh) => _layerVersion[mesh];

    void UpdateLayers(Matrix4x4 cameraToWorld, bool cameraMoved, bool verticesChanged)
    {
        for (int m = 0; m < _meshes.Count; m++)
        {
            if (_meshes[m].Layer is not { } layer || !_visible[m] && _layers[m] is not null)
                continue;
            bool fixedCoordinates = layer.Coordinates == LayerCoordinates.Uv;
            if (_layers[m] is not null && (fixedCoordinates || !cameraMoved && !(verticesChanged && IsDynamic(m))))
                continue;
            var data = _layers[m] ??= new float[_meshes[m].Vertices.Length / RenderModel.VertexStride * 4];
            layer.Evaluate(_meshes[m], cameraToWorld, data);
            _layerVersion[m]++;
        }
    }

    /// <summary>Lit vertex colours (RGBA per vertex) of a mesh, or null when it uses the simple lighting.</summary>
    public float[]? Colors(int mesh) => _colors[mesh];

    /// <summary>Changes whenever <see cref="Colors"/> of the mesh changes (renderers re-upload).</summary>
    public int ColorVersion(int mesh) => _colorVersion[mesh];

    /// <summary>
    /// Recomputes the vertex colours that are out of date: after the mesh moved (<paramref name="verticesChanged"/> for
    /// animated meshes), when the camera moved for camera-relative lights, or after the lights or tints changed.
    /// </summary>
    /// <returns>True when effect meshes (.ptcl sprites) were rewritten: upload the dynamic meshes' vertices.</returns>
    public bool UpdateLighting(Matrix4x4 cameraToWorld, bool verticesChanged)
    {
        bool cameraMoved = _lightCamera != cameraToWorld;
        _lightCamera = cameraToWorld;
        UpdateLayers(cameraToWorld, cameraMoved, verticesChanged);
        foreach (var (index, (particles, attach)) in _particles)
        {
            var o = _objects[index];
            var colors = new float[o.Model.Meshes.Count][];
            for (int i = 0; i < colors.Length; i++)
                colors[i] = _colors[o.MeshOffset + i]!;
            particles.Evaluate(_effectTime, cameraToWorld, attach, colors);
            for (int m = o.MeshOffset; m < o.MeshOffset + o.Model.Meshes.Count; m++)
                _colorVersion[m]++;
        }
        for (int m = 0; m < _meshes.Count; m++)
        {
            if (_particles.ContainsKey(_owner[m]))
                continue;
            var lights = _objectLights[_owner[m]];
            if (lights is null)
            {
                if (_colors[m] is not null)
                {
                    _colors[m] = null;
                    _colorVersion[m]++;
                }
                continue;
            }
            bool dirty = _colorDirty[m] || _colors[m] is null || verticesChanged && IsDynamic(m) || cameraMoved && lights.HasCameraLights;
            if (!dirty || !_visible[m])
            {
                _colorDirty[m] |= dirty;
                continue;
            }
            var mesh = _meshes[m];
            var colors = _colors[m] ??= new float[mesh.Vertices.Length / RenderModel.VertexStride * 4];
            FixedLighting.Light(mesh, lights, cameraToWorld, Light(m), colors);
            _colorDirty[m] = false;
            _colorVersion[m]++;
        }
        return _particles.Count > 0;
    }

    /// <summary>The framed object's model (the only one for single-model scenes; mesh toggles use its indices).</summary>
    public RenderModel Model => _objects[_subject].Model;

    /// <summary>The framed object's animator; null for static models.</summary>
    public CharacterAnimator? Animator => _objects[_subject].Animator;

    public IReadOnlyList<RenderMesh> Meshes => _meshes;
    public IReadOnlyList<RgbaImage?> Textures => _textures;

    readonly Dictionary<int, int> _textureVersion = [];

    /// <summary>Changes whenever <see cref="Textures"/>[index] is replaced (renderers re-upload it).</summary>
    public int TextureVersion(int index) => _textureVersion.GetValueOrDefault(index);

    /// <summary>
    /// Replaces the texture of an object that is <paramref name="current"/> (by reference) with <paramref name="image"/>
    /// of the same size (the swimsuit's wet stain); false when the object has no such texture.
    /// </summary>
    public bool ReplaceTexture(int objectIndex, RgbaImage current, RgbaImage image)
    {
        var o = _objects[objectIndex];
        for (int i = 0; i < o.Model.Textures.Count; i++)
        {
            if (!ReferenceEquals(o.Model.Textures[i], current))
                continue;
            int index = o.TextureOffset + i;
            if (_textures[index] is not { } old || old.Width != image.Width || old.Height != image.Height)
                return false;
            _textures[index] = image;
            _textureVersion[index] = TextureVersion(index) + 1;
            Changed?.Invoke();
            return true;
        }
        return false;
    }

    /// <summary>Index into <see cref="Textures"/> for a mesh, or -1.</summary>
    public int TextureIndex(int mesh) =>
        _meshes[mesh].TextureIndex < 0 ? -1 : _meshes[mesh].TextureIndex + _objects[_owner[mesh]].TextureOffset;

    /// <summary>
    /// Multiplies the material colors of one object's meshes (1 = as lit by the viewer's light). Used to dim and
    /// tint a character to the stage's time of day.
    /// </summary>
    public void SetLight(int objectIndex, Vector3 light)
    {
        _objects[objectIndex].Light = light;
        MarkColorsDirty(objectIndex);
        Changed?.Invoke();
    }

    public Vector3 Light(int mesh) => _objects[_owner[mesh]].Light * (_tint.TryGetValue(mesh, out var tint) ? tint : Vector3.One);

    readonly Dictionary<int, Vector3> _tint = [];

    /// <summary>
    /// Multiplies the colour of the subject's meshes that <paramref name="match"/> (the wardrobe's nail, eye and hair
    /// colours); null puts the original colour back.
    /// </summary>
    public void SetSubjectTint(Func<RenderMesh, bool> match, Vector3? tint)
    {
        var o = _objects[_subject];
        for (int m = o.MeshOffset; m < o.MeshOffset + o.Model.Meshes.Count; m++)
        {
            if (!match(_meshes[m]))
                continue;
            if (tint is { } t)
                _tint[m] = t;
            else
                _tint.Remove(m);
            _colorDirty[m] = true;
        }
        Changed?.Invoke();
    }

    readonly Dictionary<int, float> _opacity = [];

    /// <summary>0..1 opacity of a mesh (CONFIG's see-through man); below 1 it is drawn with the translucent meshes.</summary>
    public float Opacity(int mesh) => _opacity.TryGetValue(mesh, out float o) ? o : 1f;

    /// <summary>Sets the opacity of the meshes of one object that <paramref name="match"/>.</summary>
    public void SetObjectOpacity(int objectIndex, Func<RenderMesh, bool> match, float opacity)
    {
        var o = _objects[objectIndex];
        for (int m = o.MeshOffset; m < o.MeshOffset + o.Model.Meshes.Count; m++)
        {
            if (!match(_meshes[m]))
                continue;
            if (opacity >= 0.999f)
                _opacity.Remove(m);
            else
                _opacity[m] = Math.Clamp(opacity, 0f, 1f);
        }
        Changed?.Invoke();
    }

    /// <summary>True when the mesh's vertices change while animating (re-upload after <see cref="Update"/>).</summary>
    public bool IsDynamic(int mesh) =>
        _particles.ContainsKey(_owner[mesh])
        || _objects[_owner[mesh]] is { Animator: { } animator } o && animator.IsMeshAnimated(mesh - o.MeshOffset);

    /// <summary>Raised when visibility or animation settings change; the viewer redraws.</summary>
    public event Action? Changed;

    public bool IsVisible(int meshIndex) => _visible[meshIndex] && !(_obstacleHidden?[meshIndex] ?? false);

    // ------------------------------------------------------------------ obstacles (CONFIG "erase obstacles")

    readonly List<(int[] Meshes, Vector2[] Footprint)> _obstacles = [];
    bool[]? _obstacleHidden;

    /// <summary>Hide the stage objects that come between the camera and the subject (see <see cref="SetObstacles"/>).</summary>
    public bool EraseObstacles { get; set; }

    /// <summary>
    /// The objects that may be hidden: their meshes and their hit boxes seen from above (x, z triangles, see
    /// <see cref="StageObstacle.Blocks"/>). Checked against the camera every frame in <see cref="SetLookTarget"/>.
    /// </summary>
    public void SetObstacles(IEnumerable<(int[] Meshes, Vector2[] Footprint)> obstacles)
    {
        _obstacles.Clear();
        _obstacles.AddRange(obstacles);
        _obstacleHidden = _obstacles.Count > 0 ? new bool[_meshes.Count] : null;
    }

    void UpdateObstacles(Vector3 eye)
    {
        if (_obstacleHidden is not { } hidden)
            return;
        var o = _objects[_subject];
        var target = Vector3.Transform(o.Model.Center, o.Animator?.Placement ?? Matrix4x4.Identity);
        var from = new Vector2(eye.X, eye.Z);
        var to = new Vector2(target.X, target.Z);
        foreach (var (meshes, footprint) in _obstacles)
        {
            bool block = EraseObstacles && StageObstacle.Blocks(footprint, from, to);
            foreach (int m in meshes)
                hidden[m] = block;
        }
    }

    public void SetVisible(int meshIndex, bool visible)
    {
        if (_visible[meshIndex] == visible)
            return;
        _visible[meshIndex] = visible;
        _objects[_owner[meshIndex]].Animator?.Invalidate();
        Changed?.Invoke();
    }

    /// <summary>Shows or hides every mesh of one object (meshes its model hides by default stay hidden).</summary>
    public void SetObjectVisible(int objectIndex, bool visible)
    {
        bool changed = false;
        for (int i = 0; i < _meshes.Count; i++)
        {
            if (_owner[i] != objectIndex || _visible[i] == (visible && _meshes[i].Visible))
                continue;
            _visible[i] = visible && _meshes[i].Visible;
            changed = true;
        }
        if (!changed)
            return;
        _objects[objectIndex].Animator?.Invalidate();
        Changed?.Invoke();
    }

    /// <summary>Call after changing an animator (clip, expression, play state, placement).</summary>
    /// <summary>
    /// A screenshot asked for: the backend that draws the scene next calls it with that frame (RGBA rows from the top,
    /// width, height) and clears it.
    /// </summary>
    public Action<byte[], int, int>? CaptureRequest { get; set; }

    public void NotifyChanged()
    {
        foreach (var o in _objects)
            o.Animator?.Invalidate();
        Changed?.Invoke();
    }

    public bool IsAnimating => _particles.Count > 0 || _objects.Any(o => o.Animator?.IsAnimating == true);

    /// <summary>Seconds of play seen by the effects (.ptcl sprites, water shader).</summary>
    public double EffectTime => _effectTime;

    /// <summary>
    /// The water shader of a mesh as (scale, bump matrix angle, wave frequency), or null when it has none.
    /// </summary>
    public (float Scale, float Angle, float Wave)? Water(int meshIndex) =>
        _meshes[meshIndex].Water is { } water ? (water.Scale, water.AngleAt(_effectTime), water.Wave) : null;

    /// <summary>The stage glare drawn over the frame (null = none).</summary>
    public StageGlare? Glare { get; set; }

    /// <summary>True when a visible mesh lights the glare (<see cref="RenderMesh.IsGlareSource"/>).</summary>
    public bool IsGlareSource(int meshIndex) => IsVisible(meshIndex) && _meshes[meshIndex].IsGlareSource;

    /// <summary>Camera pitch to start from (radians, positive = from above); null = the viewer's default.</summary>
    public float? CameraPitch { get; set; }

    /// <summary>Camera zoom to start from (1 = whole model); null = the viewer's default.</summary>
    public float? CameraZoom { get; set; }

    /// <summary>Asks the viewer to move its camera to a view (null = back to the scene's default view).</summary>
    public event Action<CameraView?>? CameraRequested;

    public void RequestCamera(CameraView? view)
    {
        _aimFrames = view?.Aim is { } joints && _objects[_subject].Animator is { } animator
            ? joints.Select(animator.Model.Rig.Find).Where(i => i >= 0).ToArray()
            : null;
        CameraRequested?.Invoke(view);
    }

    int[]? _aimFrames;

    /// <summary>Added to the subject's heading for the default view (e.g. pi/2 = from her side).</summary>
    public float CameraYawOffset { get; set; }

    /// <summary>
    /// Centre the camera on the subject's current pose instead of its bind pose (lying down poses are far from the
    /// standing T-pose the model's bounds describe); zooming in then stays on the body rather than the face.
    /// The centre is taken once, after the first animated frame, so the camera does not follow every move.
    /// </summary>
    public bool FrameLivePose { get; set; }

    Vector3? _liveCenter;

    Vector3? LiveCenter()
    {
        var o = _objects[_subject];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int m = o.MeshOffset; m < o.MeshOffset + o.Model.Meshes.Count; m++)
        {
            if (!_visible[m])
                continue;
            var vertices = _meshes[m].Vertices;
            for (int v = 0; v < vertices.Length; v += RenderModel.VertexStride)
            {
                var p = new Vector3(vertices[v], vertices[v + 1], vertices[v + 2]);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
        return min.X <= max.X ? (min + max) * 0.5f : null;
    }

    /// <summary>Points the characters' eyes at the camera.</summary>
    int _markerObject = -1;

    /// <summary>
    /// Makes an object the camera target marker (cm00, CONFIG's crosshair): a quad that follows the camera's target,
    /// faces the camera and is drawn over everything.
    /// </summary>
    public void SetCameraMarker(int objectIndex) => _markerObject = objectIndex;

    /// <summary>Meshes drawn last without depth test (the camera marker).</summary>
    public bool IsOverlay(int mesh) => _markerObject >= 0 && _owner[mesh] == _markerObject;

    /// <param name="eye">The camera position: characters look at it, obstacles in front of it are hidden.</param>
    /// <param name="cameraTarget">The point the camera looks at (moves the crosshair marker).</param>
    public void SetLookTarget(Vector3 eye, Vector3? cameraTarget = null)
    {
        UpdateObstacles(eye);
        if (_markerObject >= 0 && cameraTarget is { } target && _objects[_markerObject].Animator is { } marker)
        {
            var facing = eye - target;
            var placement = facing.LengthSquared() > 1e-6f
                ? Matrix4x4.CreateWorld(target, Vector3.Normalize(facing), Vector3.UnitY)
                : Matrix4x4.CreateTranslation(target);
            if (marker.Placement != placement)
                marker.Placement = placement;
        }
        foreach (var o in _objects)
            if (o.Animator is { } animator)
                animator.LookTarget = eye;
    }

    /// <summary>Advances every animator; true when some vertices changed (re-upload the dynamic meshes).</summary>
    public bool Update(double seconds)
    {
        _effectTime += seconds;
        bool changed = false;
        foreach (var o in _objects)
        {
            if (o.Animator is not { } animator)
                continue;
            int offset = o.MeshOffset;
            changed |= animator.Update(seconds, i => _visible[offset + i]);
        }
        if (FrameLivePose && _liveCenter is null && _objects[_subject].Animator is not null)
            _liveCenter = LiveCenter();
        return changed;
    }

    public CameraSubject Subject
    {
        get
        {
            var o = _objects[_subject];
            var placement = o.Animator?.Placement ?? Matrix4x4.Identity;
            var model = o.Model;
            var center = (FrameLivePose ? _liveCenter : null) ?? Vector3.Transform(model.Center, placement);
            var aim = _aimFrames is { Length: > 0 } frames && o.Animator is { } animator
                ? frames.Aggregate(Vector3.Zero, (sum, i) => sum + animator.WorldOf(i).Translation) / frames.Length
                : FrameLivePose ? center : o.Animator?.Focus
                ?? Vector3.Transform(model.FocusPoint ?? model.Center + new Vector3(0, (model.BoundsMax.Y - model.Center.Y) * 0.85f, 0), placement);
            float sceneRadius = _objects.Max(x => Vector3.Distance(center, Vector3.Transform(x.Model.Center, x.Animator?.Placement ?? Matrix4x4.Identity)) + x.Model.Radius);
            var forward = Vector3.TransformNormal(Vector3.UnitZ, placement);
            return new CameraSubject(center, model.Radius, aim, sceneRadius, MathF.Atan2(forward.X, forward.Z) + CameraYawOffset);
        }
    }
}
