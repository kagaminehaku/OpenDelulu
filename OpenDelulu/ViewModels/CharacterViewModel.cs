using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenDelulu.Engine;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Engine.Audio;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Localization;
using OpenDelulu.Rendering;
using OpenDelulu.Services;

namespace OpenDelulu.ViewModels;

/// <summary>
/// Character maker: pick a girl, hairstyle and swimsuit (rebuilds the model), then an animation file,
/// clip and expression (applied live to the current model).
/// </summary>
public partial class CharacterViewModel : ViewModelBase
{
    readonly CharacterAssembler _assembler;
    readonly VoiceLibrary _voices;
    int _buildVersion;
    int _animationVersion;
    bool _suppressBuild;
    bool _syncingClip;
    Character? _character;
    ModelScene? _scene;

    public CharacterViewModel(GameFileSystem fs, CharacterDatabase database, VoiceLibrary voices)
    {
        _assembler = new CharacterAssembler(fs, database);
        _voices = voices;
        Girls = database.Girls;
        Cuts = [new("char.cutAuto"), .. Enumerable.Range(1, Girls.Count == 0 ? 0 : Girls.Max(g => g.Bodies.Count)).Select(i => new LocalizedText(() => F("char.cutN", i)))];
        _tableArchive = database.TableArchive;
    }

    public IReadOnlyList<GirlInfo> Girls { get; }
    readonly string _tableArchive;

    public IReadOnlyList<LocalizedText> Cuts { get; }
    public string TableInfo => F("char.tableInfo", _tableArchive, Girls.Count);
    public ObservableCollection<LocalizedText> Hairs { get; } = [];

    protected override void OnLanguageChanged() => OnPropertyChanged(nameof(TableInfo));
    public ObservableCollection<SwimsuitInfo> Swimsuits { get; } = [];
    public ObservableCollection<AssetRef> Animations { get; } = [];
    public ObservableCollection<AnimationClipInfo> Clips { get; } = [];
    public ObservableCollection<string> Expressions { get; } = [];

    [ObservableProperty] public partial GirlInfo? SelectedGirl { get; set; }
    [ObservableProperty] public partial int SelectedHairIndex { get; set; }
    [ObservableProperty] public partial SwimsuitInfo? SelectedSwimsuit { get; set; }
    [ObservableProperty] public partial int SelectedCutIndex { get; set; }
    [ObservableProperty] public partial AssetRef? SelectedAnimation { get; set; }
    [ObservableProperty] public partial AnimationClipInfo? SelectedClip { get; set; }
    [ObservableProperty] public partial string? SelectedExpression { get; set; }
    [ObservableProperty] public partial bool IsPlaying { get; set; } = true;
    [ObservableProperty] public partial bool FollowNext { get; set; } = true;
    [ObservableProperty] public partial bool AutoBlink { get; set; } = true;
    [ObservableProperty] public partial bool EyesFollowCamera { get; set; } = true;
    [ObservableProperty] public partial double Speed { get; set; } = 1;
    [ObservableProperty] public partial ModelPreviewViewModel? Preview { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }

    /// <summary>Indexes match <see cref="VoiceGroupCodes"/>.</summary>
    public LocalizedText[] VoiceGroups { get; } = [new("char.voiceAll"), new("char.voicePo"), new("char.voicePh"), new("char.voicePd"), new("char.voicePe")];
    static readonly string?[] VoiceGroupCodes = [null, "po", "ph", "pd", "pe"];
    [ObservableProperty] public partial int SelectedVoiceGroupIndex { get; set; }
    [ObservableProperty] public partial string VoiceInfo { get; set; } = "";

    // ------------------------------------------------------------------ model (rebuild)

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
            {
                int number = i + 1;
                Hairs.Add(new LocalizedText(() => F("char.hairN", number)));
            }
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
        string? keepAnimation = SelectedAnimation?.File;
        int? keepClip = SelectedClip?.Index;
        IsBusy = true;
        try
        {
            var character = await Task.Run(() => _assembler.Build(spec));
            if (version != _buildVersion)
                return;

            // Keep the chosen animation/clip when only the outfit changed.
            // The previous animation may not exist for this girl's body type (dt01_* vs dt02_*).
            if (FindAnimation(character.Girl.BodyAnimations, keepAnimation) is { } same
                && same != character.Girl.BodyAnimations.FirstOrDefault())
                character.Animator.Body = await Task.Run(() => _assembler.LoadBodyAnimation(character.Model, same));
            if (keepClip is int clip && character.Animator.Body?.Clips.Any(c => c.Index == clip) == true)
                character.Animator.Body.Play(clip);

            AttachCharacter(character);
            var model = character.Model;
            int visible = model.Meshes.Count(m => m.Visible);
            Preview = new ModelPreviewViewModel(model.Name,
                F("char.previewDetails", visible, model.Meshes.Count, model.Meshes.Sum(m => m.Vertices.Length / RenderModel.VertexStride)),
                _scene!);
            Status = "";
            if (AppHost.StartupSpeak is { } group)
            {
                // After the camera intro, so the line is seen (used for automated screenshots).
                AppHost.StartupSpeak = null;
                SelectedVoiceGroupIndex = Math.Max(0, Array.IndexOf(VoiceGroupCodes, group));
                await Task.Delay(2000);
                Speak();
            }
        }
        catch (Exception ex)
        {
            if (version == _buildVersion)
                Status = F("char.buildError", ex.Message);
        }
        finally
        {
            if (version == _buildVersion)
                IsBusy = false;
        }
    }

    void AttachCharacter(Character character)
    {
        if (_character?.Animator.Body is { } oldBody)
            oldBody.ClipChanged -= OnClipChanged;
        _character = character;
        _scene = new ModelScene(character.Model, character.Animator);

        _syncingClip = true;
        Animations.Clear();
        foreach (var animation in character.Girl.BodyAnimations)
            Animations.Add(animation);
        SelectedAnimation = FindAnimation(Animations, character.Animator.Body?.Name);

        Expressions.Clear();
        foreach (var expression in character.Animator.Face?.Expressions ?? [])
            Expressions.Add(expression);
        if (character.Animator.Face is { } face)
        {
            if (SelectedExpression is { } keep && Expressions.Contains(keep))
                face.Expression = keep;
            SelectedExpression = face.Expression;
        }
        _syncingClip = false;

        ApplyBody(character.Animator.Body);
        ApplyPlaybackSettings();
    }

    // ------------------------------------------------------------------ voice + lip sync

    [RelayCommand]
    void Speak()
    {
        if (_character is not { } character)
            return;
        string? group = VoiceGroupCodes[Math.Clamp(SelectedVoiceGroupIndex, 0, VoiceGroupCodes.Length - 1)];
        var lines = _voices.For(character.Girl.Id).Where(l => group is null || l.Group == group).ToList();
        if (lines.Count == 0)
        {
            VoiceInfo = T("char.noVoices");
            return;
        }
        var line = lines[Random.Shared.Next(lines.Count)];
        try
        {
            var speech = _voices.Speak(line, AudioSystem.Shared);
            character.Animator.Speech = speech;
            _scene?.NotifyChanged();
            string text = speech.Script is { } vss ? $" · {vss.Text}" : "";
            VoiceInfo = $"{line.File} ({speech.Voice.Duration?.TotalSeconds:0.0} s){text}"
                + (AudioSystem.Shared.OutputError is { } error ? " · " + F("audio.deviceError", error) : "");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            VoiceInfo = F("audio.playError", line.File, ex.Message);
        }
    }

    [RelayCommand]
    void StopVoice() => AudioSystem.Shared.Mixer.StopBus(AudioBus.Voice, 0.1);

    // ------------------------------------------------------------------ animation (live)

    partial void OnSelectedAnimationChanged(AssetRef? value)
    {
        if (!_syncingClip && value is { } asset)
            _ = SwitchAnimationAsync(asset);
    }

    /// <summary>
    /// <see cref="AssetRef"/> is a struct: a plain FirstOrDefault returns an empty ref instead of null
    /// when nothing matches, which then fails deep inside the file system.
    /// </summary>
    static AssetRef? FindAnimation(IEnumerable<AssetRef> animations, string? file) =>
        file is null ? null : animations.Where(a => a.File == file).Select(a => (AssetRef?)a).FirstOrDefault();

    async Task SwitchAnimationAsync(AssetRef asset)
    {
        if (_character is not { } character || character.Animator.Body?.Name == asset.File)
            return;
        int version = ++_animationVersion;
        AnimationPlayer? player;
        try
        {
            player = await Task.Run(() => _assembler.LoadBodyAnimation(character.Model, asset));
        }
        catch (Exception ex)
        {
            Status = F("char.animationError", asset.File, ex.Message);
            return;
        }
        if (version != _animationVersion || !ReferenceEquals(character, _character))
            return;
        if (character.Animator.Body is { } old)
            old.ClipChanged -= OnClipChanged;
        character.Animator.Body = player;
        ApplyBody(player);
        ApplyPlaybackSettings();
        Status = player is null ? F("char.noSkeleton", asset.File) : "";
    }

    void ApplyBody(AnimationPlayer? body)
    {
        _syncingClip = true;
        Clips.Clear();
        foreach (var clip in body?.Clips ?? [])
            Clips.Add(clip);
        SelectedClip = body?.Current is { } current ? Clips.FirstOrDefault(c => c.Index == current.Index) : null;
        _syncingClip = false;
        if (body is not null)
            body.ClipChanged += OnClipChanged;
        _scene?.NotifyChanged();
    }

    void OnClipChanged(AnimationClipInfo clip)
    {
        // Raised from the render loop when a clip hands over to its "next" clip.
        _syncingClip = true;
        SelectedClip = Clips.FirstOrDefault(c => c.Index == clip.Index);
        _syncingClip = false;
    }

    partial void OnSelectedClipChanged(AnimationClipInfo? value)
    {
        if (_syncingClip || value is null || _character?.Animator.Body is not { } body)
            return;
        body.Play(value.Index);
        _scene?.NotifyChanged();
    }

    partial void OnSelectedExpressionChanged(string? value)
    {
        if (_syncingClip || value is null || _character?.Animator.Face is not { } face)
            return;
        face.Expression = value;
        _scene?.NotifyChanged();
    }

    partial void OnIsPlayingChanged(bool value) => ApplyPlaybackSettings();

    partial void OnFollowNextChanged(bool value) => ApplyPlaybackSettings();

    partial void OnAutoBlinkChanged(bool value) => ApplyPlaybackSettings();

    partial void OnEyesFollowCameraChanged(bool value) => ApplyPlaybackSettings();

    partial void OnSpeedChanged(double value) => ApplyPlaybackSettings();

    void ApplyPlaybackSettings()
    {
        if (_character?.Animator is not { } animator)
            return;
        animator.EyesFollowTarget = EyesFollowCamera;
        if (animator.Body is { } body)
        {
            body.IsPlaying = IsPlaying;
            body.FollowNext = FollowNext;
            body.SpeedScale = Speed;
        }
        if (animator.Face is { } face)
        {
            face.AutoBlink = AutoBlink;
            if (!AutoBlink)
                face.EyeClosed = 0;
        }
        _scene?.NotifyChanged();
    }
}
