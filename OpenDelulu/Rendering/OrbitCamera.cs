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
    public float Zoom { get; set; } = 1f;

    public void Reset()
    {
        Yaw = DefaultYaw;
        Pitch = 0.1f;
        Zoom = 1f;
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
    public (Matrix4x4 ViewProjection, Vector3 LightDirection) Compute(RenderModel model, float aspect, bool flipY)
    {
        float radius = model.Radius;
        var target = model.Center;
        float distance = radius * 2.4f * Zoom;
        var eyeDir = new Vector3(
            MathF.Sin(Yaw) * MathF.Cos(Pitch),
            MathF.Sin(Pitch),
            -MathF.Cos(Yaw) * MathF.Cos(Pitch));
        var eye = target + eyeDir * distance;

        var view = Matrix4x4.CreateLookAtLeftHanded(eye, target, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
            MathF.PI / 4, aspect, MathF.Max(distance - radius * 1.5f, radius * 0.01f), distance + radius * 3f);
        var viewProjection = view * projection;
        if (flipY)
            viewProjection *= Matrix4x4.CreateScale(1, -1, 1);
        return (viewProjection, Vector3.Normalize(eyeDir + new Vector3(0, 0.6f, 0)));
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
}
