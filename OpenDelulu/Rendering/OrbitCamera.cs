using System;
using System.Numerics;
using OpenDelulu.Engine.Assets;

namespace OpenDelulu.Rendering;

/// <summary>Orbit camera around a model's bounding sphere; shared by every render backend.</summary>
public sealed class OrbitCamera
{
    /// <summary>Illusion characters face +Z, so the default camera looks from the front (+Z towards -Z).</summary>
    public const float DefaultYaw = MathF.PI;

    public float Yaw { get; set; } = DefaultYaw;
    public float Pitch { get; set; } = 0.1f;
    public float Zoom { get; set; } = Services.AppHost.StartupZoom ?? 1f;

    /// <summary>Zoom used by <see cref="Reset"/> (a startup --zoom still wins).</summary>
    public float DefaultZoom { get; set; } = 1f;

    public void Reset()
    {
        Yaw = DefaultYaw;
        Pitch = 0.1f;
        Zoom = Services.AppHost.StartupZoom ?? DefaultZoom;
    }

    public void Rotate(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch = Math.Clamp(Pitch + deltaPitch, -1.5f, 1.5f);
    }

    public void ZoomBy(float factor) => Zoom = Math.Clamp(Zoom * factor, 0.05f, 20f);

    /// <summary>
    /// View-projection for Direct3D content (left-handed, depth 0..1, row vectors). Upload untransposed:
    /// GLSL's column-major read then equals the transpose needed for <c>M * v</c>.
    /// </summary>
    /// <param name="flipY">Vulkan's clip space points Y down.</param>
    /// <summary>Camera space to world space (for camera-relative lights): the inverse of the view matrix.</summary>
    public Matrix4x4 CameraToWorld(CameraSubject subject)
    {
        float raise = Math.Clamp((1f - Zoom) / 0.6f, 0f, 1f);
        var target = Vector3.Lerp(subject.Center, subject.Aim, raise);
        float distance = subject.Radius * 2.4f * Zoom;
        float yaw = Yaw - subject.FacingYaw;
        var eye = target + new Vector3(MathF.Sin(yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), -MathF.Cos(yaw) * MathF.Cos(Pitch)) * distance;
        var view = Matrix4x4.CreateLookAtLeftHanded(eye, target, Vector3.UnitY);
        return Matrix4x4.Invert(view, out var inverse) ? inverse : Matrix4x4.Identity;
    }

    /// <summary>
    /// The point the camera looks at: zooming in also moves the orbit centre from the middle of the model to its focus
    /// point (between a character's eyes), so close-ups are not seen from waist height.
    /// </summary>
    public Vector3 Target(CameraSubject subject)
    {
        float raise = Math.Clamp((1f - Zoom) / 0.6f, 0f, 1f);
        return Vector3.Lerp(subject.Center, subject.Aim, raise);
    }

    public (Matrix4x4 ViewProjection, Vector3 LightDirection, Vector3 Eye) Compute(CameraSubject subject, float aspect, bool flipY)
    {
        float radius = subject.Radius;
        var target = Target(subject);
        float distance = radius * 2.4f * Zoom;
        float yaw = Yaw - subject.FacingYaw; // DefaultYaw looks at the subject's face
        var eyeDir = new Vector3(
            MathF.Sin(yaw) * MathF.Cos(Pitch),
            MathF.Sin(Pitch),
            -MathF.Cos(yaw) * MathF.Cos(Pitch));
        var eye = target + eyeDir * distance;

        // A stage around the subject reaches much further than the subject itself.
        bool staged = subject.SceneRadius > radius * 1.5f;
        float near = staged ? MathF.Max(radius * 0.02f, 0.05f) : MathF.Max(distance - radius * 1.5f, radius * 0.01f);
        float far = distance + MathF.Max(radius * 3f, subject.SceneRadius * 2f);
        var view = Matrix4x4.CreateLookAtLeftHanded(eye, target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(MathF.PI / 4, aspect, near, far);
        var viewProjection = view * projection;
        if (flipY)
            viewProjection *= Matrix4x4.CreateScale(1, -1, 1);
        return (viewProjection, Vector3.Normalize(eyeDir + new Vector3(0, 0.6f, 0)), eye);
    }

    /// <summary>
    /// The ray through a point of the view (<paramref name="x"/>, <paramref name="y"/> from -1 to 1, y up) and the
    /// screen's up direction there, for the projection <see cref="Compute"/> gives the backends.
    /// </summary>
    public (Ray Ray, Vector3 Up)? RayThrough(CameraSubject subject, float aspect, float x, float y)
    {
        var (viewProjection, _, _) = Compute(subject, aspect, flipY: false);
        if (!Matrix4x4.Invert(viewProjection, out var inverse))
            return null;
        Vector3 Unproject(float px, float py, float depth)
        {
            var v = Vector4.Transform(new Vector4(px, py, depth, 1), inverse);
            return new Vector3(v.X, v.Y, v.Z) / v.W;
        }
        var near = Unproject(x, y, 0);
        var far = Unproject(x, y, 1);
        var up = Unproject(x, y + 0.05f, 0.5f) - Unproject(x, y, 0.5f);
        return (new Ray(near, Vector3.Normalize(far - near)), up.LengthSquared() > 1e-12f ? Vector3.Normalize(up) : Vector3.UnitY);
    }
}

/// <summary>Measures real time between rendered frames for animation.</summary>
internal sealed class FrameClock
{
    readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();
    double _last = -1;

    /// <summary>Seconds since the previous call (0 after a pause), capped to avoid jumps.</summary>
    public double Tick()
    {
        double now = _watch.Elapsed.TotalSeconds;
        double delta = _last < 0 ? 0 : Math.Min(0.1, now - _last);
        _last = now;
        return delta;
    }

    public void Pause() => _last = -1;
}

/// <summary>A control that draws a <see cref="ModelScene"/> with one graphics API.</summary>
internal interface IModelViewBackend
{
    ModelScene? Scene { get; set; }
    OrbitCamera Camera { get; set; }
    /// <summary>Human readable API/device, shown in the viewer corner.</summary>
    string Description { get; }
    void RequestFrame();
    /// <summary>Raised when the backend cannot run here (the host falls back to another one).</summary>
    event Action<string>? Failed;
    event Action? Ready;
}

public enum RenderBackendKind
{
    Auto,
    OpenGl,
    Vulkan,
}

/// <summary>User preference for the 3D backend (persisted in settings).</summary>
public static class RenderBackendSettings
{
    static RenderBackendKind _preferred = RenderBackendKind.Auto;

    public static RenderBackendKind Preferred
    {
        get => _preferred;
        set
        {
            if (_preferred == value)
                return;
            _preferred = value;
            Changed?.Invoke();
        }
    }

    public static event Action? Changed;

    /// <summary>
    /// True when the platform layer was started with a Vulkan compositor ("pure Vulkan": frames are shared
    /// as Vulkan images instead of D3D11 textures). Decided at startup; changing it needs a restart.
    /// </summary>
    public static bool VulkanCompositor { get; set; }

    /// <summary>Parses "auto", "gl"/"opengl", "vulkan"/"vk" (case-insensitive).</summary>
    public static RenderBackendKind Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "gl" or "opengl" => RenderBackendKind.OpenGl,
        "vulkan" or "vk" => RenderBackendKind.Vulkan,
        _ => RenderBackendKind.Auto,
    };
}
