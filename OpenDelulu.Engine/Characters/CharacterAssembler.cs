using System.Numerics;
using OpenDelulu.Engine.Animation;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Formats.Imaging;
using OpenDelulu.Formats.Character;
using OpenDelulu.Formats.Xa;
using OpenDelulu.Formats.Xx;

namespace OpenDelulu.Engine.Characters;

/// <summary>What to build: a girl, her hairstyle and swimsuit.</summary>
/// <param name="HairIndex">0-based index into <see cref="GirlInfo.Hairs"/>.</param>
/// <param name="SwimsuitRow">Row in lbNN; null = first swimsuit.</param>
/// <param name="CutOverride">Force a body variant (1-based) instead of the swimsuit's own.</param>
/// <param name="Accessories">Worn accessories: place (lc00 row, 1..15) → item row of its lcNN table.</param>
public sealed record CharacterSpec(int GirlId, int HairIndex = 0, int? SwimsuitRow = null, int? CutOverride = null,
    IReadOnlyDictionary<int, int>? Accessories = null);

/// <summary>An assembled, animatable character.</summary>
public sealed record Character(GirlInfo Girl, RenderModel Model, CharacterAnimator Animator)
{
    /// <summary>1-based body variant the model was built from (follows the swimsuit).</summary>
    public int Cut { get; init; } = 1;
}

/// <summary>
/// Assembles a character the way the game does: body variant + face + hair attached to the head joint,
/// with the 16x16 placeholder textures replaced by the skin, face and swimsuit textures from the tables.
/// </summary>
public sealed class CharacterAssembler(GameFileSystem fs, CharacterDatabase database)
{
    // Placeholder texture names embedded in the models.
    const string BodyPlaceholder = "body.bmp";
    const string FacePlaceholder = "face.bmp";
    /// <summary>Swimsuit placeholders are named per body variant: mizugi01.tga ... mizugi05.tga.</summary>
    const string SwimsuitPlaceholderPrefix = "mizugi";

    public CharacterDatabase Database { get; } = database;

    AccessoryDatabase? _accessories;

    /// <summary>The accessory tables (lc00..lc11), read on first use.</summary>
    public AccessoryDatabase Accessories => _accessories ??= AccessoryDatabase.Load(fs);

    /// <summary>Placeholder of the body's pareo meshes, replaced by the chosen lc07 texture.</summary>
    const string PareoPlaceholder = "pareo.tga";

    /// <summary>Builds the model and its animator (base body animation + face morphs).</summary>
    public Character Build(CharacterSpec spec)
    {
        var girl = Database.FindGirl(spec.GirlId) ?? throw new ArgumentException($"Unknown girl {spec.GirlId}.");
        var model = BuildModel(spec);
        var animator = new CharacterAnimator(model);

        if (girl.FaceAnimation is { } faceXa && TryLoadXa(faceXa)?.Morphs is { } morphs)
            animator.Face = new MorphPlayer(model, morphs, "face");
        if (girl.BodyAnimations.FirstOrDefault() is { } bodyXa)
            animator.Body = LoadBodyAnimation(model, bodyXa);
        animator.Physics = LoadPhysics(girl, model, spec);
        animator.Neck = NeckGaze.TryCreate(model, LoadNeck());
        return new Character(girl, model, animator) { Cut = CutFor(girl, spec) };
    }

    /// <summary>
    /// Switches the girl to a date activity: her animation, plus the activity's prop (if any) with its own animation
    /// kept on the same clip as hers (swing seat and girl move together).
    /// </summary>
    public (RenderModel Model, CharacterAnimator? Animator)? StartActivity(Character girl, DateActivity activity)
    {
        var body = LoadBodyAnimation(girl.Model, activity.Animation);
        if (body is not null)
            girl.Animator.Body = body;
        if (activity.Prop is not { } prop)
            return null;
        var propModel = RenderModel.FromXx(LoadModel(prop), prop.File);
        CharacterAnimator? propAnimator = null;
        if (activity.PropAnimation is { } xa && TryLoadXa(xa)?.Animation is { } animation)
        {
            var player = new AnimationPlayer(propModel.Rig, animation, xa.File);
            if (player.BoundTracks > 0)
            {
                propAnimator = new CharacterAnimator(propModel) { Body = player, EyesFollowTarget = false };
                if (body?.Current is { } current)
                    player.Play(current.Index);
                if (body is not null)
                    body.ClipChanged += clip => player.Play(clip.Index);
            }
        }
        return (propModel, propAnimator);
    }

    /// <summary>Hair (.kys of the chosen hairstyle) and breast/buttock (.tty) springs; null when none apply.</summary>
    SpringBones? LoadPhysics(GirlInfo girl, RenderModel model, CharacterSpec spec)
    {
        var physics = new SpringBones(model.Rig);
        int hair = Math.Clamp(spec.HairIndex, 0, Math.Max(0, girl.HairPhysics.Count - 1));
        try
        {
            if (girl.HairPhysics.Count > 0 && girl.HairPhysics[hair] is { } kys)
                physics.AddHair(KysFile.Read(fs.ReadReferenced(kys.Archive, kys.File)));
            // Set 1 is the swimsuit locator set of the breast files ("水着用ロケーター") and the buttock set ("お尻").
            foreach (var tty in girl.BodyPhysics)
                physics.AddJiggle(TtyFile.Read(fs.ReadReferenced(tty.Archive, tty.File)), set: 1);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException)
        {
        }
        return physics.BoneCount > 0 ? physics : null;
    }

    /// <summary>Loads a body .xa for <paramref name="model"/>; null if it has no skeletal animation.</summary>
    public AnimationPlayer? LoadBodyAnimation(RenderModel model, AssetRef asset) =>
        TryLoadXa(asset)?.Animation is { } animation ? new AnimationPlayer(model.Rig, animation, asset.File) : null;

    XaModel? TryLoadXa(AssetRef asset)
    {
        try
        {
            return XaReader.Read(fs.ReadReferenced(asset.Archive, asset.File));
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }

    LookFile? _neck;
    bool _neckLoaded;

    /// <summary>bo00_00_00_00_00.neck (shared by every body): how the head turns to look at someone.</summary>
    LookFile? LoadNeck()
    {
        if (_neckLoaded)
            return _neck;
        _neckLoaded = true;
        try
        {
            if (fs.FindAnywhere("bo00_00_00_00_00.neck") is { } found)
                _neck = LookFile.Read(fs.ReadFile(found.Archive.Name, found.Entry.Name));
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
        }
        return _neck;
    }

    static int CutFor(GirlInfo girl, CharacterSpec spec)
    {
        var swimsuit = girl.Swimsuits.FirstOrDefault(s => s.Row == spec.SwimsuitRow) ?? girl.Swimsuits.FirstOrDefault();
        return Math.Clamp(spec.CutOverride ?? swimsuit?.Cut ?? 1, 1, Math.Max(1, girl.Bodies.Count));
    }

    public RenderModel BuildModel(CharacterSpec spec)
    {
        var girl = Database.FindGirl(spec.GirlId) ?? throw new ArgumentException($"Unknown girl {spec.GirlId}.");
        var swimsuit = girl.Swimsuits.FirstOrDefault(s => s.Row == spec.SwimsuitRow) ?? girl.Swimsuits.FirstOrDefault();
        int cut = CutFor(girl, spec);

        var body = LoadModel(girl.Bodies[cut - 1]);
        var bodyTextures = new Dictionary<string, RgbaImage>(StringComparer.OrdinalIgnoreCase);
        AddTexture(bodyTextures, BodyPlaceholder, girl.SkinTexture);
        // The overlay meshes of the intimate areas carry a "zxNN_MM" placeholder; the girl's texture comes from ld00.
        foreach (var texture in body.Textures.Where(t => t.Name.StartsWith("zx", StringComparison.OrdinalIgnoreCase)))
            AddTexture(bodyTextures, texture.Name, girl.DetailTexture);
        foreach (var texture in body.Textures.Where(t => t.Name.StartsWith(SwimsuitPlaceholderPrefix, StringComparison.OrdinalIgnoreCase)))
            AddTexture(bodyTextures, texture.Name, swimsuit?.Texture);

        var parts = new List<RenderPart> { new("body", body, body.Root, bodyTextures) };

        var faceTextures = new Dictionary<string, RgbaImage>(StringComparer.OrdinalIgnoreCase);
        AddTexture(faceTextures, FacePlaceholder, girl.FaceTexture);
        var holders = new List<XxModel> { body };
        if (Attach(body, girl.Face) is { } face)
        {
            parts.Add(new RenderPart("face", face.Model, face.Root, faceTextures));
            holders.Add(face.Model);
        }

        if (girl.Hairs.Count > 0 && Attach(body, girl.Hairs[Math.Clamp(spec.HairIndex, 0, girl.Hairs.Count - 1)]) is { } hair)
        {
            parts.Add(new RenderPart("hair", hair.Model, hair.Root));
            holders.Add(hair.Model);
        }

        bool pareo = false;
        foreach (var (place, row) in spec.Accessories ?? new Dictionary<int, int>())
        {
            if (Accessories.Slot(place) is not { } slot || Accessories.Find(slot.Table, row) is not { } item)
                continue;
            if (slot.IsPareo)
            {
                // The pareo is part of every body; the item is only its texture.
                AddTexture(bodyTextures, PareoPlaceholder, item.Model);
                pareo = bodyTextures.ContainsKey(PareoPlaceholder);
                continue;
            }
            if (AttachAccessory(body, holders, slot, item) is { } accessory)
                parts.Add(new RenderPart($"accessory{place}", accessory.Model, accessory.Root));
        }

        string name = $"{girl.DisplayName} · {swimsuit?.Name ?? "?"}";
        // Like the exe's lx01 groups: the intimate-area meshes are shown when the outfit has no bottom covering them.
        bool covered = body.FindFrame("o01_P_Pantu01")?.Mesh is not null;
        return RenderModel.Build(name, parts, mesh => IsVisibleByDefault(mesh) || (!covered && IsDetail(mesh.FrameName))
            || pareo && mesh.PartName == "body" && mesh.FrameName.StartsWith("o01_P_Pareo", StringComparison.Ordinal));
    }

    /// <summary>
    /// Hangs an accessory on the character: rigid items are modelled around their own root frame (o01_N_Mimi_AC…), which
    /// takes the place of the holder frame of the same role on the body, face or hair (o01_N_Mimi_L_AC…); shoes are
    /// skinned to the leg bones and only need to join the body's hierarchy.
    /// </summary>
    (XxModel Model, XxFrame Root)? AttachAccessory(XxModel body, IReadOnlyList<XxModel> holders, AccessorySlot slot, AccessoryItem item)
    {
        XxModel model;
        try { model = LoadModel(item.Model); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or EndOfStreamException) { return null; }
        var root = (item.ItemFrame ?? slot.ItemFrame) is { } rootName ? model.FindFrame(rootName) : null;
        if (root is null)
            return null;
        XxFrame? holder;
        if (slot.IsShoes)
        {
            holder = body.Root;
        }
        else
        {
            string? holderName = item.BodyFrame ?? slot.BodyFrame;
            holder = holderName is null ? null : holders.Select(h => h.FindFrame(holderName)).FirstOrDefault(f => f is not null);
        }
        if (holder is null)
            return null;
        holder.AttachChild(root);
        return (model, root);
    }

    (XxModel Model, XxFrame Root)? Attach(XxModel body, PartRef part)
    {
        var model = LoadModel(part.Model);
        var parent = body.FindFrame(part.ParentFrame);
        var root = model.FindFrame(part.AttachFrame);
        if (parent is null || root is null)
            return null;
        // Parts are modelled in place (hair and face already sit at head height), so the attachment
        // only has to follow the joint's motion relative to its bind pose: local' = local * inverse(jointBind).
        if (Matrix4x4.Invert(parent.WorldMatrix, out var inverseBind))
            root.Matrix *= inverseBind;
        parent.AttachChild(root);
        return (model, root);
    }

    XxModel LoadModel(AssetRef asset) => XxReader.Read(fs.ReadReferenced(asset.Archive, asset.File));

    void AddTexture(Dictionary<string, RgbaImage> map, string placeholder, AssetRef? asset)
    {
        if (asset is not { } a)
            return;
        try
        {
            map[placeholder] = ImageDecoder.Decode(fs.ReadReferenced(a.Archive, a.File), a.File);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or NotSupportedException)
        {
            // Keep the placeholder; a missing optional texture must not break the character.
        }
    }

    /// <summary>
    /// The default "wearing the swimsuit" state. The body file also carries alternative states
    /// (Bura02, Pantu02-04), the pareo, collision/glare helpers and detail meshes the game toggles
    /// during scenes; those start hidden and can be switched on in the viewer.
    /// </summary>
    /// <summary>
    /// Intimate-area meshes of the body models (deliberately scrambled names): "…0" use the skin texture,
    /// "…1" lie on top with the zx texture. The game toggles both groups (lx01 rows 17..20) by outfit state.
    /// </summary>
    public static readonly string[] DetailFrames =
        ["o01_P_qetwry0", "o01_P_adgsfh0", "o01_P_zcbxvn0", "o01_P_wryqet1", "o01_P_sfhadg1", "o01_P_xvnzcb1"];

    static bool IsDetail(string frame) => Array.IndexOf(DetailFrames, frame) >= 0;

    static bool IsVisibleByDefault(RenderMesh mesh)
    {
        if (RenderModel.IsHelperMaterial(mesh.MaterialName))
            return false;
        string frame = mesh.FrameName;
        return mesh.PartName switch
        {
            "body" => frame is "o01_P_Body" or "o01_P_Bura01" or "o01_P_Pantu01"
                      || frame.StartsWith("o01_P_Tume", StringComparison.Ordinal)
                      || frame.StartsWith("o01_P_Ribon", StringComparison.Ordinal),
            "face" => frame != "o01_P_Hoho", // blush overlay, driven by expressions
            _ => true,
        };
    }
}
