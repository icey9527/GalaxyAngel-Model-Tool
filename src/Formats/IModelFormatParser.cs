using System.Collections.Generic;

namespace ScnViewer;

interface IModelFormatParser
{
    string Name { get; }
    bool CanParse(string path, byte[] data, out string magic);
    ModelLoader.LoadResult Load(string path, byte[] data, string magic);
}

