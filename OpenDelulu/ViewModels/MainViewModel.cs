using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenDelulu.Engine;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Engine.Script;
using OpenDelulu.Engine.Stages;
using OpenDelulu.Formats.Imaging;
using OpenDelulu.Formats.Lst;
using OpenDelulu.Formats.Pp;
using OpenDelulu.Formats.Script;
using OpenDelulu.Formats.Voice;
using OpenDelulu.Formats.Xx;
using OpenDelulu.Localization;
using OpenDelulu.Rendering;
using OpenDelulu.Services;

namespace OpenDelulu.ViewModels;

public sealed record ArchiveItem(ArchiveInfo Info, int FileCount)
{
    public string Display => $"{Info.Name}  ·  {FileCount}";
    public string Edition => Info.Edition.ToString();
}

public sealed record FileItem(string ArchiveName, PpEntry Entry)
{
    public string Display => Entry.Name;
    public string Size => Entry.Size >= 1 << 20 ? $"{Entry.Size / 1048576.0:F1} MB" : $"{Entry.Size / 1024.0:F0} KB";
}

public partial class MainViewModel : ViewModelBase, IDisposable
{
    readonly AppSettings _settings = AppSettings.Load();
    GameFileSystem? _fileSystem;
    int _previewVersion;

    public MainViewModel()
    {
        Translator.Instance.SetLanguage(AppHost.StartupLanguage ?? _settings.Language);
        _initializing = true;
        SelectedLanguage = Languages.FirstOrDefault(l => l.Code == Translator.Instance.Language);
        _initializing = false;
        var backend = RenderBackendSettings.Parse(AppHost.StartupBackend ?? _settings.RenderBackend);
        RenderBackendSettings.Preferred = backend;
        _initializing = true; // a --backend override must not be saved as the user's choice
        SelectedBackendIndex = (int)backend;
        _initializing = false;
        LoadSoundSettings();
        GameFolder = AppHost.StartupGameFolder ?? _settings.LastGameFolder ?? AppHost.SuggestedGameFolder ?? "";
    }

    public IReadOnlyList<LanguageInfo> Languages => Translator.Instance.Languages;

    [ObservableProperty]
    public partial LanguageInfo? SelectedLanguage { get; set; }

    partial void OnSelectedLanguageChanged(LanguageInfo? value)
    {
        if (_initializing || value is null)
            return;
        Translator.Instance.SetLanguage(value.Code);
        _settings.Language = value.Code;
        _settings.Save();
    }

    protected override void OnLanguageChanged() => OnPropertyChanged(nameof(StorageHint));

    /// <summary>Indexes match <see cref="RenderBackendKind"/>.</summary>
    public LocalizedText[] Backends { get; } = [new("backend.auto"), new("backend.gl"), new("backend.vulkan")];

    [ObservableProperty]
    public partial int SelectedBackendIndex { get; set; }

    bool _initializing;

    partial void OnSelectedBackendIndexChanged(int value)
    {
        if (_initializing)
            return;
        RenderBackendSettings.Preferred = (RenderBackendKind)Math.Clamp(value, 0, 2);
        _settings.RenderBackend = RenderBackendSettings.Preferred.ToString();
        _settings.Save();
        // The compositor (what draws the whole window) is chosen at startup; only Vulkan uses a Vulkan one.
        bool wantsVulkanCompositor = RenderBackendSettings.Preferred == RenderBackendKind.Vulkan;
        if (AppHost.SupportsVulkanCompositor && wantsVulkanCompositor != RenderBackendSettings.VulkanCompositor)
            Status = wantsVulkanCompositor
                ? T("backend.restartVulkan")
                : T("backend.restartDefault");
    }

    /// <summary>Set by the view: shows the platform folder picker, returns a local path or null.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    public string StorageHint => AppHost.StorageHintKey is { } key ? F(key, AppHost.StorageHintArgs) : AppHost.StorageHint ?? T("hint.default");

    [ObservableProperty]
    public partial string GameFolder { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = T("status.noData");

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public ObservableCollection<ArchiveItem> Archives { get; } = [];

    [ObservableProperty]
    public partial ArchiveItem? SelectedArchive { get; set; }

    public ObservableCollection<FileItem> Files { get; } = [];

    [ObservableProperty]
    public partial string FileFilter { get; set; } = "";

    [ObservableProperty]
    public partial FileItem? SelectedFile { get; set; }

    [ObservableProperty]
    public partial PreviewViewModel? Preview { get; set; }

    /// <summary>Character maker; null until game data with character tables is loaded.</summary>
    [ObservableProperty]
    public partial CharacterViewModel? Characters { get; set; }

    /// <summary>The game itself (basic loop); null until stages, characters and scripts are loaded.</summary>
    [ObservableProperty]
    public partial PlayViewModel? Play { get; set; }

    /// <summary>Event script player; null until game data with scripts is loaded.</summary>
    [ObservableProperty]
    public partial ScriptViewModel? Scripts { get; set; }

    /// <summary>0 = play, 1 = character maker, 2 = scripts, 3 = data browser.</summary>
    [ObservableProperty]
    public partial int SelectedTab { get; set; } = 3;

    /// <summary>Opens the last used folder automatically if it still looks valid.</summary>
    public async Task TryAutoLoadAsync()
    {
        if (!string.IsNullOrWhiteSpace(GameFolder) && Directory.Exists(GameFolder))
            await LoadAsync();

        if (AppHost.StartupFile is var (archive, file))
        {
            SelectedArchive = Archives.FirstOrDefault(a => a.Info.Name.Equals(archive, StringComparison.OrdinalIgnoreCase));
            SelectedFile = Files.FirstOrDefault(f => f.Entry.Name.Equals(file, StringComparison.OrdinalIgnoreCase));
        }
    }

    [RelayCommand]
    async Task BrowseAsync()
    {
        if (PickFolder is null)
            return;
        var path = await PickFolder();
        if (string.IsNullOrEmpty(path))
            return;
        GameFolder = path;
        await LoadAsync();
    }

    [RelayCommand]
    async Task LoadAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Status = T("status.scanning");
        try
        {
            string folder = GameFolder.Trim();
            StageDatabase? stageDb = null;
            var (fileSystem, items, characters, characterError) = await Task.Run(() =>
            {
                var fs = new GameFileSystem(GameInstall.Detect(folder));
                var list = fs.Archives.Select(a => new ArchiveItem(a, fs.GetArchive(a.Name).Entries.Count)).ToList();
                CharacterDatabase? db = null;
                string? error = null;
                try { db = CharacterDatabase.Load(fs); }
                catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException) { error = ex.Message; }
                (CharacterDatabase db, VoiceLibrary voices, ScriptLibrary scripts)? loaded =
                    db is null ? null : (db, new VoiceLibrary(fs), new ScriptLibrary(fs));
                StageDatabase? stages = null;
                try { stages = StageDatabase.Load(fs); }
                catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException) { }
                stageDb = stages;
                return (fs, list, loaded, error);
            });

            _fileSystem?.Dispose();
            _fileSystem = fileSystem;
            Archives.Clear();
            foreach (var item in items)
                Archives.Add(item);
            SelectedArchive = Archives.FirstOrDefault();

            bool openScript = AppHost.StartupScript is not null;
            Characters = characters is ({ Girls.Count: > 0 } db, var voices, _) ? new CharacterViewModel(fileSystem, db, voices) : null;
            Scripts = characters is ({ Girls.Count: > 0 } sdb, _, { Scripts.Count: > 0 } library) ? new ScriptViewModel(fileSystem, sdb, library) : null;
            Play = characters is ({ Girls.Count: > 0 } pdb, var pvoices, { Scripts.Count: > 0 } plib) && stageDb is { Stages.Count: > 0 }
                ? new PlayViewModel(fileSystem, pdb, stageDb, plib, pvoices) : null;
            if (Characters is not null)
            {
                Characters.SelectedGirl = Characters.Girls.FirstOrDefault(g => g.Id == AppHost.StartupCharacter) ?? Characters.Girls[0];
                if (Characters.Swimsuits.FirstOrDefault(s => s.Row == AppHost.StartupSwimsuit) is { } swimsuit)
                    Characters.SelectedSwimsuit = swimsuit;
                if (AppHost.StartupCut is int cut && cut > 0 && cut < Characters.Cuts.Count)
                    Characters.SelectedCutIndex = cut;
                SelectedTab = AppHost.StartupFile is not null ? 3 : openScript ? 2 : AppHost.StartupCharacter is not null ? 1 : 0;
            }

            var editions = string.Join(", ", fileSystem.Install.Editions.Select(e => e.Edition));
            string summary = F("status.loaded", editions, items.Count, items.Sum(i => i.FileCount));
            Status = characters is null
                ? F("status.noCharacterTables", summary, characterError)
                : F("status.loadedCharacters", summary, characters.Value.db.Girls.Count);
            _settings.LastGameFolder = folder;
            _settings.Save();
        }
        catch (Exception ex)
        {
            Status = F("status.error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedArchiveChanged(ArchiveItem? value) => RefreshFiles();

    partial void OnFileFilterChanged(string value) => RefreshFiles();

    partial void OnSelectedFileChanged(FileItem? value) => _ = LoadPreviewAsync(value);

    void RefreshFiles()
    {
        Files.Clear();
        if (_fileSystem is null || SelectedArchive is null)
            return;
        var archive = _fileSystem.GetArchive(SelectedArchive.Info.Name);
        var filter = FileFilter.Trim();
        foreach (var entry in archive.Entries)
            if (filter.Length == 0 || entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Files.Add(new FileItem(archive.Name, entry));
    }

    async Task LoadPreviewAsync(FileItem? item)
    {
        int version = ++_previewVersion;
        if (item is null || _fileSystem is null)
        {
            Preview = null;
            return;
        }

        var fs = _fileSystem;
        string title = $"{item.ArchiveName} / {item.Entry.Name}";
        try
        {
            var result = await Task.Run<object>(() => BuildPreview(fs, item));
            if (version != _previewVersion)
                return;

            Preview = result switch
            {
                RgbaImage image => new ImagePreviewViewModel(title, $"{image.Width} × {image.Height}, {item.Size}", ToBitmap(image)),
                RenderModel model => new ModelPreviewViewModel(title,
                    F("preview.model", model.Meshes.Count, model.Meshes.Sum(m => m.Vertices.Length / RenderModel.VertexStride), model.Textures.Count),
                    new ModelScene(model)),
                PreviewViewModel ready => ready,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            if (version == _previewVersion)
                Preview = new TextPreviewViewModel(title, T("preview.unreadable"), ex.ToString());
        }
    }

    /// <summary>Runs on a worker thread: decode only, no UI objects except plain text previews.</summary>
    static object BuildPreview(GameFileSystem fs, FileItem item)
    {
        var archive = fs.GetArchive(item.ArchiveName);
        string title = $"{item.ArchiveName} / {item.Entry.Name}";
        string ext = item.Entry.Extension;

        if (ext == ".lst")
        {
            var table = LstTable.Decode(archive.ReadAllBytes(item.Entry), item.Entry.Name);
            return new TextPreviewViewModel(title, F("preview.table", table.Rows.Count), table.Text);
        }
        if (AudioSources.IsAudio(item.Entry.Name))
        {
            var bytes = archive.ReadAllBytes(item.Entry);
            using var source = AudioSources.Open(bytes);
            string vssName = Path.ChangeExtension(item.Entry.Name, ".vss");
            string? lipSync = archive.Find(vssName) is { } vss ? VssFile.Read(archive.ReadAllBytes(vss)).Text : null;
            return new AudioPreviewViewModel(title,
                F("preview.audio", source.Duration?.TotalSeconds ?? 0, source.SampleRate, source.Channels, item.Size), bytes, lipSync);
        }
        if (ext == ".vss")
        {
            var vss = VssFile.Read(archive.ReadAllBytes(item.Entry));
            return new TextPreviewViewModel(title, F("preview.vss", vss.DurationMs, vss.Morae.Count),
                string.Join(Environment.NewLine, vss.Morae.Select((m, i) => $"{i * vss.DurationMs / Math.Max(1, vss.Morae.Count),6} ms  {m.Kana,-2} {m.Shape}")));
        }
        if (ext == ".sdt")
        {
            var script = SdtFile.Read(archive.ReadAllBytes(item.Entry));
            var text = new StringBuilder();
            foreach (var scene in script.Scenes)
            {
                text.AppendLine(F("preview.scene", scene.Id, scene.Value));
                AppendCommands(text, scene.Commands, "  ");
            }
            return new TextPreviewViewModel(title, F("preview.script", script.Scenes.Count), text.ToString());
        }
        if (ext == ".xx")
            return RenderModel.FromXx(XxReader.Read(archive.ReadAllBytes(item.Entry)), item.Entry.Name);
        if (ImageDecoder.IsImage(item.Entry.Name))
            return ImageDecoder.Decode(archive.ReadAllBytes(item.Entry), item.Entry.Name);

        // Unknown or not yet supported: show a hex dump of the beginning.
        var data = archive.ReadAllBytes(item.Entry);
        return new TextPreviewViewModel(title, F("preview.unsupported", item.Size, ext), HexDump(data, 4096));
    }

    static void AppendCommands(StringBuilder text, IEnumerable<SdtCommand> commands, string indent)
    {
        foreach (var command in commands)
        {
            text.Append(indent).AppendLine(command.ToString());
            for (int i = 0; i < command.Blocks.Count; i++)
            {
                text.Append(indent).Append("  [").Append(string.Join(", ", command.Rows.ElementAtOrDefault(i) ?? [])).AppendLine("]");
                AppendCommands(text, command.Blocks[i], indent + "    ");
            }
        }
    }

    static Bitmap ToBitmap(RgbaImage image) => UiTextures.ToBitmap(image);

    static string HexDump(byte[] data, int max)
    {
        var sb = new StringBuilder();
        int length = Math.Min(data.Length, max);
        for (int row = 0; row < length; row += 16)
        {
            sb.Append(row.ToString("X8")).Append("  ");
            for (int i = 0; i < 16; i++)
                sb.Append(row + i < length ? data[row + i].ToString("X2") + " " : "   ");
            sb.Append(' ');
            for (int i = 0; i < 16 && row + i < length; i++)
            {
                byte b = data[row + i];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }
            sb.AppendLine();
        }
        if (data.Length > max)
            sb.AppendLine(F("preview.moreBytes", data.Length - max));
        return sb.ToString();
    }

    public void Dispose() => _fileSystem?.Dispose();
}
