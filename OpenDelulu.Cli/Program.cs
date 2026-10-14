using System.Diagnostics;
using System.Text;
using OpenDelulu.Engine;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Formats.Imaging;
using OpenDelulu.Formats.Lst;
using OpenDelulu.Formats.Pp;
using OpenDelulu.Formats.Xx;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length < 2)
{
    Console.WriteLine("""
        Open Delulu command line tool

          delulu info    <gameFolder>                         installed editions and archives
          delulu ls      <gameFolder> <archive.pp>            list files in an archive
          delulu cat     <gameFolder> <archive.pp> <file>     print a decoded .lst table
          delulu xx      <gameFolder> <archive.pp> <file.xx>  dump the frame tree of a model
          delulu png     <gameFolder> <archive.pp> <image> <out.png>  export a texture as PNG
          delulu extract <gameFolder> <archive.pp> <outDir>   extract (and decode .lst to .txt)
          delulu chars   <gameFolder>                         characters, hairstyles, swimsuits (from the .lst tables)
          delulu cutfit  <gameFolder> <girlId>                 diagnostic: swimsuit texture vs body variant UV fit
          delulu verify  <gameFolder>                         parse every .lst/.xx/image, report failures

        <gameFolder> is the folder that contains SexyBeach3 (and optionally SexyBeach3Plus / SexyBeach3PlusG).
        """);
    return 1;
}

using var fs = new GameFileSystem(GameInstall.Detect(args[1]));

switch (args[0])
{
    case "info":
        foreach (var e in fs.Install.Editions)
            Console.WriteLine($"{e.Edition,-8} {fs.Install.Storage.Description}/{e.DataFolder}");
        Console.WriteLine();
        foreach (var a in fs.Archives)
            Console.WriteLine($"{a.Edition,-8} {a.Name,-28} {a.Size / 1048576.0,8:F1} MB  {fs.GetArchive(a.Name).Entries.Count,5} files");
        foreach (var a in fs.SkippedFiles)
            Console.WriteLine($"(skipped, not an SB3 archive) {a.Path}");
        return 0;

    case "ls":
        foreach (var entry in fs.GetArchive(args[2]).Entries)
            Console.WriteLine($"{entry.Name,-32} {entry.Size,10}");
        return 0;

    case "cat":
        Console.WriteLine(fs.ReadTable(args[2], args[3]).Text);
        return 0;

    case "xx":
        var model = fs.ReadModel(args[2], args[3]);
        Console.WriteLine($"format {model.Format}, {model.Materials.Count} materials, {model.Textures.Count} textures");
        foreach (var frame in model.EnumerateFrames())
        {
            int depth = 0;
            for (var p = frame.Parent; p is not null; p = p.Parent) depth++;
            string mesh = frame.Mesh is { } m
                ? $"  mesh: {m.Submeshes.Count} submeshes, {m.Submeshes.Sum(s => s.Vertices.Length)} verts, {m.Bones.Count} bones" +
                  $" [{string.Join(", ", m.Submeshes.Select(s => s.MaterialIndex >= 0 && s.MaterialIndex < model.Materials.Count ? model.Materials[s.MaterialIndex].Name : "?"))}]"
                : "";
            Console.WriteLine($"{new string(' ', depth * 2)}{frame.Name}{mesh}");
            if (args.Length > 4 && frame.Name.Contains(args[4], StringComparison.Ordinal))
                Console.WriteLine($"{new string(' ', depth * 2)}  local {frame.Matrix}\n{new string(' ', depth * 2)}  world {frame.WorldMatrix}");
        }
        foreach (var mat in model.Materials)
            Console.WriteLine($"material {mat.Name}: {string.Join(", ", mat.Textures.Select(t => t.Name).Where(n => n.Length > 0))}");
        foreach (var t in model.Textures)
            Console.WriteLine($"texture {t}");
        return 0;

    case "png":
        var image = ImageDecoder.Decode(fs.ReadFile(args[2], args[3]), args[3]);
        File.WriteAllBytes(args[4], PngWriter.Encode(image));
        Console.WriteLine($"{image.Width}x{image.Height} -> {args[4]}");
        return 0;

    case "extract":
        var archive = fs.GetArchive(args[2]);
        Directory.CreateDirectory(args[3]);
        foreach (var entry in archive.Entries)
        {
            var data = archive.ReadAllBytes(entry);
            if (entry.Extension == ".lst")
                File.WriteAllText(Path.Combine(args[3], entry.Name + ".txt"), LstTable.Decode(data, entry.Name).Text, Encoding.UTF8);
            else
                File.WriteAllBytes(Path.Combine(args[3], entry.Name), data);
        }
        Console.WriteLine($"Extracted {archive.Entries.Count} files to {args[3]}");
        return 0;

    case "chars":
        var db = CharacterDatabase.Load(fs);
        Console.WriteLine($"tables: {db.TableArchive}");
        var assembler = new CharacterAssembler(fs, db);
        foreach (var girl in db.Girls)
        {
            Console.WriteLine($"{girl.DisplayName}: skin {girl.SkinTexture}, face {girl.Face.Model}, {girl.Hairs.Count} hair, {girl.Bodies.Count} body variants, {girl.Swimsuits.Count} swimsuits");
            foreach (var suit in girl.Swimsuits.Take(args.Length > 2 ? int.Parse(args[2]) : 3))
                Console.WriteLine($"    #{suit.Row,-3} cut {suit.Cut}  {suit.Name}  {suit.Texture}");
            var built = assembler.Build(new CharacterSpec(girl.Id));
            Console.WriteLine($"    built: {built.Meshes.Count(m => m.Visible)}/{built.Meshes.Count} meshes visible, {built.Textures.Count(t => t is null)} missing textures");
        }
        return 0;

    case "cutfit":
        return CutFit(fs, int.Parse(args[2]));

    case "verify":
        return Verify(fs);

    default:
        Console.Error.WriteLine($"Unknown command {args[0]}");
        return 1;
}

// Diagnostic: which body variant's swimsuit UVs best match each swimsuit texture's opaque area (F1 score).
static int CutFit(GameFileSystem fs, int girlId)
{
    const int N = 128;
    var db = CharacterDatabase.Load(fs);
    var girl = db.FindGirl(girlId)!;
    var masks = girl.Bodies.Select(b =>
    {
        var model = XxReader.Read(fs.ReadReferenced(b.Archive, b.File));
        var mask = new bool[N * N];
        foreach (var frame in model.EnumerateFrames())
            if (frame.Name.StartsWith("o01_P_Bura01") || frame.Name.StartsWith("o01_P_Pantu01") || frame.Name.StartsWith("o01_P_Ribon"))
                foreach (var sub in frame.Mesh?.Submeshes ?? [])
                    for (int i = 0; i + 2 < sub.Indices.Length; i += 3)
                        Raster(mask, sub.Vertices[sub.Indices[i]].UV, sub.Vertices[sub.Indices[i + 1]].UV, sub.Vertices[sub.Indices[i + 2]].UV);
        return mask;
    }).ToList();
    var table = LstTable.Decode(fs.ReadFile(db.TableArchive, $"lb{girlId:00}_00_00_00_00.lst"));
    foreach (var suit in girl.Swimsuits)
    {
        var image = ImageDecoder.Decode(fs.ReadReferenced(suit.Texture.Archive, suit.Texture.File), suit.Texture.File);
        var opaque = new bool[N * N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
                opaque[y * N + x] = image.Pixels[((y * image.Height / N) * image.Width + x * image.Width / N) * 4 + 3] > 128;
        var scores = masks.Select(mask =>
        {
            int tp = 0, fp = 0, fn = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] && opaque[i]) tp++;
                else if (mask[i]) fp++;
                else if (opaque[i]) fn++;
            }
            return tp == 0 ? 0 : 2.0 * tp / (2 * tp + fp + fn);
        }).ToList();
        int best = scores.IndexOf(scores.Max()) + 1;
        var row = table.Rows.First(r => r[0] == suit.Row.ToString());
        Console.WriteLine($"#{suit.Row,-3} best {best} ({string.Join(" ", scores.Select(s => s.ToString("F2")))})  | {string.Join(" ", row.Skip(8).Take(18))} | {string.Join(" ", row.Skip(26).Take(25))} | {string.Join(" ", row.Skip(51).Take(10))}  {suit.Name}");
    }
    return 0;

    static void Raster(bool[] mask, System.Numerics.Vector2 a, System.Numerics.Vector2 b, System.Numerics.Vector2 c)
    {
        a *= N; b *= N; c *= N;
        int x0 = (int)MathF.Max(0, MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), x1 = (int)MathF.Min(N - 1, MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        int y0 = (int)MathF.Max(0, MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), y1 = (int)MathF.Min(N - 1, MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        float Edge(System.Numerics.Vector2 p, System.Numerics.Vector2 q, float x, float y) => (q.X - p.X) * (y - p.Y) - (q.Y - p.Y) * (x - p.X);
        float area = Edge(a, b, c.X, c.Y);
        if (MathF.Abs(area) < 1e-6f) return;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float w0 = Edge(b, c, px, py) / area, w1 = Edge(c, a, px, py) / area, w2 = Edge(a, b, px, py) / area;
                if (w0 >= 0 && w1 >= 0 && w2 >= 0) mask[y * N + x] = true;
            }
    }
}

static int Verify(GameFileSystem fs)
{
    var watch = Stopwatch.StartNew();
    var counts = new Dictionary<string, (int Ok, int Failed)>();
    var failures = new List<string>();

    void Count(string kind, bool ok, string what, Exception? ex = null)
    {
        counts.TryGetValue(kind, out var c);
        counts[kind] = ok ? (c.Ok + 1, c.Failed) : (c.Ok, c.Failed + 1);
        if (!ok)
            failures.Add($"{what}: {ex?.GetType().Name}: {ex?.Message}");
    }

    foreach (var info in fs.Archives)
    {
        PpArchive archive = fs.GetArchive(info.Name);
        foreach (var entry in archive.Entries)
        {
            string what = $"{info.Name}/{entry.Name}";
            string ext = entry.Extension;
            if (ext is not (".lst" or ".xx") && !ImageDecoder.IsImage(entry.Name))
                continue;
            try
            {
                var data = archive.ReadAllBytes(entry);
                switch (ext)
                {
                    case ".lst":
                        LstTable.Decode(data, entry.Name);
                        break;
                    case ".xx":
                        var model = XxReader.Read(data);
                        var render = RenderModel.FromXx(model, entry.Name);
                        int missing = render.Textures.Count(t => t is null);
                        if (missing > 0)
                            failures.Add($"{what}: {missing} embedded texture(s) could not be decoded");
                        break;
                    default:
                        ImageDecoder.Decode(data, entry.Name);
                        break;
                }
                Count(ext, true, what);
            }
            catch (Exception ex)
            {
                Count(ext, false, what, ex);
            }
        }
    }

    foreach (var (kind, (ok, failed)) in counts.OrderBy(k => k.Key))
        Console.WriteLine($"{kind,-6} ok {ok,6}  failed {failed,4}");
    foreach (var failure in failures.Take(50))
        Console.WriteLine("  " + failure);
    if (failures.Count > 50)
        Console.WriteLine($"  ... {failures.Count - 50} more");
    Console.WriteLine($"Done in {watch.Elapsed.TotalSeconds:F1}s");
    return counts.Values.Any(c => c.Failed > 0) ? 2 : 0;
}
