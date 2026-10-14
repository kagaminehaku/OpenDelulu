using System.Globalization;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Formats.Lst;

namespace OpenDelulu.Engine.Stages;

/// <summary>Time of day of a stage row (ld02_01 rows 1..5 of each stage block).</summary>
public enum StageTime
{
    Morning = 1,
    Noon = 2,
    Evening = 3,
    Night = 4,
    LateNight = 5,
}

/// <summary>One stage at one time of day: the files of an ld02_01 row.</summary>
/// <param name="Positions">moNN.xx: standing points (o01_N_Point_Date01.., _Oil.., _H..) and clickable spots (o01_A_*).</param>
/// <param name="Extra">miNN.xx (Bonus Disc stage 9 only), drawn with the stage.</param>
public sealed record StageVariant(
    int Stage,
    StageTime Time,
    AssetRef Model,
    AssetRef? Animation,
    AssetRef? Lights,
    AssetRef? Particles,
    AssetRef? Glare,
    AssetRef? Positions,
    AssetRef? Extra);

public sealed record StageInfo(int Id, IReadOnlyList<StageVariant> Variants)
{
    public StageVariant? At(StageTime time) => Variants.FirstOrDefault(v => v.Time == time) ?? Variants.FirstOrDefault();
}

/// <summary>
/// Stages from ld02_01 (loaded by the exe's map init, "%sld%02d_%02d_00_00_00.lst"): ten rows per stage NN
/// (rows 10(NN-1)+1 .. +5 used), each with model/animation/lights/particles/glare/positions as (archive, file)
/// pairs, then the names of the point groups.
/// </summary>
public sealed class StageDatabase
{
    public IReadOnlyList<StageInfo> Stages { get; }

    StageDatabase(IReadOnlyList<StageInfo> stages) => Stages = stages;

    public StageInfo? Find(int id) => Stages.FirstOrDefault(s => s.Id == id);

    public static StageDatabase Load(GameFileSystem fs)
    {
        string archiveName = CharacterDatabase.TableArchives.FirstOrDefault(fs.HasArchive)
            ?? throw new FileNotFoundException("No table archive is installed.");
        var archive = fs.GetArchive(archiveName);
        var entry = archive.Find("ld02_01_00_00_00.lst") ?? throw new InvalidDataException($"ld02_01 missing in {archiveName}.");
        var table = LstTable.Decode(archive.ReadAllBytes(entry), entry.Name);

        var variants = new List<StageVariant>();
        foreach (var row in table.Rows)
        {
            if (!int.TryParse(row[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) || number < 1)
                continue;
            int stage = (number - 1) / 10 + 1, slot = (number - 1) % 10 + 1;
            if (slot > 5 || AssetRef.FromRow(row, 1) is not { } model)
                continue;
            // The Bonus Disc's stage 9 lists files of its own archive; keep only what is installed.
            AssetRef? Optional(int column) => AssetRef.FromRow(row, column) is { } r && fs.ExistsReferenced(r.Archive, r.File) ? r : null;
            if (!fs.ExistsReferenced(model.Archive, model.File))
                continue;
            variants.Add(new StageVariant(stage, (StageTime)slot, model, Optional(3), Optional(5), Optional(7), Optional(9), Optional(11), Optional(17)));
        }

        var stages = variants.GroupBy(v => v.Stage).OrderBy(g => g.Key)
            .Select(g => new StageInfo(g.Key, g.OrderBy(v => v.Time).ToList()))
            .ToList();
        return new StageDatabase(stages);
    }
}
