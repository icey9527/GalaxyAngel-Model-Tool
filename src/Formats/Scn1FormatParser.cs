using System;

namespace ScnViewer;

sealed class Scn1FormatParser : IModelFormatParser
{
    public string Name => "SCN1";

    public bool CanParse(string path, byte[] data, out string magic)
    {
        magic = "";
        if (data.Length < 4) return false;
        magic = System.Text.Encoding.ASCII.GetString(data, 0, 4);
        return magic == "SCN1";
    }

    public ModelLoader.LoadResult Load(string path, byte[] data, string magic)
    {
        var idx = ScnParser.ParseScn1Index(data);
        return new ModelLoader.LoadResult(magic, idx.Models, idx, null);
    }
}

