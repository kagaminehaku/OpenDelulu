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
            if (!scene.Model.Meshes[i].Visible)
                toggle.IsVisible = false;
            meshes.Add(toggle);
        }
        Meshes = meshes;
    }

    public ModelScene Scene { get; }
    public IReadOnlyList<MeshToggleViewModel> Meshes { get; }
}

public sealed partial class MeshToggleViewModel(ModelScene scene, int index) : ViewModelBase
{
    public string Name { get; } = $"{scene.Model.Meshes[index].PartName}: {scene.Model.Meshes[index].DisplayName}";

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    partial void OnIsVisibleChanged(bool value) => scene.SetVisible(index, value);
}
