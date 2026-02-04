using System;

namespace ScnViewer;

sealed class Scn0FormatParser : IModelFormatParser
{
    public string Name => "SCN0";

    public bool CanParse(string path, byte[] data, out string magic)
    {
        magic = "";
        if (data.Length < 4) return false;
        magic = System.Text.Encoding.ASCII.GetString(data, 0, 4);
        return magic == "SCN0";
    }

    public ModelLoader.LoadResult Load(string path, byte[] data, string magic)
    {
        var idx = ScnParser.ParseScn0Index(path, data);
        return new ModelLoader.LoadResult(magic, idx.Models, null, idx);
    }
}

