using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenTK.WinForms;

namespace ScnViewer;

sealed class ViewerForm : Form
{
    private readonly MenuStrip _menu = new();
    private readonly ToolStripMenuItem _open = new("Open...");

    private readonly TableLayoutPanel _layout = new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 2,
        RowCount = 1,
    };

    private readonly ListBox _list = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        Width = 280,
    };

    private readonly TreeView _tree = new()
    {
        Dock = DockStyle.Fill,
        HideSelection = false,
        FullRowSelect = true,
        CheckBoxes = true,
    };

    private readonly ListBox _meshes = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
    };

    private readonly TabControl _tabs = new()
    {
        Dock = DockStyle.Fill,
    };

    private readonly TabPage _tabFiles = new("Files");
    private readonly TabPage _tabMeshes = new("Meshes");
    private readonly TabPage _tabTree = new("Tree");

    private readonly GLControl _gl = new(new GLControlSettings
    {
        API = OpenTK.Windowing.Common.ContextAPI.OpenGL,
        APIVersion = new Version(3, 3),
        Profile = OpenTK.Windowing.Common.ContextProfile.Core,
        Flags = OpenTK.Windowing.Common.ContextFlags.ForwardCompatible,
        IsEventDriven = false,
        DepthBits = 24,
        StencilBits = 8,
        NumberOfSamples = 4, // MSAA 4x (anti-aliasing)
    })
    {
        Dock = DockStyle.Fill,
        MinimumSize = new System.Drawing.Size(1024, 768),
    };

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly List<string> _scnFiles = new();
    private string? _currentFolder;
    private Renderer? _renderer;
    private CancellationTokenSource? _loadCts;
    private bool _buildingTree;
    private bool _suppressMeshPick;

    public ViewerForm(string? initialPath)
    {
        Text = "SCN Viewer";
        // 4:3 render area + right file list.
        ClientSize = new System.Drawing.Size(1280, 800);

        _menu.Items.Add(_open);
        MainMenuStrip = _menu;
        Controls.Add(_layout);
        Controls.Add(_menu);

        _layout.ColumnStyles.Clear();
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1024));
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _layout.RowStyles.Clear();
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _layout.Controls.Add(_gl, 0, 0);
        _tabs.TabPages.Add(_tabFiles);
        _tabs.TabPages.Add(_tabMeshes);
        _tabs.TabPages.Add(_tabTree);
        _tabFiles.Controls.Add(_list);
        _tabMeshes.Controls.Add(_meshes);
        _tabTree.Controls.Add(_tree);
        _layout.Controls.Add(_tabs, 1, 0);

        _open.Click += (_, _) => DoOpenFile();
        _list.SelectedIndexChanged += (_, _) => OnPickFile();
        _meshes.SelectedIndexChanged += (_, _) => OnPickMesh();
        _tree.AfterSelect += (_, e) => OnPickTree(e.Node);
        _tree.AfterCheck += (_, e) => OnPickTreeCheck(e.Node);

        _gl.Load += (_, _) =>
        {
            try
            {
                _renderer = new Renderer(_gl);
                _renderer.Initialize();
                // If folder listing was populated before GL was ready, load the currently selected file now.
                if (_list.SelectedIndex >= 0)
                    OnPickFile();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.ToString(), "SCN Viewer - GL init failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        _gl.Resize += (_, _) => _renderer?.Resize(_gl.ClientSize.Width, _gl.ClientSize.Height);
        _gl.Paint += (_, _) => _renderer?.Render();
        _gl.MouseDown += (_, e) => _renderer?.OnMouseDown(e);
        _gl.MouseMove += (_, e) => _renderer?.OnMouseMove(e);
        _gl.MouseWheel += (_, e) => _renderer?.OnMouseWheel(e);

        _timer.Tick += (_, _) => _gl.Invalidate();
        _timer.Start();

        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            if (Directory.Exists(initialPath))
                LoadFolder(initialPath);
            else if (File.Exists(initialPath) && initialPath.EndsWith(".scn", StringComparison.OrdinalIgnoreCase))
                LoadFile(initialPath);
        }
    }

    private void DoOpenFile()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "SCN files (*.scn)|*.scn|All files (*.*)|*.*",
            Title = "Open SCN File",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            LoadFile(dlg.FileName);
    }

    private void LoadFile(string file)
    {
        var folder = Path.GetDirectoryName(file);
        if (folder != null && Directory.Exists(folder))
        {
            LoadFolder(folder);
            var rel = Path.GetRelativePath(folder, file);
            var idx = _list.Items.IndexOf(rel);
            if (idx >= 0) _list.SelectedIndex = idx;
        }
        else
        {
            _list.Items.Clear();
            _list.Items.Add(Path.GetFileName(file));
            _list.SelectedIndex = 0;
            _scnFiles.Clear();
            _scnFiles.Add(file);
            _currentFolder = null;
            _tree.Nodes.Clear();
        }
    }

    private void LoadFolder(string folder)
    {
        _currentFolder = folder;
        _scnFiles.Clear();
        _scnFiles.AddRange(Directory.EnumerateFiles(folder, "*.scn", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in _scnFiles)
            _list.Items.Add(Path.GetRelativePath(folder, p));
        _list.EndUpdate();
        if (_scnFiles.Count > 0) _list.SelectedIndex = 0;
        _tree.Nodes.Clear();
    }

    private readonly List<ScnModel> _currentModels = new();
    private ScnParser.Scn1Index? _currentScn1Index;
    private void OnPickFile()
    {
        if (_renderer == null) return;
        var idx = _list.SelectedIndex;
        if (idx < 0 || idx >= _scnFiles.Count) return;
        var path = _scnFiles[idx];

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        Task.Run(() =>
        {
            var data = File.ReadAllBytes(path);
            var magic = data.Length >= 4 ? System.Text.Encoding.ASCII.GetString(data, 0, 4) : "";
            if (magic == "SCN1")
            {
                var idx = ScnParser.ParseScn1Index(data);
                return (idx.Models, folder: Path.GetDirectoryName(path) ?? ".", file: Path.GetFileName(path), magic, idx);
            }
            if (magic == "SCN0")
            {
                var mesh = ScnParser.ParseScn0High(path, data);
                var models = mesh != null ? new List<ScnModel> { new ScnModel(Path.GetFileNameWithoutExtension(path), mesh) } : new List<ScnModel>();
                return (models, folder: Path.GetDirectoryName(path) ?? ".", file: Path.GetFileName(path), magic, idx: (ScnParser.Scn1Index?)null);
            }
            return (new List<ScnModel>(), folder: Path.GetDirectoryName(path) ?? ".", file: Path.GetFileName(path), magic, idx: (ScnParser.Scn1Index?)null);
        }, token).ContinueWith(t =>
        {
            if (t.IsCanceled || token.IsCancellationRequested) return;
            if (t.IsFaulted)
            {
                var msg = t.Exception?.GetBaseException().Message ?? "Failed to load.";
                BeginInvoke(() => MessageBox.Show(this, msg, "SCN Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error));
                return;
            }
            var (models, folder, file, magic, idx) = t.Result;
            if (models.Count == 0)
            {
                BeginInvoke(() => MessageBox.Show(this, "Failed to parse this SCN file.", "SCN Viewer",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning));
                return;
            }
            BeginInvoke(() =>
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    _currentModels.Clear();
                    _currentModels.AddRange(models);
                    _currentScn1Index = idx;
                    var defaultVisible = ChooseDefaultVisibleModels(idx, _currentModels);
                    _renderer?.LoadScene(_currentModels, folder, defaultVisible);
                    BuildTree(idx, defaultVisible);
                    UpdateTitle(file, magic, defaultVisible);

                    BuildMeshesList();
                    if (_meshes.Items.Count > 0)
                    {
                        _suppressMeshPick = true;
                        try
                        {
                            var first = defaultVisible.Count > 0 ? defaultVisible.Min() : 0;
                            if ((uint)first < (uint)_meshes.Items.Count) _meshes.SelectedIndex = first;
                        }
                        finally
                        {
                            _suppressMeshPick = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.ToString(), "SCN Viewer - load failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        }, CancellationToken.None);
    }

    private void OnPickTree(TreeNode? node)
    {
        if (_renderer == null) return;
        if (_currentModels.Count == 0) return;
        var fi = _list.SelectedIndex;
        if (fi < 0 || fi >= _scnFiles.Count) return;
        var folder = Path.GetDirectoryName(_scnFiles[fi]) ?? ".";
        var file = Path.GetFileName(_scnFiles[fi]);

        try
        {
            // Selection is informational; visibility is controlled by checkboxes.
            if (node?.Tag is not ScnTreeSelection) return;
            var visible = GetVisibleModelsFromTree();
            UpdateTitle(file, _currentScn1Index != null ? "SCN1" : "SCN0", visible);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "SCN Viewer - load failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnPickTreeCheck(TreeNode? node)
    {
        if (_renderer == null) return;
        if (_buildingTree) return;
        if (node == null) return;
        if (_currentModels.Count == 0) return;

        try
        {
            _buildingTree = true;
            SetChildrenChecked(node, node.Checked);
        }
        finally
        {
            _buildingTree = false;
        }

        var visible = GetVisibleModelsFromTree();
        _renderer.SetVisibleModels(visible);

        var fi = _list.SelectedIndex;
        if (fi < 0 || fi >= _scnFiles.Count) return;
        var file = Path.GetFileName(_scnFiles[fi]);
        var magic = _currentScn1Index != null ? "SCN1" : "SCN0";
        UpdateTitle(file, magic, visible);
    }

    private static void SetChildrenChecked(TreeNode node, bool isChecked)
    {
        foreach (TreeNode c in node.Nodes)
        {
            c.Checked = isChecked;
            SetChildrenChecked(c, isChecked);
        }
    }

    private IReadOnlyCollection<int> GetVisibleModelsFromTree()
    {
        var outList = new List<int>();
        foreach (TreeNode n in _tree.Nodes)
            CollectCheckedModels(n, outList);
        return outList;

        static void CollectCheckedModels(TreeNode n, List<int> outList)
        {
            if (n.Checked && n.Tag is ScnTreeSelection sel)
                outList.Add(sel.ModelIndex);
            foreach (TreeNode c in n.Nodes)
                CollectCheckedModels(c, outList);
        }
    }

    private void UpdateTitle(string file, string magic, IReadOnlyCollection<int> visible)
    {
        var v = 0;
        var f = 0;
        foreach (var i in visible)
        {
            if ((uint)i >= (uint)_currentModels.Count) continue;
            v += _currentModels[i].Mesh.Positions.Length;
            f += _currentModels[i].Mesh.Indices.Length / 3;
        }
        Text = $"SCN Viewer - {file} ({magic})  (v={v}, f={f})";
    }

    private static IReadOnlyCollection<int> ChooseDefaultVisibleModels(ScnParser.Scn1Index? idx, List<ScnModel> models)
    {
        if (models.Count == 0) return Array.Empty<int>();
        if (idx == null || idx.Groups.Count == 0)
            return Enumerable.Range(0, models.Count).ToArray();

        // Prefer the group with the largest total face count.
        var bestGroup = -1;
        var bestFaces = -1;
        foreach (var g in idx.Groups.GroupBy(g => g.GroupIndex))
        {
            var containers = g.Select(x => x.ContainerIndex).Distinct().ToHashSet();
            var faces = 0;
            foreach (var (m, mi) in models.Select((m, i) => (m, i)))
            {
                if (!containers.Contains(m.ContainerIndex)) continue;
                faces += m.Mesh.Indices.Length / 3;
            }
            if (faces > bestFaces) { bestFaces = faces; bestGroup = g.Key; }
        }

        if (bestGroup < 0) return Enumerable.Range(0, models.Count).ToArray();

        var bestContainers = idx.Groups.Where(x => x.GroupIndex == bestGroup).Select(x => x.ContainerIndex).Distinct().ToHashSet();
        return models.Select((m, i) => (m, i)).Where(t => bestContainers.Contains(t.m.ContainerIndex)).Select(t => t.i).ToArray();
    }

    private void BuildTree(ScnParser.Scn1Index? idx, IReadOnlyCollection<int> defaultVisible)
    {
        _tree.BeginUpdate();
        _buildingTree = true;
        _tree.Nodes.Clear();
        if (idx == null)
        {
            _buildingTree = false;
            _tree.EndUpdate();
            return;
        }

        // Pre-index the scene tree by name for easy cross-reference.
        var sceneNameIndex = new Dictionary<string, List<ScnTreeNodeInfo>>(StringComparer.OrdinalIgnoreCase);
        if (idx.Tree != null)
            IndexSceneNames(idx.Tree, sceneNameIndex);

        // Groups (LOD/sets) -> containers/entries -> meshes
        var groupsRoot = new TreeNode("Groups (LOD/sets)");
        foreach (var g in idx.Groups.GroupBy(x => x.GroupIndex).OrderBy(g => g.Key))
        {
            var names = g.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(3).ToList();
            var suffix = names.Count > 0 ? $" ({string.Join(", ", names)})" : "";
            var gNode = new TreeNode($"Group {g.Key}{suffix}") { Checked = false };
            foreach (var e in g.OrderBy(x => x.ContainerIndex))
            {
                var cname = idx.Containers.FirstOrDefault(c => c.Index == e.ContainerIndex)?.Name ?? $"container{e.ContainerIndex}";
                var cn = new TreeNode($"{cname}  [{e.Name}]") { Checked = false };

                // Cross-reference: show matching Scene Tree nodes (same name string) if present.
                if (!string.IsNullOrWhiteSpace(e.Name) && sceneNameIndex.TryGetValue(e.Name, out var hits))
                {
                    foreach (var hit in hits.Take(3))
                        cn.Nodes.Add(new TreeNode($"SceneNode: {hit.Name}  @0x{hit.StartOffset:X}") { Checked = false });
                }

                AddContainerModels(cn, e.ContainerIndex, defaultVisible);
                gNode.Nodes.Add(cn);
            }
            groupsRoot.Nodes.Add(gNode);
        }
        _tree.Nodes.Add(groupsRoot);

        if (idx.MeshTable != null)
        {
            var mtRoot = new TreeNode($"Mesh Table (@0x{idx.MeshTable.StartOffset:X})");
            foreach (var g in idx.MeshTable.Groups.OrderBy(g => g.GroupIndex))
            {
                var gNode = new TreeNode($"Group {g.GroupIndex} ({g.Entries.Count})");
                foreach (var e in g.Entries)
                {
                    var eNode = new TreeNode($"{e.Name}  (flag={e.Flag}, bytes={e.Payload.Length})");
                    // Show any loaded models whose name matches this entry.
                    foreach (var (m, mi) in _currentModels.Select((m, i) => (m, i)).Where(t => t.m.GroupIndex == g.GroupIndex))
                    {
                        if (!m.Name.StartsWith(e.Name, StringComparison.OrdinalIgnoreCase)) continue;
                        eNode.Nodes.Add(new TreeNode($"Model: {m.Name}") { Tag = new ScnTreeSelection(mi), Checked = defaultVisible.Contains(mi) });
                    }
                    gNode.Nodes.Add(eNode);
                }
                mtRoot.Nodes.Add(gNode);
            }
            _tree.Nodes.Add(mtRoot);
        }
        else
        {
            var scan = idx.MeshTableScan;
            if (scan != null)
            {
                _tree.Nodes.Add(new TreeNode(
                    $"Mesh Table (not found)  pref=0x{scan.PreferredStart:X} scan=0x{scan.ScanStart:X}..0x{scan.ScanEnd:X}  candidates={scan.Candidates} bestScore={scan.BestScore}"));
            }
        }

        // Raw scene tree (names only, for inspection)
        var sceneRoot = new TreeNode("Scene Tree (nodes)");
        if (idx.Tree != null)
            sceneRoot.Nodes.Add(BuildSceneNode(idx.Tree));
        _tree.Nodes.Add(sceneRoot);

        UpdateParentChecks(_tree.Nodes);
        _tree.ExpandAll();
        _buildingTree = false;
        _tree.EndUpdate();
    }

    private static void IndexSceneNames(ScnTreeNodeInfo node, Dictionary<string, List<ScnTreeNodeInfo>> index)
    {
        if (!string.IsNullOrWhiteSpace(node.Name))
        {
            if (!index.TryGetValue(node.Name, out var list)) index[node.Name] = list = new List<ScnTreeNodeInfo>();
            list.Add(node);
        }
        foreach (var c in node.Children)
            IndexSceneNames(c, index);
    }

    private static void UpdateParentChecks(TreeNodeCollection nodes)
    {
        foreach (TreeNode n in nodes)
            UpdateParentChecksOne(n);

        static bool UpdateParentChecksOne(TreeNode node)
        {
            var anyChecked = node.Tag is ScnTreeSelection ? node.Checked : false;
            foreach (TreeNode c in node.Nodes)
                anyChecked |= UpdateParentChecksOne(c);
            if (node.Tag is not ScnTreeSelection)
                node.Checked = anyChecked;
            return anyChecked;
        }
    }

    private void BuildMeshesList()
    {
        _meshes.BeginUpdate();
        _meshes.Items.Clear();
        for (var i = 0; i < _currentModels.Count; i++)
        {
            var n = _currentModels[i].Name;
            if (string.IsNullOrWhiteSpace(n)) n = $"<no-name>#{i}";
            _meshes.Items.Add(n);
        }
        _meshes.EndUpdate();
    }

    private void OnPickMesh()
    {
        if (_renderer == null) return;
        if (_suppressMeshPick) return;
        var idx = _meshes.SelectedIndex;
        if (idx < 0 || idx >= _currentModels.Count) return;

        var fi = _list.SelectedIndex;
        if (fi < 0 || fi >= _scnFiles.Count) return;
        var folder = Path.GetDirectoryName(_scnFiles[fi]) ?? ".";
        var file = Path.GetFileName(_scnFiles[fi]);

        try
        {
            _renderer.SetVisibleModels(new[] { idx });
            UpdateTreeChecks(new[] { idx });
            UpdateTitle(file, _currentScn1Index != null ? "SCN1" : "SCN0", new[] { idx });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "SCN Viewer - load failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateTreeChecks(IReadOnlyCollection<int> visible)
    {
        if (_buildingTree) return;
        try
        {
            _buildingTree = true;
            var set = visible.ToHashSet();
            foreach (TreeNode n in _tree.Nodes)
                ApplyChecks(n, set);
        }
        finally
        {
            _buildingTree = false;
        }

        static void ApplyChecks(TreeNode n, HashSet<int> visible)
        {
            if (n.Tag is ScnTreeSelection sel)
                n.Checked = visible.Contains(sel.ModelIndex);
            foreach (TreeNode c in n.Nodes)
                ApplyChecks(c, visible);
        }
    }

    private void AddContainerModels(TreeNode containerNode, int containerIndex, IReadOnlyCollection<int> defaultVisible)
    {
        var models = _currentModels
            .Select((m, i) => (m, i))
            .Where(t => t.m.ContainerIndex == containerIndex)
            .ToList();

        foreach (var (m, mi) in models)
        {
            var meshNode = new TreeNode(m.Name)
            {
                Tag = new ScnTreeSelection(mi),
                Checked = defaultVisible.Contains(mi),
            };

            var matsNode = new TreeNode("Materials");
            foreach (var kv in m.Mesh.MaterialSets.OrderBy(kv => kv.Key))
            {
                var ms = kv.Value;
                var cm = ms.ColorMap ?? "";
                var nm = ms.NormalMap ?? "";
                var lm = ms.LuminosityMap ?? "";
                var rm = ms.ReflectionMap ?? "";
                matsNode.Nodes.Add(new TreeNode($"mat {kv.Key}: Color={cm} Normal={nm} Lum={lm} Refl={rm}"));
            }
            meshNode.Nodes.Add(matsNode);

            var subsetsNode = new TreeNode($"Subsets ({m.Mesh.Subsets.Count})");
            foreach (var s in m.Mesh.Subsets.OrderBy(s => s.StartTri))
                subsetsNode.Nodes.Add(new TreeNode($"mat={s.MaterialId} startTri={s.StartTri} triCount={s.TriCount} baseV={s.BaseVertex} vCnt={s.VertexCount}"));
            meshNode.Nodes.Add(subsetsNode);

            containerNode.Nodes.Add(meshNode);
        }
    }

    private static TreeNode BuildSceneNode(ScnTreeNodeInfo n)
    {
        var name = string.IsNullOrWhiteSpace(n.Name) ? "<no-name>" : n.Name;
        var tn = new TreeNode($"{name}  @0x{n.StartOffset:X}");
        foreach (var c in n.Children)
            tn.Nodes.Add(BuildSceneNode(c));
        return tn;
    }
}
