using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Characters;

namespace OpenDelulu.ViewModels;

/// <summary>
/// The Gravure photo mode (81_photosel, 82_photo; data in sb3g_0001 / sb3f_0001): the shoot is chosen on the memory
/// screen's columns (girl, place, time), then she holds poses on the stage while the st11 menu on the right sets
/// Location (her standing point), Pose (paBB_01.xa, 30 still poses per body type), Expression, Gaze (eyes on the
/// camera or ahead), Head (follows the camera or not), Gloss (oil shine 0..10) and Skin (tan %), Item (the pose's prop) and
/// Auto Camera. Layout from the exe's
/// sprite table (0x4CF9xx): panel (844, 4), rows every 66 px from y 38, arrows at x 858 / 932.
/// </summary>
public sealed partial class PlayViewModel
{
    const int PhotoRows = 6;
    static readonly string[] PhotoArchives = ["sb3g_0001.pp", "sb3f_0001.pp"];

    bool _photoSelect;
    bool _photoShoot;
    readonly int[] _photoValue = new int[PhotoRows]; // location, pose, expression, gaze, head, gloss (0-based)
    int _photoSkin;
    AnimationPlayer? _photoPoses;
    // Which pose set (1 Gravure pa0B_01, 2 the smaller pa0B_02) and the Item / Auto Camera buttons.
    int _photoSet;
    string? _photoArchive;
    bool _photoItem, _photoAutoCamera, _photoWhiten;

    [ObservableProperty] public partial bool IsPhotoMenuVisible { get; set; }
    [ObservableProperty] public partial bool IsPhotoMenuFolded { get; set; }

    public IReadOnlyList<MemorySprite> PhotoSprites { get; private set; } = [];

    /// <summary>The Gravure data (photo poses) is installed.</summary>
    public bool HasPhotoMode => PhotoArchives.Any(_fs.HasArchive);

    /// <summary>Opens the shoot selection (the memory columns without the date field).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void OpenPhotoSelect()
    {
        if (!HasPhotoMode || InGame || IsBusy)
            return;
        _photoSelect = true;
        OpenMemory();
    }

    /// <summary>Playback on the shoot selection: girl, place and time chosen → the shoot.</summary>
    async void StartPhotoShoot()
    {
        int girlIndex = _memoryChoice[0], place = _memoryChoice[3], time = _memoryChoice[4];
        if (girlIndex < 0 || place < 0 || time < 0 || IsBusy)
            return;
        if (Girls.FirstOrDefault(g => g.Id == MemoryGirls[girlIndex].Girl) is not { } girl || place >= _stages.Stages.Count)
            return;
        SelectedGirl = girl;
        SelectedStageIndex = place;
        SelectedTimeIndex = time;
        _photoShoot = true;
        IsMemoryOpen = false;
        MemorySprites.Clear();
        await EnterAsync();
    }

    /// <summary>After the stage has loaded for a shoot: the poses and the menu.</summary>
    void BeginPhotoShoot()
    {
        if (_girl is not { } girl)
            return;
        HideDateHud();
        Dialogue.Hide();
        _photoPoses = LoadPhotoPoses(girl);
        Array.Clear(_photoValue);
        _photoSkin = (int)Math.Round(Progress.Tan * 100);
        IsPhotoMenuFolded = false;
        IsPhotoMenuVisible = true;
        ApplyPhoto();
    }

    /// <summary>paBB_01.xa (Gravure) or paBB_02.xa (the smaller set) for her body type BB (from her idle animation, kiBB).</summary>
    AnimationPlayer? LoadPhotoPoses(Character girl)
    {
        string idle = girl.Girl.BodyAnimations.FirstOrDefault().File ?? "";
        int type = idle.Length >= 4 && int.TryParse(idle.AsSpan(2, 2), out int t) && t is >= 1 and <= 3 ? t : 1;
        foreach (var (archive, set) in new[] { ("sb3g_0001.pp", 1), ("sb3f_0001.pp", 2) })
        {
            string file = $"pa{type:00}_{set:00}_00_00_00.xa";
            if (_fs.HasArchive(archive) && _fs.ExistsReferenced(archive, file))
            {
                _photoSet = set;
                _photoArchive = archive;
                return _assembler.LoadBodyAnimation(girl.Model, new AssetRef(archive, file));
            }
        }
        return null;
    }

    int PhotoCount(int row) => row switch
    {
        0 => Math.Max(1, _stage?.PointGroup("Date").Count ?? 1),
        1 => Math.Max(1, (_photoPoses?.Clips.Count ?? 1) - 1),
        2 => Math.Max(1, _girl?.Animator.Face?.Expressions.Count ?? 1),
        3 or 4 => 2,
        _ => 11,
    };

    /// <summary>Puts the menu's values on her: point, pose, expression, eyes, neck, shine.</summary>
    void ApplyPhoto()
    {
        if (_girl is not { } girl)
            return;
        var animator = girl.Animator;
        _point = _photoValue[0];
        if (_photoPoses is { } poses)
        {
            poses.FollowNext = false;
            animator.Body = poses;
            poses.Play(Math.Clamp(_photoValue[1] + 1, 1, poses.Clips.Count - 1)); // clip 0 is the T pose
        }
        animator.Face?.SetExpression(_photoValue[2], _photoValue[2]);
        animator.EyesFollowTarget = _photoValue[3] == 0;
        if (animator.Neck is { } neck)
            neck.Active = _photoValue[4] == 0;
        float gloss = _photoValue[5] / 10f;
        foreach (var mesh in girl.Model.Meshes.Where(m => m.PartName == "body" && m.FrameName == "o01_P_Body"))
        {
            mesh.Gloss ??= new float[mesh.SourceVertices.Length];
            Array.Fill(mesh.Gloss, gloss);
        }
        animator.Invalidate();
        ApplyPhotoItem();
        PlaceGirl();
        FillPhotoMenu();
    }

    /// <summary>
    /// Item: the pose's prop (ld01 rows 181/182: pi00_%02d.xx, numbered like the poses of both sets, 1..30 Gravure and
    /// 31..35 the smaller set; only some poses have one, e.g. 21 an umbrella), standing where she stands.
    /// </summary>
    void ApplyPhotoItem()
    {
        int number = _photoValue[1] + 1 + (_photoSet == 2 ? 30 : 0);
        string file = $"pi00_{number:00}_00_00_00.xx";
        bool wanted = _photoItem && _photoArchive is { } archive && _fs.ExistsReferenced(archive, file);
        string? current = _prop?.Model.Name;
        if (wanted && current == file || !wanted && current is null)
            return;
        _prop = null;
        if (wanted)
        {
            try
            {
                var xx = OpenDelulu.Formats.Xx.XxReader.Read(_fs.ReadReferenced(_photoArchive!, file));
                var model = Engine.Assets.RenderModel.FromXx(xx, file);
                _prop = (model, new CharacterAnimator(model) { EyesFollowTarget = false });
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException)
            {
            }
        }
        BuildScene();
    }

    void FillPhotoMenu()
    {
        var sprites = new List<MemorySprite>();
        // st11 is in the Gravure archives only.
        string archive = PhotoArchives.FirstOrDefault(_fs.HasArchive) ?? PhotoArchives[0];
        void Add(double x, double y, double w, double h, string file) => sprites.Add(new MemorySprite(x, y, w, h, Ui.Get(archive, file)));
        string Digit(int d) => $"st11_03_{d:00}_00_00.tga";
        if (IsPhotoMenuFolded)
        {
            Add(984, 10, 24, 24, "st11_02_00_00_00.tga");
        }
        else
        {
            Add(844, 4, 176, 760, "st11_01_00_00_00.tga");
            for (int row = 0; row < PhotoRows; row++)
            {
                int shown = Math.Clamp(_photoValue[row] + (row == 5 ? 0 : 1), 0, 99);
                Add(956, 38 + 66 * row, 32, 32, Digit(shown / 10));
                Add(975, 38 + 66 * row, 32, 32, Digit(shown % 10));
            }
            string skin = Math.Clamp(_photoSkin, 0, 100).ToString("000");
            for (int i = 0; i < 3; i++)
                Add(919 + 19 * i, 433, 32, 32, Digit(skin[i] - '0'));
            if (_photoWhiten)
                Add(937, 466, 64, 24, "st11_05_00_00_00.tga");
            else
                Add(863, 466, 64, 24, "st11_04_00_00_00.tga");
            if (_photoItem)
                Add(856, 561, 152, 24, "st11_06_00_00_00.tga");
            if (_photoAutoCamera)
                Add(856, 589, 152, 24, "st11_07_00_00_00.tga");
        }
        PhotoSprites = sprites;
        OnPropertyChanged(nameof(PhotoSprites));
    }

    /// <summary>A click on the photo menu (screen coordinates); false when it missed the menu.</summary>
    public bool PressPhoto(double x, double y)
    {
        if (!IsPhotoMenuVisible)
            return false;
        if (x >= 984 && x < 1008 && y >= 10 && y < 34)
        {
            IsPhotoMenuFolded = !IsPhotoMenuFolded;
            FillPhotoMenu();
            return true;
        }
        if (IsPhotoMenuFolded || x < 844)
            return false;
        bool left = x >= 858 && x < 932, right = x >= 932 && x < 1006;
        for (int row = 0; row < PhotoRows; row++)
            if (y >= 70 + 66 * row && y < 94 + 66 * row && (left || right))
            {
                int count = PhotoCount(row);
                _photoValue[row] = (_photoValue[row] + (right ? 1 : -1) + count) % count;
                ApplyPhoto();
                return true;
            }
        if (y is >= 466 and < 490 && (x is >= 863 and < 927 || x is >= 937 and < 1001))
        {
            // Suntan or Whiten: what the Skin percentage does.
            _photoWhiten = x >= 937;
            FillPhotoMenu();
            _ = ApplyPhotoSkinAsync();
            return true;
        }
        if ((y is >= 495 and < 519 || y is >= 525 and < 549) && (left || right))
        {
            int step = (y >= 525 ? 10 : 1) * (right ? 1 : -1);
            _photoSkin = Math.Clamp(_photoSkin + step, 0, 100);
            FillPhotoMenu();
            _ = ApplyPhotoSkinAsync();
            return true;
        }
        if (x >= 856 && x < 1008)
        {
            if (y is >= 561 and < 585)
            {
                _photoItem = !_photoItem;
                ApplyPhoto();
            }
            else if (y is >= 589 and < 613)
            {
                // Auto Camera: the camera frames her pose by itself (a fixed view in front of her) until moved.
                _photoAutoCamera = !_photoAutoCamera;
                Scene?.RequestCamera(_photoAutoCamera ? new Rendering.CameraView(Pitch: 0.12f, Zoom: 1.1f) : null);
                FillPhotoMenu();
            }
            else if (y is >= 617 and < 641)
            {
                _photoAutoCamera = false;
                Scene?.RequestCamera(null);
                FillPhotoMenu();
            }
            else if (y is >= 654 and < 678)
                OpenWardrobe();
            else if (y is >= 682 and < 706)
                EndPhotoShoot(toSelect: true);
            else if (y is >= 710 and < 734)
                EndPhotoShoot(toSelect: false);
            return true;
        }
        return true;
    }

    /// <summary>Skin: the tan of the shoot (rebuilds her once the value settles), not saved to the game.</summary>
    async Task ApplyPhotoSkinAsync()
    {
        int wanted = _photoSkin;
        await Task.Delay(300);
        if (wanted != _photoSkin || _girl is not { } current || IsBusy)
            return;
        var spec = SpecFor(current.Girl.Id, Progress.SwimsuitRow) with { Tan = (_photoWhiten ? -wanted : wanted) / 100.0 };
        IsBusy = true;
        try
        {
            _girl = await Task.Run(() => _assembler.Build(spec));
            _photoPoses = LoadPhotoPoses(_girl);
            BuildScene();
            ApplyPhoto();
        }
        finally
        {
            IsBusy = false;
        }
    }

    void EndPhotoShoot(bool toSelect)
    {
        IsPhotoMenuVisible = false;
        PhotoSprites = [];
        OnPropertyChanged(nameof(PhotoSprites));
        _photoShoot = false;
        _photoPoses = null;
        _photoItem = _photoAutoCamera = _photoWhiten = false;
        _prop = null;
        _photoReturn = toSelect;
        Leave();
    }

    bool? _photoReturn;
}
