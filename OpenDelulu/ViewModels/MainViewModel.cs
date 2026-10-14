using System;
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
using OpenDelulu.Engine.Characters;
using OpenDelulu.Formats.Imaging;
using OpenDelulu.Formats.Lst;
using OpenDelulu.Formats.Pp;
using OpenDelulu.Formats.Xx;
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
        GameFolder = AppHost.StartupGameFolder ?? _settings.LastGameFolder ?? AppHost.SuggestedGameFolder ?? "";
        StorageHint = AppHost.StorageHint ?? "Chọn thư mục chứa SexyBeach3 (và SexyBeach3Plus / SexyBeach3PlusG nếu có).";
    }

    /// <summary>Set by the view: shows the platform folder picker, returns a local path or null.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    public string StorageHint { get; }

    [ObservableProperty]
    public partial string GameFolder { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Chưa mở dữ liệu game.";

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

    /// <summary>0 = character maker, 1 = data browser.</summary>
    [ObservableProperty]
    public partial int SelectedTab { get; set; } = 1;

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
        Status = "Đang quét dữ liệu...";
        try
        {
            string folder = GameFolder.Trim();
            var (fileSystem, items, characters, characterError) = await Task.Run(() =>
            {
                var fs = new GameFileSystem(GameInstall.Detect(folder));
                var list = fs.Archives.Select(a => new ArchiveItem(a, fs.GetArchive(a.Name).Entries.Count)).ToList();
                CharacterDatabase? db = null;
                string? error = null;
                try { db = CharacterDatabase.Load(fs); }
                catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException) { error = ex.Message; }
                return (fs, list, db, error);
            });

            _fileSystem?.Dispose();
            _fileSystem = fileSystem;
            Archives.Clear();
            foreach (var item in items)
                Archives.Add(item);
            SelectedArchive = Archives.FirstOrDefault();

            Characters = characters is { Girls.Count: > 0 } ? new CharacterViewModel(fileSystem, characters) : null;
            if (Characters is not null)
            {
                Characters.SelectedGirl = Characters.Girls.FirstOrDefault(g => g.Id == AppHost.StartupCharacter) ?? Characters.Girls[0];
                if (Characters.Swimsuits.FirstOrDefault(s => s.Row == AppHost.StartupSwimsuit) is { } swimsuit)
                    Characters.SelectedSwimsuit = swimsuit;
                if (AppHost.StartupCut is int cut && cut > 0 && cut < Characters.Cuts.Count)
                    Characters.SelectedCutIndex = cut;
                SelectedTab = AppHost.StartupFile is null ? 0 : 1;
            }

            var editions = string.Join(", ", fileSystem.Install.Editions.Select(e => e.Edition));
            Status = $"{editions}: {items.Count} archive, {items.Sum(i => i.FileCount):N0} file"
                + (characters is null ? $". Không đọc được bảng nhân vật: {characterError}" : $", {characters.Girls.Count} nhân vật.");
            _settings.LastGameFolder = folder;
            _settings.Save();
        }
        catch (Exception ex)
        {
            Status = "Lỗi: " + ex.Message;
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
                    $"{model.Meshes.Count} mesh, {model.Meshes.Sum(m => m.Vertices.Length / RenderModel.VertexStride):N0} đỉnh, {model.Textures.Count} texture",
                    new ModelScene(model)),
                PreviewViewModel ready => ready,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            if (version == _previewVersion)
                Preview = new TextPreviewViewModel(title, "Không đọc được file", ex.ToString());
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
            return new TextPreviewViewModel(title, $"Bảng dữ liệu, {table.Rows.Count} dòng", table.Text);
        }
        if (ext == ".xx")
            return RenderModel.FromXx(XxReader.Read(archive.ReadAllBytes(item.Entry)), item.Entry.Name);
        if (ImageDecoder.IsImage(item.Entry.Name))
            return ImageDecoder.Decode(archive.ReadAllBytes(item.Entry), item.Entry.Name);

        // Unknown or not yet supported: show a hex dump of the beginning.
        var data = archive.ReadAllBytes(item.Entry);
        return new TextPreviewViewModel(title, $"{item.Size} · chưa hỗ trợ xem trước định dạng {ext}", HexDump(data, 4096));
    }

    static unsafe Bitmap ToBitmap(RgbaImage image)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var locked = bitmap.Lock();
        var span = new Span<byte>((void*)locked.Address, locked.RowBytes * image.Height);
        image.CopyToBgra(span, locked.RowBytes);
        return bitmap;
    }

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
            sb.AppendLine($"... ({data.Length - max:N0} byte nữa)");
        return sb.ToString();
    }

    public void Dispose() => _fileSystem?.Dispose();
}
