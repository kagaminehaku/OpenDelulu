using System.Collections.Generic;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenDelulu.Engine.Audio;
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

/// <summary>A .wav/.ogg: plays on the effects bus (music-length files loop until stopped).</summary>
public sealed partial class AudioPreviewViewModel(string title, string details, byte[] data, string? lipSyncText) : PreviewViewModel(title, details)
{
    SoundHandle? _handle;

    public string? LipSyncText { get; } = lipSyncText;
    public bool HasLipSync => LipSyncText is not null;

    [RelayCommand]
    void Play()
    {
        _handle?.Stop(0.05);
        _handle = AudioSystem.Shared.Play(data, AudioBus.Effects, loop: Loop);
    }

    [RelayCommand]
    void Stop() => _handle?.Stop(0.1);

    [ObservableProperty]
    public partial bool Loop { get; set; }

    public string Device => AudioSystem.Shared.OutputError is { } error
        ? F("audio.deviceError", error)
        : F("audio.device", AudioSystem.Shared.OutputName == "none" ? T("audio.none") : AudioSystem.Shared.OutputName);

    public string LipSyncLabel => F("preview.lipSync", LipSyncText);

    protected override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Device));
        OnPropertyChanged(nameof(LipSyncLabel));
    }
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

    public string MeshesHeader => F("preview.meshes", Meshes.Count);

    protected override void OnLanguageChanged() => OnPropertyChanged(nameof(MeshesHeader));
}

public sealed partial class MeshToggleViewModel(ModelScene scene, int index) : ViewModelBase
{
    public string Name { get; } = $"{scene.Model.Meshes[index].PartName}: {scene.Model.Meshes[index].DisplayName}";

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    partial void OnIsVisibleChanged(bool value) => scene.SetVisible(index, value);
}
