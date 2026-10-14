using System.Diagnostics;
using System.Text;
using OpenDelulu.Engine;
using OpenDelulu.Engine.Assets;
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
          delulu extract <gameFolder> <archive.pp> <outDir>   extract (and decode .lst to .txt)
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
                ? $"  mesh: {m.Submeshes.Count} submeshes, {m.Submeshes.Sum(s => s.Vertices.Length)} verts, {m.Bones.Count} bones"
                : "";
            Console.WriteLine($"{new string(' ', depth * 2)}{frame.Name}{mesh}");
        }
        foreach (var mat in model.Materials)
            Console.WriteLine($"material {mat.Name}: {string.Join(", ", mat.Textures.Select(t => t.Name).Where(n => n.Length > 0))}");
        foreach (var t in model.Textures)
            Console.WriteLine($"texture {t}");
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

    case "verify":
        return Verify(fs);

    default:
        Console.Error.WriteLine($"Unknown command {args[0]}");
        return 1;
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
