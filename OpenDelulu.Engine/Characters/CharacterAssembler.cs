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
public sealed record CharacterSpec(int GirlId, int HairIndex = 0, int? SwimsuitRow = null, int? CutOverride = null);

/// <summary>An assembled, animatable character.</summary>
public sealed record Character(GirlInfo Girl, RenderModel Model, CharacterAnimator Animator);

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
        return new Character(girl, model, animator);
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

    public RenderModel BuildModel(CharacterSpec spec)
    {
        var girl = Database.FindGirl(spec.GirlId) ?? throw new ArgumentException($"Unknown girl {spec.GirlId}.");
        var swimsuit = girl.Swimsuits.FirstOrDefault(s => s.Row == spec.SwimsuitRow) ?? girl.Swimsuits.FirstOrDefault();
        int cut = Math.Clamp(spec.CutOverride ?? swimsuit?.Cut ?? 1, 1, girl.Bodies.Count);

        var body = LoadModel(girl.Bodies[cut - 1]);
        var bodyTextures = new Dictionary<string, RgbaImage>(StringComparer.OrdinalIgnoreCase);
        AddTexture(bodyTextures, BodyPlaceholder, girl.SkinTexture);
        foreach (var texture in body.Textures.Where(t => t.Name.StartsWith(SwimsuitPlaceholderPrefix, StringComparison.OrdinalIgnoreCase)))
            AddTexture(bodyTextures, texture.Name, swimsuit?.Texture);

        var parts = new List<RenderPart> { new("body", body, body.Root, bodyTextures) };

        var faceTextures = new Dictionary<string, RgbaImage>(StringComparer.OrdinalIgnoreCase);
        AddTexture(faceTextures, FacePlaceholder, girl.FaceTexture);
        if (Attach(body, girl.Face) is { } face)
            parts.Add(new RenderPart("face", face.Model, face.Root, faceTextures));

        if (girl.Hairs.Count > 0 && Attach(body, girl.Hairs[Math.Clamp(spec.HairIndex, 0, girl.Hairs.Count - 1)]) is { } hair)
            parts.Add(new RenderPart("hair", hair.Model, hair.Root));

        string name = $"{girl.DisplayName} · {swimsuit?.Name ?? "?"}";
        return RenderModel.Build(name, parts, IsVisibleByDefault);
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
