using System;
using System.Collections.Generic;
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

        var modelPaths = Directory.EnumerateFiles(inDir.FullName, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".scn", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith(".axo", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var errors = new List<string>();
        foreach (var modelPath in modelPaths)
        {
            try
            {
                var data = File.ReadAllBytes(modelPath);
                if (data.Length < 4) continue;

                var isAxo = modelPath.EndsWith(".axo", StringComparison.OrdinalIgnoreCase);
                var magic = System.Text.Encoding.ASCII.GetString(data, 0, 4);
                if (!isAxo && magic != "SCN0" && magic != "SCN1") continue;

                var relative = Path.GetRelativePath(inDir.FullName, modelPath);
                var relativeDir = Path.GetDirectoryName(relative) ?? "";
                var stem = Path.GetFileNameWithoutExtension(modelPath);

                var outStemDir = Path.Combine(
                    outDir.FullName,
                    isAxo ? "axo" : magic.ToLowerInvariant(),
                    relativeDir,
                    stem);

                if (Directory.Exists(outStemDir))
                    Directory.Delete(outStemDir, recursive: true);
                Directory.CreateDirectory(outStemDir);

                var models = ModelLoader.Load(modelPath, data).Models;

                if (models.Count == 0) continue;

                var srcFolder = Path.GetDirectoryName(modelPath) ?? ".";

                if (isAxo)
                {
                    // AXO export layout:
                    // out/axo/<relativeDir>/<axoStem>/<axoStem>.obj + <axoStem>.mtl + textures
                    // Keep meshes as separate OBJ objects, but write them into a single OBJ file.
                    foreach (var model in models)
                        TexturePipeline.RewriteTexturesToPng(srcFolder, outStemDir, model.Mesh);

                    // If some referenced textures weren't copied (missing source .agi.png), write a small report
                    // so it's obvious why external viewers show black.
                    var missing = FindMissingTextures(outStemDir, models);
                    if (missing.Count > 0)
                        File.WriteAllLines(Path.Combine(outStemDir, "_missing_textures.txt"), missing);

                    ObjWriter.Write(outStemDir, stem, models);
                    continue;
                }

                // SCN export layout:
                // out/<scn0|scn1>/<relativeDir>/<scnStem>/<modelName>/*.obj + textures
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var model in models)
                {
                    var folderName = SanitizePathSegment(model.Name);
                    if (!usedNames.Add(folderName))
                    {
                        var n = 2;
                        while (!usedNames.Add(folderName + "_" + n)) n++;
                        folderName = folderName + "_" + n;
                    }

                    var modelDir = Path.Combine(outStemDir, folderName);
                    Directory.CreateDirectory(modelDir);

                    TexturePipeline.RewriteTexturesToPng(srcFolder, modelDir, model.Mesh);
                    ObjWriter.Write(modelDir, model.Name, model.Mesh);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(modelPath)}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
            throw new Exception("Some files failed:\n" + string.Join("\n", errors.Take(25)) + (errors.Count > 25 ? "\n..." : ""));
    }

    private static string SanitizePathSegment(string name)
    {
        var s = new string(name.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray());
        if (string.IsNullOrWhiteSpace(s)) s = "model";
        return s;
    }

    private static List<string> FindMissingTextures(string outDir, IReadOnlyList<ScnModel> models)
    {
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void CheckOne(HashSet<string> missing, string outDir, string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var p = Path.Combine(outDir, Path.GetFileName(name));
            if (!File.Exists(p))
                missing.Add(Path.GetFileName(name));
        }

        foreach (var m in models)
        {
            foreach (var ms in m.Mesh.MaterialSets.Values)
            {
                CheckOne(missing, outDir, ms.ColorMap);
                CheckOne(missing, outDir, ms.NormalMap);
                CheckOne(missing, outDir, ms.LuminosityMap);
                CheckOne(missing, outDir, ms.ReflectionMap);
            }
        }

        return missing.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
