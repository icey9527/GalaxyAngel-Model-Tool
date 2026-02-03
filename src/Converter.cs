using System;
using System.IO;
using System.Linq;

namespace ScnViewer;

static class Converter
{
    public static void ConvertFolder(string inputDir, string outputDir)
    {
        var inDir = new DirectoryInfo(inputDir);
        var outDir = new DirectoryInfo(outputDir);
        if (!inDir.Exists)
            throw new DirectoryNotFoundException(inDir.FullName);
        outDir.Create();

        var scnPaths = Directory.EnumerateFiles(inDir.FullName, "*.scn", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var scnPath in scnPaths)
        {
            try
            {
                var data = File.ReadAllBytes(scnPath);
                if (data.Length < 4) continue;
                var magic = System.Text.Encoding.ASCII.GetString(data, 0, 4);
                if (magic != "SCN0" && magic != "SCN1") continue;

                var stem = Path.GetFileNameWithoutExtension(scnPath);
                Console.WriteLine(stem);
                var dst = Path.Combine(outDir.FullName, magic.ToLowerInvariant(), stem);
                if (Directory.Exists(dst))
                    Directory.Delete(dst, recursive: true);
                Directory.CreateDirectory(dst);

                var mesh = magic == "SCN1"
                    ? ScnParser.ParseScn1High(scnPath, data)
                    : ScnParser.ParseScn0High(scnPath, data);
                if (mesh == null) continue;
                if (Environment.GetEnvironmentVariable("SCN_DEBUG") == "1")
                {
                    Console.WriteLine($"{magic} {stem}: v={mesh.Positions.Length} idx={mesh.Indices.Length} idx%3={mesh.Indices.Length % 3} subsets={mesh.Subsets.Count} mats={mesh.MaterialSets.Count}");
                    for (var si = 0; si < Math.Min(mesh.Subsets.Count, 8); si++)
                    {
                        var s = mesh.Subsets[si];
                        Console.WriteLine($"  subset[{si}] mat={s.MaterialId} startTri={s.StartTri} triCount={s.TriCount} baseV={s.BaseVertex} vCnt={s.VertexCount}");
                    }
                }

                var srcFolder = Path.GetDirectoryName(scnPath) ?? ".";
                TexturePipeline.RewriteTexturesToPng(srcFolder, dst, mesh);
                ObjWriter.Write(dst, stem, mesh);
            }
            catch (Exception ex)
            {
                if (Environment.GetEnvironmentVariable("SCN_DEBUG") == "1")
                    Console.Error.WriteLine($"[!] Failed: {scnPath}\n{ex}");
                else
                    Console.Error.WriteLine($"[!] Failed: {scnPath} ({ex.Message})");
            }
        }
    }
}
