using System;
using System.IO;
using System.Windows.Forms;

namespace ScnViewer;

sealed class ExportDialog : Form
{
    private static string? s_lastInput;
    private static string? s_lastOutput;

    private readonly TextBox _input = new() { Dock = DockStyle.Fill };
    private readonly Button _browseInput = new() { Text = "Browse...", Dock = DockStyle.Right, Width = 110 };

    private readonly TextBox _output = new() { Dock = DockStyle.Fill };
    private readonly Button _browseOutput = new() { Text = "Browse...", Dock = DockStyle.Right, Width = 110 };

    private readonly Button _export = new() { Text = "Export", Width = 90 };

    public string InputFolder => _input.Text.Trim();
    public string OutputFolder => _output.Text.Trim();

    public event EventHandler? ExportRequested;

    public ExportDialog(string? initialInputFolder, string? initialOutputFolder)
    {
        Text = "Export";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new System.Drawing.Size(560, 156);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10),
        };
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(table);

        table.Controls.Add(MakeLabeledRow("Input", _input, _browseInput), 0, 0);
        table.Controls.Add(MakeLabeledRow("Output", _output, _browseOutput), 0, 1);

        var rowButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0),
        };
        rowButtons.Controls.Add(_export);
        table.Controls.Add(rowButtons, 0, 2);

        AcceptButton = _export;

        var appDir = AppContext.BaseDirectory;
        var defaultIn = Path.Combine(appDir, "in");
        var defaultOut = Path.Combine(appDir, "out");

        _input.Text =
            !string.IsNullOrWhiteSpace(initialInputFolder) ? initialInputFolder :
            !string.IsNullOrWhiteSpace(s_lastInput) ? s_lastInput :
            defaultIn;

        _output.Text =
            !string.IsNullOrWhiteSpace(initialOutputFolder) ? initialOutputFolder :
            !string.IsNullOrWhiteSpace(s_lastOutput) ? s_lastOutput :
            defaultOut;

        _browseInput.Click += (_, _) => BrowseFolder(_input, "Select input folder (contains .scn/.axo)");
        _browseOutput.Click += (_, _) => BrowseFolder(_output, "Select output folder");

        _export.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(InputFolder) || !Directory.Exists(InputFolder))
            {
                MessageBox.Show(this, "Please select a valid input folder.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var outDir = OutputFolder;
            if (string.IsNullOrWhiteSpace(outDir))
            {
                MessageBox.Show(this, "Please enter an output folder.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            s_lastInput = InputFolder;
            s_lastOutput = OutputFolder;
            ExportRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public void SetSuggestedInputFolder(string? folder)
    {
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            _input.Text = folder;
    }

    public void SetSuggestedOutputFolder(string? folder)
    {
        if (!string.IsNullOrWhiteSpace(folder))
            _output.Text = folder;
    }

    public void SetBusy(bool busy)
    {
        _export.Enabled = !busy;
        _browseInput.Enabled = !busy;
        _browseOutput.Enabled = !busy;
        _input.ReadOnly = busy;
        _output.ReadOnly = busy;
    }

    private void BrowseFolder(TextBox box, string desc)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = desc,
            SelectedPath = Directory.Exists(box.Text) ? box.Text : AppContext.BaseDirectory,
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            box.Text = dlg.SelectedPath;
    }

    private static Control MakeLabeledRow(string label, Control main, Control? right = null)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, right == null ? 0 : 110));

        var lbl = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
        };
        panel.Controls.Add(lbl, 0, 0);
        panel.Controls.Add(main, 1, 0);
        if (right != null) panel.Controls.Add(right, 2, 0);
        return panel;
    }
}
