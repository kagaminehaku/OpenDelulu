using System.Collections.Generic;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Rendering;

namespace OpenDelulu.ViewModels;

/// <summary>Base for whatever is shown in the preview pane.</summary>
public abstract class PreviewViewModel(string title, string details) : ViewModelBase
{
    public string Title { get; } = title;
    public string Details { get; } = details;
}

public sealed class TextPreviewViewModel(string title, string details, string text) : PreviewViewModel(title, details)
{
    public string Text { get; } = text;
}

public sealed class ImagePreviewViewModel(string title, string details, Bitmap bitmap) : PreviewViewModel(title, details)
{
    public Bitmap Bitmap { get; } = bitmap;
}

public sealed class ModelPreviewViewModel : PreviewViewModel
{
    public ModelPreviewViewModel(string title, string details, ModelScene scene) : base(title, details)
    {
        Scene = scene;
        var meshes = new List<MeshToggleViewModel>(scene.Model.Meshes.Count);
        for (int i = 0; i < scene.Model.Meshes.Count; i++)
        {
            var toggle = new MeshToggleViewModel(scene, i);
            if (IsHelperMaterial(scene.Model.Meshes[i].MaterialName))
                toggle.IsVisible = false;
            meshes.Add(toggle);
        }
        Meshes = meshes;
    }

    /// <summary>Collision ("Atari") and glare helper meshes are never drawn as-is by the game.</summary>
    static bool IsHelperMaterial(string material) =>
        material.Contains("Atari", System.StringComparison.OrdinalIgnoreCase) ||
        material.Contains("Glare", System.StringComparison.OrdinalIgnoreCase);

    public ModelScene Scene { get; }
    public IReadOnlyList<MeshToggleViewModel> Meshes { get; }
}

public sealed partial class MeshToggleViewModel(ModelScene scene, int index) : ViewModelBase
{
    public string Name { get; } = scene.Model.Meshes[index].DisplayName;

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    partial void OnIsVisibleChanged(bool value) => scene.SetVisible(index, value);
}
