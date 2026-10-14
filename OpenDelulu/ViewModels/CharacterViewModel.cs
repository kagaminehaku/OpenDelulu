using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Engine;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Rendering;

namespace OpenDelulu.ViewModels;

/// <summary>Character maker: pick a girl, hairstyle and swimsuit; the model is rebuilt on every change.</summary>
public partial class CharacterViewModel : ViewModelBase
{
    readonly CharacterAssembler _assembler;
    int _buildVersion;
    bool _suppressBuild;

    public CharacterViewModel(GameFileSystem fs, CharacterDatabase database)
    {
        _assembler = new CharacterAssembler(fs, database);
        Girls = database.Girls;
        Cuts = ["Theo đồ bơi", .. Enumerable.Range(1, Girls.Count == 0 ? 0 : Girls.Max(g => g.Bodies.Count)).Select(i => $"Kiểu thân {i}")];
        TableInfo = $"Bảng dữ liệu: {database.TableArchive}, {Girls.Count} nhân vật";
    }

    public IReadOnlyList<GirlInfo> Girls { get; }
    public IReadOnlyList<string> Cuts { get; }
    public string TableInfo { get; }
    public ObservableCollection<string> Hairs { get; } = [];
    public ObservableCollection<SwimsuitInfo> Swimsuits { get; } = [];

    [ObservableProperty]
    public partial GirlInfo? SelectedGirl { get; set; }

    [ObservableProperty]
    public partial int SelectedHairIndex { get; set; }

    [ObservableProperty]
    public partial SwimsuitInfo? SelectedSwimsuit { get; set; }

    [ObservableProperty]
    public partial int SelectedCutIndex { get; set; }

    [ObservableProperty]
    public partial ModelPreviewViewModel? Preview { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    partial void OnSelectedGirlChanged(GirlInfo? value)
    {
        int previousRow = SelectedSwimsuit?.Row ?? 0;
        int previousHair = SelectedHairIndex;

        _suppressBuild = true;
        Hairs.Clear();
        Swimsuits.Clear();
        if (value is not null)
        {
            for (int i = 0; i < value.Hairs.Count; i++)
                Hairs.Add($"Kiểu tóc {i + 1}");
            foreach (var suit in value.Swimsuits)
                Swimsuits.Add(suit);
        }
        SelectedHairIndex = Math.Clamp(previousHair, 0, Math.Max(0, Hairs.Count - 1));
        SelectedSwimsuit = Swimsuits.FirstOrDefault(s => s.Row == previousRow) ?? Swimsuits.FirstOrDefault();
        _suppressBuild = false;
        _ = RebuildAsync();
    }

    partial void OnSelectedHairIndexChanged(int value) => _ = RebuildAsync();

    partial void OnSelectedSwimsuitChanged(SwimsuitInfo? value) => _ = RebuildAsync();

    partial void OnSelectedCutIndexChanged(int value) => _ = RebuildAsync();

    async Task RebuildAsync()
    {
        if (_suppressBuild || SelectedGirl is not { } girl)
            return;

        int version = ++_buildVersion;
        var spec = new CharacterSpec(girl.Id, Math.Max(0, SelectedHairIndex), SelectedSwimsuit?.Row,
            SelectedCutIndex > 0 ? SelectedCutIndex : null);
        IsBusy = true;
        try
        {
            var model = await Task.Run(() => _assembler.Build(spec));
            if (version != _buildVersion)
                return;
            int visible = model.Meshes.Count(m => m.Visible);
            Preview = new ModelPreviewViewModel(model.Name,
                $"{visible}/{model.Meshes.Count} mesh hiển thị, {model.Meshes.Sum(m => m.Vertices.Length / RenderModel.VertexStride):N0} đỉnh",
                new ModelScene(model));
            Status = "";
        }
        catch (Exception ex)
        {
            if (version == _buildVersion)
                Status = "Lỗi ghép nhân vật: " + ex.Message;
        }
        finally
        {
            if (version == _buildVersion)
                IsBusy = false;
        }
    }
}
