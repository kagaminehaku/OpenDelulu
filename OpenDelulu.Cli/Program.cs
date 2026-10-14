using System.Diagnostics;
using System.Text;
using OpenDelulu.Engine;
using OpenDelulu.Engine.Assets;
using OpenDelulu.Engine.Characters;
using OpenDelulu.Formats.Character;
using OpenDelulu.Formats.Imaging;
using OpenDelulu.Formats.Lst;
using OpenDelulu.Formats.Pp;
using OpenDelulu.Formats.Save;
using OpenDelulu.Formats.Script;
using OpenDelulu.Formats.Scene;
using OpenDelulu.Formats.Voice;
using OpenDelulu.Formats.Xa;
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
          delulu xa      <gameFolder> <archive.pp> <file.xa>  dump clips, tracks and morphs of an animation
          delulu extract <gameFolder> <archive.pp> <outDir>   extract (and decode .lst to .txt)
          delulu chars   <gameFolder>                         characters, hairstyles, swimsuits (from the .lst tables)
          delulu cutfit  <gameFolder> <girlId>                 diagnostic: swimsuit texture vs body variant UV fit
          delulu verify  <gameFolder> [.ext]                  parse every supported file (or one type), report failures
          delulu sdt     <gameFolder> <archive.pp> <file.sdt> print an event/dialogue script
          delulu saves   <gameFolder> [pictureDir]            parse data/save/*.sav (optionally export character pictures)

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

    case "xa":
        var xa = XaReader.Read(fs.ReadFile(args[2], args[3]));
        Console.WriteLine($"format {xa.Format}, materials {xa.Materials?.Materials.Count ?? 0}, morph sets {xa.Morphs?.IndexSets.Count ?? 0}, " +
                          $"morph keyframes {xa.Morphs?.Keyframes.Count ?? 0}, morph clips {xa.Morphs?.Clips.Count ?? 0}, footer {xa.Footer.Length} bytes");
        if (xa.Morphs is { } morphs)
            foreach (var set in morphs.IndexSets)
                Console.WriteLine($"morph set {set.Name}: {set.MeshIndices.Length} vertices, morph index max {set.MorphIndices.DefaultIfEmpty().Max()}");
        if (xa.Morphs is { } morphs2)
            Console.WriteLine($"morph keyframes: {string.Join(", ", morphs2.Keyframes.Take(12).Select(k => $"{k.Name}({k.Positions.Length})"))} ...");
        if (xa.Morphs is { } morphs3)
            foreach (var clip in morphs3.Clips)
                Console.WriteLine($"morph clip {clip.Name} on {clip.MeshName}: {string.Join(", ", clip.KeyframeRefs.Select(r => r.Name))}");
        if (xa.Animation is { } anim)
        {
            foreach (var (index, clip) in anim.UsedClips())
                Console.WriteLine($"clip #{index,-4} {clip}  next {clip.Next}");
            foreach (var track in anim.Tracks.Take(args.Length > 4 ? int.Parse(args[4]) : 5))
            {
                var k = track.Keyframes.FirstOrDefault();
                Console.WriteLine($"track {track}: first key #{k.Index} R{k.Rotation} T{k.Translation} S{k.Scaling}");
            }
            Console.WriteLine($"{anim.Tracks.Count} tracks, frames {anim.Tracks.Min(t => t.Keyframes.FirstOrDefault().Index)}..{anim.Tracks.Max(t => t.Keyframes.LastOrDefault().Index)}");
        }
        return 0;

    case "xacheck":
    {
        // Compare keyframe 0 of every track with the bind pose of the matching .xx frame.
        var bindModel = fs.ReadModel(args[2], args[3]);
        var clipSet = XaReader.Read(fs.ReadFile(args[4], args[5])).Animation!;
        double worstAsIs = 0, worstConj = 0;
        int matched = 0;
        foreach (var track in clipSet.Tracks)
        {
            if (bindModel.FindFrame(track.Name) is not { } frame || track.Keyframes.Length == 0)
                continue;
            matched++;
            var key = track.Keyframes[0];
            var conj = key with { Rotation = System.Numerics.Quaternion.Conjugate(key.Rotation) };
            worstAsIs = Math.Max(worstAsIs, MaxDiff(key.ToMatrix(), frame.Matrix));
            worstConj = Math.Max(worstConj, MaxDiff(conj.ToMatrix(), frame.Matrix));
        }
        Console.WriteLine($"{matched}/{clipSet.Tracks.Count} tracks matched; max |key0 - bind|: as-is {worstAsIs:G4}, conjugated {worstConj:G4}");
        return 0;

        static double MaxDiff(System.Numerics.Matrix4x4 a, System.Numerics.Matrix4x4 b)
        {
            double m = 0;
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    m = Math.Max(m, Math.Abs(a[r, c] - b[r, c]));
            return m;
        }
    }

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
            var character = assembler.Build(new CharacterSpec(girl.Id));
            var built = character.Model;
            Console.WriteLine($"    built: {built.Meshes.Count(m => m.Visible)}/{built.Meshes.Count} meshes visible, {built.Textures.Count(t => t is null)} missing textures");
            var body = character.Animator.Body;
            Console.WriteLine($"    animation: {girl.BodyAnimations.Count} body .xa, base {body?.Name} ({body?.BoundTracks} tracks bound, {body?.Clips.Count} clips), " +
                              $"face morph targets {character.Animator.Face?.TargetCount}, expressions {character.Animator.Face?.Expressions.Count}");
            var watch = Stopwatch.StartNew();
            for (int f = 0; f < 30; f++)
                character.Animator.Update(1 / 30.0, i => built.Meshes[i].Visible);
            Console.WriteLine($"    skinning: {watch.Elapsed.TotalMilliseconds / 30:F2} ms/frame");
        }
        return 0;

    case "cutfit":
        return CutFit(fs, int.Parse(args[2]));

    case "verify":
        return Verify(fs, args.Length > 2 ? args[2] : null);

    case "sdt":
        var script = SdtFile.Read(fs.ReadFile(args[2], args[3]));
        Console.WriteLine($"{script.Name} v{script.Version}, {script.Scenes.Count} scene(s)");
        foreach (var scene in script.Scenes)
        {
            Console.WriteLine($"scene {scene.Id} ({scene.Value}) labels: {string.Join(", ", scene.Labels.Select(l => $"{l.Value}:{l.Text}"))}");
            PrintCommands(scene.Commands, "  ");
        }
        return 0;

    case "saves":
        return Saves(fs, args.Length > 2 ? args[2] : null);

    default:
        Console.Error.WriteLine($"Unknown command {args[0]}");
        return 1;
}

static void PrintCommands(IEnumerable<SdtCommand> commands, string indent)
{
    foreach (var command in commands)
    {
        Console.WriteLine(indent + command);
        foreach (var row in command.Rows)
            Console.WriteLine($"{indent}    | {string.Join(", ", row)}");
        for (int i = 0; i < command.Blocks.Count; i++)
        {
            Console.WriteLine($"{indent}  block {i}:");
            PrintCommands(command.Blocks[i], indent + "    ");
        }
    }
}

// Parses every file in <edition>/data/save; optionally exports the character pictures as PNG.
static int Saves(GameFileSystem fs, string? pictureDir)
{
    int failed = 0;
    foreach (var edition in fs.Install.Editions)
    {
        string dir = $"{edition.DataFolder}/save";
        if (!fs.Install.Storage.DirectoryExists(dir))
            continue;
        foreach (var name in fs.Install.Storage.EnumerateFiles(dir, "*.sav").Order(StringComparer.OrdinalIgnoreCase))
        {
            using var stream = fs.Install.Storage.OpenRead($"{dir}/{name}");
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            try
            {
                var save = SaveFile.Read(data, name);
                string detail = save.Kind == SaveKind.Personal
                    ? $"history {save.History.Count(h => h.Time is not null)} dated ({string.Join(", ", save.History.Where(h => h.Time is not null).Select(h => h.Time!.Value.ToString("yyyy-MM-dd HH:mm")))}), " +
                      $"pictures {string.Join(" ", save.Pictures.Select((_, i) => save.DecodePicture(i) is var img ? $"{img.Width}x{img.Height}" : ""))}" +
                      (save.Plus ? $", plus extension {save.PlusExtension.Count / 2} entries" : "")
                    : string.Join(" ", save.Fields.Take(12));
                Console.WriteLine($"{edition.Edition,-8} {name}: {save.Kind}{(save.Plus ? " (Plus)" : "")}, {data.Length} bytes: {detail}");
                if (pictureDir is not null && save.Kind == SaveKind.Personal)
                {
                    Directory.CreateDirectory(pictureDir);
                    for (int i = 0; i < save.Pictures.Count; i++)
                        File.WriteAllBytes(Path.Combine(pictureDir, $"{edition.Edition}_{Path.GetFileNameWithoutExtension(name)}_{i}.png"), PngWriter.Encode(save.DecodePicture(i)));
                }
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException)
            {
                failed++;
                Console.WriteLine($"{edition.Edition,-8} {name}: FAILED {e.Message}");
            }
        }
    }
    return failed == 0 ? 0 : 1;
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

static int Verify(GameFileSystem fs, string? only = null)
{
    var watch = Stopwatch.StartNew();
    var counts = new Dictionary<string, (int Ok, int Failed)>();
    var failures = new List<string>();

    // Extension -> parser. A parser throws on malformed data and may return warnings.
    var checks = new Dictionary<string, Func<byte[], string, string?>>(StringComparer.OrdinalIgnoreCase)
    {
        [".lst"] = (d, n) => GameTables.Check(LstTable.Decode(d, n)) is { } summary && Environment.GetEnvironmentVariable("DELULU_VERBOSE") is not null ? $"info: {summary}" : null,
        [".xa"] = (d, _) => XaReader.Read(d).Footer.Length is > 0 and var extra ? $"{extra} unparsed trailing bytes" : null,
        [".xx"] = (d, n) =>
        {
            var render = RenderModel.FromXx(XxReader.Read(d), n);
            int missing = render.Textures.Count(t => t is null);
            return missing > 0 ? $"{missing} embedded texture(s) could not be decoded" : null;
        },
        [".vss"] = (d, _) => { VssFile.Read(d); return null; },
        [".xl"] = (d, _) => { XlFile.Read(d); return null; },
        [".kys"] = (d, _) => { KysFile.Read(d); return null; },
        [".eyes"] = (d, _) => LookSummary(LookFile.Read(d)),
        [".neck"] = (d, _) => LookSummary(LookFile.Read(d)),
        [".ptcl"] = (d, _) => Environment.GetEnvironmentVariable("DELULU_VERBOSE") is null ? Ignore(PtclFile.Read(d))
            : PtclFile.Read(d) is var pt ? $"info: {pt.Emitters.Count} emitters: {string.Join(", ", pt.Emitters.Take(4).Select(e => $"{e.Model.Textures.FirstOrDefault()?.Name}@{e.AttachFrame} keys {e.Curve17.Count}/{e.Curve7.Count}"))}" : null,
        [".glare"] = (d, _) => { GlareFile.Read(d); return null; },
        [".sdt"] = (d, _) => Environment.GetEnvironmentVariable("DELULU_VERBOSE") is null ? Ignore(SdtFile.Read(d))
            : SdtFile.Read(d) is var sdt ? $"info: {sdt.Name} {sdt.Scenes.Count} scenes, ops {string.Join(" ", sdt.AllCommands().GroupBy(c => c.Op).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}"))}" : null,
        [".xm"] = (d, _) =>
        {
            var morphs = XmFile.Read(d).Morphs;
            int dangling = morphs.Clips.Sum(c => c.KeyframeRefs.Count(k => morphs.FindKeyframe(k.Name) is null));
            return dangling > 0 ? $"{dangling} clip refs name a missing keyframe"
                : Environment.GetEnvironmentVariable("DELULU_VERBOSE") is null ? null
                : $"info: {morphs.IndexSets.Count} sets, {morphs.Keyframes.Count} keyframes, clips {string.Join(", ", morphs.Clips.Take(3).Select(c => $"{c.MeshName}/{c.Name}[{c.KeyframeRefs.Count}] frames {c.KeyframeRefs.First().Index}..{c.KeyframeRefs.Last().Index}"))}";
        },
        [".tty"] = (d, _) => Environment.GetEnvironmentVariable("DELULU_VERBOSE") is null ? Ignore(TtyFile.Read(d))
            : TtyFile.Read(d) is var t ? $"info: header {Convert.ToHexString(t.Header)} joints {string.Join("/", t.Joints)}; sets {string.Join(", ", t.Sets.Select(x => $"{x.Name}[{x.Entries.Count}] g={x.Float(10):F3}"))}" : null,
    };
    foreach (var image in ImageDecoder.Extensions)
        checks[image] = (d, n) => { ImageDecoder.Decode(d, n); return null; };

    foreach (var info in fs.Archives)
    {
        PpArchive archive = fs.GetArchive(info.Name);
        foreach (var entry in archive.Entries)
        {
            string what = $"{info.Name}/{entry.Name}";
            string ext = entry.Extension;
            if (!checks.TryGetValue(ext, out var check) || (only is not null && !ext.Equals(only, StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                if (check(archive.ReadAllBytes(entry), entry.Name) is { } warning)
                    failures.Add($"{what}: {warning}");
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

    static string? Ignore(object _) => null;

    // Informational line (shown with the warnings) so the decoded names can be eyeballed.
    static string? LookSummary(LookFile look) =>
        Environment.GetEnvironmentVariable("DELULU_VERBOSE") is null ? null
            : $"info: frames {string.Join("/", look.Frames)}; {string.Join(", ", look.Behaviors.Select(b => $"{b.Name}({b.MaxYaw:F3},{b.MaxPitch:F3})"))}";

    void Count(string kind, bool ok, string what, Exception? ex = null)
    {
        counts.TryGetValue(kind, out var c);
        counts[kind] = ok ? (c.Ok + 1, c.Failed) : (c.Ok, c.Failed + 1);
        if (!ok)
            failures.Add($"{what}: {ex?.GetType().Name}: {ex?.Message}");
    }
}
