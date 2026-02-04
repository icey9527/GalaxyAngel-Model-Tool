using System;
using System.Collections.Generic;
using System.Text;

namespace ScnViewer;

static class ModelLoader
{
    public sealed record LoadResult(
        string Magic,
        List<ScnModel> Models,
        ScnParser.Scn1Index? Scn1Index,
        ScnParser.Scn0Index? Scn0Index);

    private static readonly IModelFormatParser[] _parsers =
    [
        new AxoFormatParser(),
        new Scn1FormatParser(),
        new Scn0FormatParser(),
    ];

    public static LoadResult Load(string path, byte[] data)
    {
        var magic = data.Length >= 4 ? Encoding.ASCII.GetString(data, 0, 4) : "";
        foreach (var p in _parsers)
        {
            if (!p.CanParse(path, data, out var detected)) continue;
            return p.Load(path, data, detected);
        }
        return new LoadResult(magic, new List<ScnModel>(), null, null);
    }
}
