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

        var scnPaths = Directory.EnumerateFiles(inDir.FullName, "*.scn", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var errors = new List<string>();
        foreach (var scnPath in scnPaths)
        {
            try
            {
                var data = File.ReadAllBytes(scnPath);
                if (data.Length < 4) continue;
                var magic = System.Text.Encoding.ASCII.GetString(data, 0, 4);
                if (magic != "SCN0" && magic != "SCN1") continue;

                var relative = Path.GetRelativePath(inDir.FullName, scnPath);
                var relativeDir = Path.GetDirectoryName(relative) ?? "";
                var scnStem = Path.GetFileNameWithoutExtension(scnPath);

                // Output layout:
                // out/<scn0|scn1>/<relativeDir>/<scnFileStem>/<modelName>/*.obj + textures
                var scnDir = Path.Combine(
                    outDir.FullName,
                    magic.ToLowerInvariant(),
                    relativeDir,
                    scnStem);

                if (Directory.Exists(scnDir))
                    Directory.Delete(scnDir, recursive: true);
                Directory.CreateDirectory(scnDir);

                var models = magic == "SCN1"
                    ? ScnParser.ParseScn1All(scnPath, data)
                    : ScnParser.ParseScn0All(scnPath, data);

                if (models.Count == 0) continue;

                var srcFolder = Path.GetDirectoryName(scnPath) ?? ".";
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

                    var modelDir = Path.Combine(scnDir, folderName);
                    Directory.CreateDirectory(modelDir);

                    TexturePipeline.RewriteTexturesToPng(srcFolder, modelDir, model.Mesh);
                    ObjWriter.Write(modelDir, model.Name, model.Mesh);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(scnPath)}: {ex.Message}");
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
}
