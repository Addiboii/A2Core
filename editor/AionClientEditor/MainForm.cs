using AionClientEditor;
using AionClientEditor.Editing;
using AionClientEditor.Patch;
using AionClientProbe;

namespace AionClientEditor.App;

sealed class MainForm : Form
{
    readonly ListBox tableList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly TextBox filterBox = new() { Dock = DockStyle.Top, PlaceholderText = "Filter tables…" };
    readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells };
    readonly TextBox detailsBox = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
    };
    readonly Label detailsLabel = new() { Dock = DockStyle.Top, Height = 26, Text = "Cell details", TextAlign = ContentAlignment.MiddleLeft };
    readonly Label tableHeading = new() { Dock = DockStyle.Top, Height = 32, Text = "Tables", TextAlign = ContentAlignment.MiddleLeft };
    readonly Label status = new() { Dock = DockStyle.Bottom, Height = 32, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStrip tool = new();

    EditorWorkspace? workspace;
    TableDocument? current;
    DataTableGridModel? dataTableGrid;
    List<string> allPaths = [];

    public MainForm()
    {
        var version = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        Text = $"A2Core Client Editor {version}";
        Width = 1280;
        Height = 760;
        MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        A2CoreTheme.ApplyForm(this);

        var openPaks = new ToolStripButton("Open Paks…");
        openPaks.Click += (_, _) => OpenWorkspace();
        var save = new ToolStripButton("Save to patch");
        save.Click += (_, _) => SaveCurrent();
        var build = new ToolStripButton("Build patch pak");
        build.Click += (_, _) => BuildPatch();
        var export = new ToolStripButton("Export JSON…");
        export.Click += (_, _) => ExportJson();
        var import = new ToolStripButton("Import JSON…");
        import.Click += (_, _) => ImportJson();
        var revert = new ToolStripButton("Revert patch");
        revert.Click += (_, _) => RevertPatch();
        tool.Items.AddRange([openPaks, save, build, export, import, revert]);
        A2CoreTheme.ApplyToolStrip(tool);

        var brandRail = new BrandRail("A2Core Client Editor", "v" + version);

        var left = new Panel { Dock = DockStyle.Left, Width = 400, Padding = new Padding(12, 8, 8, 8), BackColor = A2CoreTheme.Background };
        left.Controls.Add(tableList);
        left.Controls.Add(filterBox);
        left.Controls.Add(tableHeading);
        tableHeading.BringToFront();
        filterBox.BringToFront();
        A2CoreTheme.ApplyListBox(tableList);
        A2CoreTheme.ApplyTextBox(filterBox);
        A2CoreTheme.ApplyLabel(tableHeading, semibold: true);
        tableHeading.Font = A2CoreTheme.UiFont(12f, semibold: true);

        grid.CellEndEdit += (_, e) => ApplyGridEdit(e.RowIndex, e.ColumnIndex);
        grid.CellBeginEdit += (_, e) =>
        {
            if (current?.Kind != TableKind.DataTable || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (grid.Rows[e.RowIndex].Tag is not DataTableGridRow row || e.ColumnIndex >= row.Cells.Count) return;
            var kind = row.Cells[e.ColumnIndex].EditKind;
            if (kind is PropertyEditKind.ReadOnly or PropertyEditKind.JsonComplex)
                e.Cancel = true;
        };
        grid.SelectionChanged += (_, _) => UpdateDetailsPanel();

        A2CoreTheme.ApplyDataGridView(grid);

        var detailsPanel = new Panel { Dock = DockStyle.Bottom, Height = 180, Padding = new Padding(0, 8, 0, 0), BackColor = A2CoreTheme.Background };
        detailsPanel.Controls.Add(detailsBox);
        detailsPanel.Controls.Add(detailsLabel);
        A2CoreTheme.ApplyTextBox(detailsBox, multiline: true);
        A2CoreTheme.ApplyLabel(detailsLabel, muted: true);

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 8), BackColor = A2CoreTheme.Background };
        right.Controls.Add(grid);
        right.Controls.Add(detailsPanel);

        var workspacePanel = new Panel { Dock = DockStyle.Fill, BackColor = A2CoreTheme.Background };
        workspacePanel.Controls.Add(right);
        workspacePanel.Controls.Add(left);

        var main = new Panel { Dock = DockStyle.Fill, BackColor = A2CoreTheme.Background };
        main.Controls.Add(workspacePanel);
        main.Controls.Add(tool);
        main.Controls.Add(status);

        Controls.Add(main);
        Controls.Add(brandRail);

        A2CoreTheme.ApplyStatusLabel(status);

        tableList.SelectedIndexChanged += (_, _) => LoadSelectedTable();
        filterBox.TextChanged += (_, _) => ApplyFilter();

        Shown += (_, _) => OpenWorkspace();
    }

    void OpenWorkspace()
    {
        var settings = EditorSettings.Load();

        using var paksDialog = new FolderBrowserDialog
        {
            Description = "Select the client Content\\Paks folder",
            SelectedPath = PickExistingFolder(settings.LastPaksFolder, AionClientSession.DefaultPaksFolder()),
        };
        if (paksDialog.ShowDialog(this) != DialogResult.OK) return;

        var keyInitial = settings.LastKeyJsonPath is { } keyPath && File.Exists(keyPath)
            ? Path.GetDirectoryName(keyPath)
            : null;
        using var keyDialog = new OpenFileDialog
        {
            Title = "Select AES key JSON (key_hex field)",
            Filter = "JSON files|*.json|All files|*.*",
            InitialDirectory = keyInitial ?? "",
            FileName = settings.LastKeyJsonPath is { } k && File.Exists(k) ? k : "",
        };
        if (keyDialog.ShowDialog(this) != DialogResult.OK) return;

        var defaultOodle = AionClientSession.TryResolveOodleDll(settings.LastOodleDllPath)
            ?? Path.Combine(AionClientSession.DefaultToolingRoot(), "oodle-2.9.16", "oodle-data-shared.dll");
        using var oodleDialog = new OpenFileDialog
        {
            Title = "Select Oodle DLL (oo2core_9_win64.dll or oodle-data-shared.dll)",
            Filter = "Oodle DLL|oo2core_9_win64.dll;oodle-data-shared.dll|DLL files|*.dll|All files|*.*",
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(defaultOodle))
                ? Path.GetDirectoryName(defaultOodle)!
                : AionClientSession.DefaultToolingRoot(),
            FileName = File.Exists(defaultOodle) ? defaultOodle : "",
        };
        var oodlePath = oodleDialog.ShowDialog(this) == DialogResult.OK
            ? oodleDialog.FileName
            : AionClientSession.TryResolveOodleDll(settings.LastOodleDllPath);
        if (string.IsNullOrWhiteSpace(oodlePath) || !File.Exists(oodlePath))
        {
            MessageBox.Show(this, "An Oodle DLL is required to read compressed paks.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var defaultUsmap = AionClientSession.TryResolveUsmap(settings.LastUsmapPath)
            ?? AionClientSession.DefaultUsmapPath();
        using var usmapDialog = new OpenFileDialog
        {
            Title = "Select usmap mappings",
            Filter = "Usmap|*.usmap|All files|*.*",
            InitialDirectory = Directory.Exists(AionClientSession.DefaultMappingsFolder())
                ? AionClientSession.DefaultMappingsFolder()
                : "",
            FileName = File.Exists(defaultUsmap) ? defaultUsmap : "",
        };
        var usmapPath = usmapDialog.ShowDialog(this) == DialogResult.OK
            ? usmapDialog.FileName
            : AionClientSession.TryResolveUsmap(settings.LastUsmapPath);
        if (string.IsNullOrWhiteSpace(usmapPath) || !File.Exists(usmapPath))
        {
            MessageBox.Show(this, "A usmap file is required to decode client tables.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            workspace?.Dispose();
            workspace = EditorWorkspace.Open(
                paksDialog.SelectedPath,
                keyDialog.FileName,
                oodlePath,
                usmapPath);
            settings.LastPaksFolder = paksDialog.SelectedPath;
            settings.LastKeyJsonPath = keyDialog.FileName;
            settings.LastOodleDllPath = oodlePath;
            settings.LastUsmapPath = usmapPath;
            settings.Save();
            allPaths = workspace.ListTablePaths().ToList();
            ApplyFilter();
            status.Text = $"Mounted {allPaths.Count} tables";
            status.ForeColor = A2CoreTheme.Muted;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static string PickExistingFolder(string? remembered, string fallback) =>
        remembered is { Length: > 0 } && Directory.Exists(remembered) ? remembered : fallback;

    void ApplyFilter()
    {
        var q = filterBox.Text.Trim();
        tableList.Items.Clear();
        foreach (var path in allPaths.Where(p => q.Length == 0 || p.Contains(q, StringComparison.OrdinalIgnoreCase)))
            tableList.Items.Add(path);
    }

    void LoadSelectedTable()
    {
        if (workspace is null || tableList.SelectedItem is not string path) return;
        try
        {
            current = workspace.LoadTable(path);
            dataTableGrid = null;
            grid.Columns.Clear();
            grid.Rows.Clear();
            detailsBox.Clear();

            if (current.Kind == TableKind.L10N)
            {
                grid.Columns.Add("key", "Key");
                grid.Columns.Add("value", "Value");
                foreach (var (key, value) in current.FlatRows())
                    grid.Rows.Add(key, value);
            }
            else if (current.BuildDataTableGrid() is { } model)
            {
                dataTableGrid = model;
                foreach (var col in model.Columns)
                {
                    var columnIndex = grid.Columns.Add(col.Header, col.Header);
                    grid.Columns[columnIndex].SortMode = DataGridViewColumnSortMode.NotSortable;
                }

                foreach (var row in model.Rows)
                {
                    var index = grid.Rows.Add(row.Cells.Select(c => c.Display).Cast<object>().ToArray());
                    grid.Rows[index].Tag = row;
                }
            }

            status.Text = path;
            status.ForeColor = A2CoreTheme.Muted;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void UpdateDetailsPanel()
    {
        detailsBox.Clear();
        if (grid.CurrentCell is not { } cell || cell.RowIndex < 0 || cell.ColumnIndex < 0) return;

        if (current?.Kind == TableKind.L10N)
        {
            detailsBox.Text = cell.ColumnIndex == 1
                ? $"L10N value for key {grid.Rows[cell.RowIndex].Cells[0].Value}"
                : "L10N key";
            return;
        }

        if (grid.Rows[cell.RowIndex].Tag is not DataTableGridRow row || cell.ColumnIndex >= row.Cells.Count) return;
        var binding = row.Cells[cell.ColumnIndex];
        if (binding.Tag is null)
        {
            detailsBox.Text = $"Row id: {row.RowKey}";
            return;
        }

        detailsBox.Text = PropertyValueFormat.FormatDetails(binding.Tag);
    }

    void ApplyGridEdit(int row, int column)
    {
        if (current is null || row < 0 || column < 0) return;
        if (current.Kind == TableKind.L10N && current.L10N is not null && column == 1)
        {
            var key = grid.Rows[row].Cells[0].Value?.ToString() ?? "";
            var value = grid.Rows[row].Cells[1].Value?.ToString() ?? "";
            current.L10N.Entries[key] = value;
            return;
        }

        if (grid.Rows[row].Tag is not DataTableGridRow gridRow || column >= gridRow.Cells.Count) return;
        var cellBinding = gridRow.Cells[column];
        if (cellBinding.Tag is null || cellBinding.EditKind is PropertyEditKind.ReadOnly or PropertyEditKind.JsonComplex)
            return;

        var text = grid.Rows[row].Cells[column].Value?.ToString() ?? "";
        if (!PropertyValueFormat.TryApply(cellBinding.Tag, text, out var error))
        {
            MessageBox.Show(this, error ?? "Invalid value", "Edit failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            grid.Rows[row].Cells[column].Value = cellBinding.Display;
            return;
        }

        grid.Rows[row].Cells[column].Value = PropertyValueFormat.FormatCell(cellBinding.Tag);
        UpdateDetailsPanel();
    }

    void SaveCurrent()
    {
        if (workspace is null || current is null) return;
        try
        {
            workspace.SaveTable(current);
            status.Text = $"Saved {current.VirtualPath} to patch project ({PatchStorage.ManifestPath})";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void BuildPatch()
    {
        if (workspace is null) return;
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the game's Content\\Paks folder to deploy z_A2Core_Patch-Windows_P.pak",
            SelectedPath = AionClientSession.DefaultPaksFolder(),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            workspace.BuildPatchPak(dialog.SelectedPath);
            status.Text = $"Deployed {PatchStorage.PakBaseName}.pak (+ empty .sig) to {dialog.SelectedPath}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Build failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ExportJson()
    {
        if (workspace is null) return;
        using var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "client-patch.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        workspace.ExportPatchJson(dialog.FileName);
        status.Text = "Exported patch JSON";
    }

    void ImportJson()
    {
        if (workspace is null) return;
        using var dialog = new OpenFileDialog { Filter = "JSON|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        workspace.ImportPatchJson(dialog.FileName);
        status.Text = "Imported patch JSON";
    }

    void RevertPatch()
    {
        if (workspace is null) return;
        if (MessageBox.Show(this, "Remove all pending patch entries?", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        PatchStorage.RevertAll(workspace.Patch);
        workspace.ReplacePatch(PatchStorage.LoadOrCreate());
        status.Text = "Patch reverted";
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        workspace?.Dispose();
        base.OnFormClosed(e);
    }
}
