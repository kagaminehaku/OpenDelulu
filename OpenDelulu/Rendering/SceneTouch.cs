using System.Numerics;
using OpenDelulu.Engine.Assets;

namespace OpenDelulu.Rendering;

/// <summary>
/// Receives presses and drags on a <see cref="ModelViewer"/> as rays into the scene (e.g. rubbing sun oil). While a
/// handler is set, the primary button / a finger touches the scene; the right mouse button still orbits the camera.
/// </summary>
public interface ISceneTouchHandler
{
    /// <summary>A press; return false to let the drag orbit the camera instead (nothing to touch there).</summary>
    /// <param name="up">The screen's up direction in world space at that point.</param>
    bool Begin(Ray ray, Vector3 up);

    void Move(Ray ray, Vector3 up);

    void End();
}
