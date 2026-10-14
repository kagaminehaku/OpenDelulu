using System.Globalization;
using OpenDelulu.Formats.Lst;

namespace OpenDelulu.Engine.Characters;

/// <summary>An (archive, file) pair as written in the game's .lst tables.</summary>
public readonly record struct AssetRef(string Archive, string File)
{
    /// <summary>Reads two adjacent cells; "0" or a missing cell means "none".</summary>
    public static AssetRef? FromRow(string[] row, int archiveColumn)
    {
        if (archiveColumn + 1 >= row.Length)
            return null;
        string archive = row[archiveColumn].Trim(), file = row[archiveColumn + 1].Trim();
        return archive is "" or "0" || file is "" or "0" ? null : new AssetRef(archive, file);
    }

    public override string ToString() => $"{Archive}/{File}";
}

/// <summary>A part model and where it is attached on the body: <c>body[ParentFrame] ← part[AttachFrame]</c>.</summary>
public sealed record PartRef(AssetRef Model, string ParentFrame, string AttachFrame);

/// <summary>A swimsuit from the per-girl table lbNN.</summary>
/// <param name="Cut">1-based body variant (boXX_NN.xx) whose meshes match this swimsuit's UV layout.</param>
public sealed record SwimsuitInfo(int Row, string Name, AssetRef Texture, AssetRef? TanTexture, int Cut, AssetRef? Icon);

/// <summary>
/// A date activity (ld01 rows 151..171): the girl's animation (dtBB_NN.xa for her body type) and an optional prop
/// with its own animation (di00_NN.xx/.xa: swing, float ring, banana boat…). Activity N happens at the stage's
/// o01_N_Point_DateNN.
/// </summary>
public sealed record DateActivity(int Number, AssetRef Animation, AssetRef? Prop, AssetRef? PropAnimation);

/// <summary>
/// A pose of the sun-oil scene (ld01 rows 81..84, "onGG_PP.xa"): about 200 clips of the girl waiting, reacting to
/// oil and touches, refusing, and the transitions between them. The exe picks it with the oil menu's four icons.
/// </summary>
public sealed record OilPose(int Number, AssetRef Animation);

/// <summary>A playable girl, assembled from ld00 (skin), ld01_NN (parts) and lbNN (swimsuits).</summary>
public sealed record GirlInfo(
    int Id,
    AssetRef? SkinTexture,
    AssetRef? FaceTexture,
    PartRef Face,
    IReadOnlyList<PartRef> Hairs,
    IReadOnlyList<AssetRef> Bodies,
    IReadOnlyList<SwimsuitInfo> Swimsuits,
    AssetRef? FaceAnimation,
    IReadOnlyList<AssetRef> BodyAnimations)
{
    /// <summary>Hair physics (.kys) per entry of <see cref="Hairs"/>; ld01 rows 16..20 next to the hairs in rows 11..15.</summary>
    public IReadOnlyList<AssetRef?> HairPhysics { get; init; } = [];

    /// <summary>
    /// Texture of the intimate-area overlay meshes (ld00 rows 21..28, "zxNN_*.tga"; the base game ships 16x16
    /// mosaic tiles, texture mods replace them), or null.
    /// </summary>
    public AssetRef? DetailTexture { get; init; }

    /// <summary>Date activities by number (1..21).</summary>
    public IReadOnlyList<DateActivity> DateActivities { get; init; } = [];

    /// <summary>Breast (ld01 row 31) and buttocks (row 32) jiggle (.tty).</summary>
    public IReadOnlyList<AssetRef> BodyPhysics { get; init; } = [];

    /// <summary>Sun-oil scene poses by number (1..4).</summary>
    public IReadOnlyList<OilPose> OilPoses { get; init; } = [];

    public string DisplayName => $"{CharacterDatabase.NameOf(Id)} ({Id:00})";
}

/// <summary>
/// Character definitions read from the game tables. File names follow the exe's format strings
/// (<c>ld%02d_%02d_00_00_00.lst</c>, <c>lb%02d_00_00_00_00.lst</c>...).
/// </summary>
public sealed class CharacterDatabase
{
    /// <summary>Archives holding the tables, newest first (Bonus Disc, then base game).</summary>
    public static readonly string[] TableArchives = ["SB3x_1001.pp", "SB3x_1000.pp", "SB3_0001.pp", "sb3_0000.pp"];

    // lbNN columns (0 = row number).
    const int SwimsuitNameColumn = 1;
    const int SwimsuitTextureColumn = 2;
    const int SwimsuitTanColumn = 4;
    const int SwimsuitGroupColumn = 8;
    const int SwimsuitIconColumn = 61;

    public string TableArchive { get; }
    public IReadOnlyList<GirlInfo> Girls { get; }

    CharacterDatabase(string tableArchive, IReadOnlyList<GirlInfo> girls)
    {
        TableArchive = tableArchive;
        Girls = girls;
    }

    public GirlInfo? FindGirl(int id) => Girls.FirstOrDefault(g => g.Id == id);

    /// <summary>Names as written in the English patch's menus (the scripts keep the Japanese ones).</summary>
    public static string NameOf(int id) => id switch
    {
        1 => "Manami",
        2 => "Maya",
        3 => "Esk",
        4 => "Eo",
        5 => "Reiko",
        6 => "Maria",
        11 => "Bael",
        12 => "Fei",
        _ => $"#{id:00}",
    };

    public static CharacterDatabase Load(GameFileSystem fs)
    {
        string tableArchive = TableArchives.FirstOrDefault(fs.HasArchive)
            ?? throw new FileNotFoundException("No table archive (sb3_0000.pp / SB3x_1000.pp) is installed.");
        var archive = fs.GetArchive(tableArchive);

        LstTable? Table(string name) =>
            archive.Find(name) is { } entry ? LstTable.Decode(archive.ReadAllBytes(entry), name) : null;

        var skins = Table("ld00_00_00_00_00.lst") ?? throw new InvalidDataException($"ld00 missing in {tableArchive}.");
        var girls = new List<GirlInfo>();

        // ld00 rows 21..28: per girl, the overlay texture "zxNN_*.tga" (first of four variants).
        var details = new Dictionary<int, AssetRef>();
        foreach (var row in skins.Rows)
            if (int.TryParse(row[0], out int n) && n is >= 21 and <= 30 && AssetRef.FromRow(row, 1) is { } zx
                && zx.File.StartsWith("zx", StringComparison.OrdinalIgnoreCase) && zx.File.Length >= 4
                && int.TryParse(zx.File.AsSpan(2, 2), out int zxGirl) && fs.ExistsReferenced(zx.Archive, zx.File))
                details.TryAdd(zxGirl, zx);

        // ld00 rows 1..10: per-girl skin textures; the girl id is the number in "boNN_00_00_00_00.bmp".
        foreach (var row in skins.Rows)
        {
            if (!int.TryParse(row[0], out int rowNumber) || rowNumber is < 1 or > 10)
                continue;
            var skin = AssetRef.FromRow(row, 1);
            if (skin is null || !TryParseId(skin.Value.File, out int id))
                continue;

            var setup = Table($"ld01_{id:00}_00_00_00.lst");
            if (setup is null)
                continue;
            var rows = setup.Rows.Where(r => int.TryParse(r[0], out _)).ToDictionary(r => int.Parse(r[0], CultureInfo.InvariantCulture));

            PartRef? Part(int n) =>
                rows.TryGetValue(n, out var r) && AssetRef.FromRow(r, 1) is { } model && model.File.EndsWith(".xx", StringComparison.OrdinalIgnoreCase)
                    ? new PartRef(model, Cell(r, 5), Cell(r, 6))
                    : null;

            var face = Part(1);
            if (face is null)
                continue;

            var hairRows = Enumerable.Range(11, 5).Where(n => Part(n) is not null).ToList();
            var hairs = hairRows.Select(n => Part(n)!).ToList();
            AssetRef? Existing(AssetRef? asset) => asset is { } x && fs.ExistsReferenced(x.Archive, x.File) ? x : null;
            AssetRef? Physics(int n, string extension) => rows.TryGetValue(n, out var r) && AssetRef.FromRow(r, 1) is { } a
                && a.File.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && fs.ExistsReferenced(a.Archive, a.File) ? a : null;
            // Tables of the Bonus Disc list a 7th variant that only exists for some body types.
            var bodies = Enumerable.Range(21, 10).Select(Part).OfType<PartRef>().Select(p => p.Model)
                .TakeWhile(b => fs.ExistsReferenced(b.Archive, b.File)).ToList();
            if (bodies.Count == 0)
                continue;

            // Row 2: face morphs (.xa next to the face .xx). Row 34: the body type's base animation
            // "kiNN"; the other animations of that body type are "dtNN_*" in the same archives.
            AssetRef? Xa(int n) => rows.TryGetValue(n, out var r) && AssetRef.FromRow(r, 1) is { } a
                && a.File.EndsWith(".xa", StringComparison.OrdinalIgnoreCase) ? a : null;
            var idle = Xa(34);

            girls.Add(new GirlInfo(id, skin, AssetRef.FromRow(row, 3), face, hairs, bodies,
                ReadSwimsuits(Table($"lb{id:00}_00_00_00_00.lst"), bodies.Count),
                Xa(2), FindBodyAnimations(fs, idle))
            {
                HairPhysics = hairRows.Select(n => Physics(n + 5, ".kys")).ToList(),
                DetailTexture = details.TryGetValue(id, out var detail) ? detail : null,
                DateActivities = Enumerable.Range(151, 25)
                    .Select(n => rows.TryGetValue(n, out var r) && AssetRef.FromRow(r, 1) is { } anim
                        && anim.File.EndsWith(".xa", StringComparison.OrdinalIgnoreCase) && fs.ExistsReferenced(anim.Archive, anim.File)
                            ? new DateActivity(n - 150, anim, Existing(AssetRef.FromRow(r, 3)), Existing(AssetRef.FromRow(r, 5)))
                            : null)
                    .OfType<DateActivity>().ToList(),
                BodyPhysics = new[] { Physics(31, ".tty"), Physics(32, ".tty") }.OfType<AssetRef>().ToList(),
                OilPoses = Enumerable.Range(81, 4)
                    .Select(n => Existing(Xa(n)) is { } anim ? new OilPose(n - 80, anim) : null)
                    .OfType<OilPose>().ToList(),
            });
        }

        return new CharacterDatabase(tableArchive, girls.OrderBy(g => g.Id).ToList());
    }

    static List<AssetRef> FindBodyAnimations(GameFileSystem fs, AssetRef? idle)
    {
        var list = new List<AssetRef>();
        if (idle is not { } baseAnimation)
            return list;
        list.Add(baseAnimation);
        if (baseAnimation.File.Length < 4)
            return list;
        string bodyType = baseAnimation.File.Substring(2, 2); // "ki01" -> "01"
        foreach (var info in fs.Archives)
            foreach (var entry in fs.GetArchive(info.Name).Entries)
                if (entry.Extension == ".xa" && entry.Name.StartsWith($"dt{bodyType}_", StringComparison.OrdinalIgnoreCase))
                    list.Add(new AssetRef(info.Name, entry.Name));
        return list;
    }

    static List<SwimsuitInfo> ReadSwimsuits(LstTable? table, int bodyCount)
    {
        var list = new List<SwimsuitInfo>();
        if (table is null)
            return list;
        foreach (var row in table.Rows)
        {
            if (!int.TryParse(row[0], out int number) || number < 1 || Cell(row, SwimsuitNameColumn) == "0")
                continue;
            if (AssetRef.FromRow(row, SwimsuitTextureColumn) is not { } texture)
                continue;
            int cut = Math.Clamp(InferCut(Cell(row, SwimsuitGroupColumn)), 1, bodyCount);
            list.Add(new SwimsuitInfo(number, Cell(row, SwimsuitNameColumn), texture,
                AssetRef.FromRow(row, SwimsuitTanColumn), cut, AssetRef.FromRow(row, SwimsuitIconColumn)));
        }
        return list;
    }

    /// <summary>
    /// The tables do not name the body variant directly. Column 8 holds a group code; each group's
    /// textures were matched against every variant's UV layout (delulu cutfit) and checked visually.
    /// Codes that differ per body type (1/4/7, 191/192/193) are the same group.
    /// </summary>
    static int InferCut(string groupCode) => groupCode switch
    {
        "1" or "4" or "7" => 4,              // bikinis (rows 201-212)
        "191" or "192" or "193" => 3,        // strap/line suits (rows 231-235)
        "232" => 5,
        "63" => 6,
        _ => 1,                              // "62": regular swimsuits and costumes
    };

    static string Cell(string[] row, int column) => column < row.Length ? row[column].Trim() : "";

    static bool TryParseId(string fileName, out int id)
    {
        id = 0;
        return fileName.Length >= 4 && int.TryParse(fileName.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }
}
