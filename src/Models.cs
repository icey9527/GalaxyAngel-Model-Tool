using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace ScnViewer;

sealed class ScnMesh
{
    public Vector3[] Positions { get; init; } = Array.Empty<Vector3>();
    public Vector3[] Normals { get; init; } = Array.Empty<Vector3>();
    public Vector2[] UVs { get; init; } = Array.Empty<Vector2>();
    public uint[] Indices { get; init; } = Array.Empty<uint>();
    public List<ScnSubset> Subsets { get; set; } = new();
    public Dictionary<int, ScnMaterialSet> MaterialSets { get; set; } = new();
}

readonly record struct ScnSubset(int MaterialId, int StartTri, int TriCount, int BaseVertex, int VertexCount);

sealed class ScnMaterialSet
{
    public string? ColorMap { get; set; }
    public string? NormalMap { get; set; }
    public string? LuminosityMap { get; set; }
    public string? ReflectionMap { get; set; }
}

sealed record ScnModel(string Name, ScnMesh Mesh, int ContainerIndex = -1, int EmbeddedIndex = 0, int GroupIndex = -1);

// Viewer-only metadata for inspection.
sealed record ScnTreeNodeInfo(string Name, int StartOffset, byte[] Blob40, int HasChild, int HasSibling, List<ScnTreeNodeInfo> Children);

sealed record ScnContainerInfo(int Index, string Name);

sealed record ScnGroupEntry(int GroupIndex, int ContainerIndex, string Name);

sealed record ScnTreeSelection(int ModelIndex);
