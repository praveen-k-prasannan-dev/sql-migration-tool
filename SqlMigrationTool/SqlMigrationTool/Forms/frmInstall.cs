using SqlMigrationTool.Models;
using SqlMigrationTool.Services;

namespace SqlMigrationTool.Forms;

public class frmInstall : Form
{
    private readonly ConnectionConfig _cfg;

    private TextBox    txtFolder       = null!;
    private Panel      pnlManifest     = null!;
    private Label      lblManifestInfo = null!;
    private ListView   lstProgress     = null!;
    private ListView   lstSummary      = null!;
    private ProgressBar progressBar    = null!;
    private Label      lblStatus       = null!;
    private Label      lblErrors       = null!;
    private Button     btnLoad         = null!;
    private Button     btnStart        = null!;
    private Button     btnAbort        = null!;
    private Button     btnBack         = null!;
    private ComboBox   cmbScriptMode   = null!;

    private InstallManifest?          _manifest;
    private string                    _folder = "";
    private CancellationTokenSource?  _cts;

    private readonly Dictionary<ManifestEntry, ListViewItem> _itemMap      = new();
    private readonly Dictionary<string, int[]>               _summaryData  = new(); // [total, success, failed]
    private readonly Dictionary<string, ListViewItem>        _summaryItems = new();

    public frmInstall(ConnectionConfig cfg)
    {
        _cfg = cfg;
        InitializeComponent();
        Icon = AppIcon.Instance;
    }

    private void InitializeComponent()
    {
        Text            = $"Install to: {_cfg.Server} \\ {_cfg.Database}";
        Size            = new Size(1000, 750);
        MinimumSize     = new Size(1000, 750);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        BackColor       = Color.White;

        // Header
        var header = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Color.FromArgb(0, 100, 0) };
        header.Controls.Add(new Label
        {
            Text      = $"Install to: {_cfg.Server} \\ {_cfg.Database}",
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 11, FontStyle.Bold),
            AutoSize  = true,
            Location  = new Point(16, 13)
        });
        AddAboutLink(header, Color.FromArgb(0, 100, 0));

        // Footer
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Color.FromArgb(245, 245, 245) };
        footer.Paint += (s, e) => e.Graphics.DrawLine(Pens.LightGray, 0, 0, footer.Width, 0);

        btnBack = new Button
        {
            Text      = "◀ Back",
            Location  = new Point(14, 12),
            Size      = new Size(80, 30),
            FlatStyle = FlatStyle.Flat
        };
        btnBack.FlatAppearance.BorderColor = Color.Gray;
        btnBack.Click += (s, e) => Close();

        btnAbort = new Button
        {
            Text      = "■ Abort",
            Location  = new Point(260, 12),
            Size      = new Size(90, 30),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.DarkRed,
            Enabled   = false
        };
        btnAbort.FlatAppearance.BorderColor = Color.DarkRed;
        btnAbort.Click += (s, e) => _cts?.Cancel();

        btnStart = new Button
        {
            Text      = "▶ Start Installation",
            Location  = new Point(838, 12),
            Size      = new Size(130, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 100, 0),
            ForeColor = Color.White,
            Enabled   = false
        };
        btnStart.FlatAppearance.BorderSize = 0;
        btnStart.Click += BtnStart_Click;

        footer.Controls.AddRange(new Control[] { btnBack, btnAbort, btnStart });

        // Main content
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 8) };

        int y = 10;

        // Folder row
        content.Controls.Add(MakeLabel("Backup Folder", 0, y));
        txtFolder = new TextBox { Location = new Point(120, y), Width = 690 };
        content.Controls.Add(txtFolder);

        var btnBrowse = new Button
        {
            Text      = "Browse…",
            Location  = new Point(818, y - 1),
            Size      = new Size(80, 26),
            FlatStyle = FlatStyle.Flat
        };
        btnBrowse.Click += BtnBrowse_Click;
        content.Controls.Add(btnBrowse);
        y += 34;

        btnLoad = new Button
        {
            Text      = "Load Manifest",
            Location  = new Point(0, y),
            Size      = new Size(120, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 84, 166),
            ForeColor = Color.White
        };
        btnLoad.FlatAppearance.BorderSize = 0;
        btnLoad.Click += BtnLoad_Click;
        content.Controls.Add(btnLoad);

        // Script mode: preselected from the manifest; another choice re-wraps the
        // scripts in memory at install time (the backup files are not changed).
        content.Controls.Add(new Label { Text = "Script mode:", Location = new Point(140, y + 6), AutoSize = true });
        cmbScriptMode = new ComboBox
        {
            Location      = new Point(230, y + 3),
            Width         = 240,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Enabled       = false
        };
        foreach (var mode in Enum.GetValues<ScriptMode>())
            cmbScriptMode.Items.Add(new ScriptModeItem(mode));
        content.Controls.Add(cmbScriptMode);
        y += 38;

        // Manifest info panel
        pnlManifest = new Panel
        {
            Location    = new Point(0, y),
            Size        = new Size(958, 80),
            BackColor   = Color.FromArgb(240, 248, 240),
            BorderStyle = BorderStyle.FixedSingle,
            Visible     = false
        };
        lblManifestInfo = new Label
        {
            Location  = new Point(8, 8),
            AutoSize  = false,
            Size      = new Size(938, 64),
            Font      = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(0, 80, 0)
        };
        pnlManifest.Controls.Add(lblManifestInfo);
        content.Controls.Add(pnlManifest);
        y += 88;

        // Section headers: "Progress" left, "Summary" right
        const int progressW = 638;
        const int gap       = 12;
        const int summaryX  = progressW + gap;
        const int summaryW  = 308;

        content.Controls.Add(MakeLabel("Progress", 0, y, null, true));
        content.Controls.Add(MakeLabel("Summary", summaryX, y, null, true));
        y += 22;

        // Progress list (narrower to make room for summary)
        lstProgress = new ListView
        {
            Location      = new Point(0, y),
            Size          = new Size(progressW, 350),
            View          = View.Details,
            FullRowSelect = true,
            GridLines     = false,
            BackColor     = Color.FromArgb(18, 28, 42),
            ForeColor     = Color.FromArgb(173, 216, 230),
            Font          = new Font("Consolas", 9),
            HeaderStyle   = ColumnHeaderStyle.Nonclickable,
            BorderStyle   = BorderStyle.FixedSingle
        };
        lstProgress.Columns.Add("", 22);
        lstProgress.Columns.Add("Phase", 46);
        lstProgress.Columns.Add("Type", 56);
        lstProgress.Columns.Add("Object", 240);
        lstProgress.Columns.Add("Status", 80);
        content.Controls.Add(lstProgress);

        // Summary table (right side)
        lstSummary = new ListView
        {
            Location               = new Point(summaryX, y),
            Size                   = new Size(summaryW, 350),
            View                   = View.Details,
            FullRowSelect          = true,
            GridLines              = true,
            BackColor              = Color.FromArgb(18, 28, 42),
            ForeColor              = Color.FromArgb(173, 216, 230),
            Font                   = new Font("Consolas", 9),
            HeaderStyle            = ColumnHeaderStyle.Nonclickable,
            BorderStyle            = BorderStyle.FixedSingle,
            MultiSelect            = false
        };
        lstSummary.Columns.Add("Type",     106);
        lstSummary.Columns.Add("Total",     50);
        lstSummary.Columns.Add("✔ OK",      54);
        lstSummary.Columns.Add("✘ Failed",  58);
        content.Controls.Add(lstSummary);

        y += 358;

        // Progress bar
        progressBar = new ProgressBar
        {
            Location = new Point(0, y),
            Size     = new Size(958, 16),
            Minimum  = 0,
            Value    = 0,
            Style    = ProgressBarStyle.Continuous
        };
        content.Controls.Add(progressBar);
        y += 20;

        // Error count + status
        lblErrors = new Label
        {
            Location  = new Point(0, y),
            AutoSize  = true,
            Text      = "Errors: 0",
            ForeColor = Color.Gray,
            Font      = new Font("Segoe UI", 9)
        };
        content.Controls.Add(lblErrors);

        lblStatus = new Label
        {
            Location  = new Point(90, y),
            AutoSize  = false,
            Width     = 800,
            Text      = "Ready — load a backup folder to begin.",
            ForeColor = Color.Gray,
            Font      = new Font("Segoe UI", 9)
        };
        content.Controls.Add(lblStatus);

        Controls.AddRange(new Control[] { content, footer, header });
    }

    private static Label MakeLabel(string text, int x, int y,
        Color? color = null, bool bold = false)
    {
        return new Label
        {
            Text      = text,
            Location  = new Point(x, y + 3),
            AutoSize  = true,
            ForeColor = color ?? Color.Black,
            Font      = bold
                ? new Font("Segoe UI", 9, FontStyle.Bold)
                : new Font("Segoe UI", 9)
        };
    }

    private void BtnBrowse_Click(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description            = "Select backup folder",
            UseDescriptionForTitle = true
        };
        if (!string.IsNullOrWhiteSpace(txtFolder.Text))
            dlg.SelectedPath = txtFolder.Text;

        if (dlg.ShowDialog() == DialogResult.OK)
            txtFolder.Text = dlg.SelectedPath;
    }

    private void BtnLoad_Click(object? sender, EventArgs e)
    {
        _folder = txtFolder.Text.Trim();
        if (string.IsNullOrWhiteSpace(_folder))
        {
            MessageBox.Show("Please select a backup folder.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            _manifest = InstallService.LoadManifest(_folder);
            PopulateProgressList();
            PopulateSummaryTable();
            ShowManifestInfo();

            var backupMode = InstallService.ManifestMode(_manifest);
            cmbScriptMode.SelectedIndex = cmbScriptMode.Items.Cast<ScriptModeItem>().ToList().FindIndex(i => i.Mode == backupMode);
            cmbScriptMode.Enabled = true;

            btnStart.Enabled    = true;
            lblStatus.Text      = "Manifest loaded. Ready to install.";
            lblStatus.ForeColor = Color.DarkGreen;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Load Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            lblStatus.Text      = $"Error: {ex.Message}";
            lblStatus.ForeColor = Color.DarkRed;
        }
    }

    private void ShowManifestInfo()
    {
        if (_manifest is null) return;

        var totalFiles = _manifest.Phases.Sum(p => p.Files.Count);
        var s = _manifest.Summary;
        lblManifestInfo.Text =
            $"✔ app_install.config.json found  (backup format {_manifest.Version})\r\n" +
            $"Source:  {_manifest.SourceServer} \\ {_manifest.SourceDatabase}  ({_manifest.Created[..10]})" +
            (_manifest.DatabaseCollation is { } collation ? $"   Collation: {collation}" : "") + "\r\n" +
            $"Files:   {totalFiles} across {_manifest.Phases.Count} phases\r\n" +
            $"Tables:{s.TotalTables}  Views:{s.TotalViews}  Procs:{s.TotalProcedures}  Functions:{s.TotalFunctions}  DataFiles:{s.TotalDataFiles}";

        pnlManifest.Visible = true;
    }

    private void PopulateProgressList()
    {
        if (_manifest is null) return;
        lstProgress.Items.Clear();
        _itemMap.Clear();

        int totalFiles = _manifest.Phases.Sum(p => p.Files.Count);
        progressBar.Maximum = totalFiles;
        progressBar.Value   = 0;

        foreach (var phase in _manifest.Phases)
        {
            var headerItem = new ListViewItem("")
            {
                BackColor = Color.FromArgb(30, 45, 65),
                ForeColor = Color.White,
                Font      = new Font("Consolas", 9, FontStyle.Bold)
            };
            headerItem.SubItems.Add($"Phase {phase.Phase}");
            headerItem.SubItems.Add("");
            headerItem.SubItems.Add(phase.Name);
            headerItem.SubItems.Add("");
            lstProgress.Items.Add(headerItem);

            foreach (var entry in phase.Files)
            {
                var item = new ListViewItem("○")
                {
                    ForeColor = Color.FromArgb(100, 140, 180),
                    Tag       = entry
                };
                item.SubItems.Add($"  Ph{phase.Phase}");
                item.SubItems.Add(entry.Type);
                item.SubItems.Add(entry.Object);
                item.SubItems.Add("Pending");
                lstProgress.Items.Add(item);
                _itemMap[entry] = item;
            }
        }
    }

    private void AddAboutLink(Panel header, Color bgColor)
    {
        var pnl = new Panel { Dock = DockStyle.Right, Width = 68, BackColor = bgColor };
        var lnk = new LinkLabel
        {
            Text             = "About",
            AutoSize         = true,
            Font             = new Font("Segoe UI", 8.5f),
            ForeColor        = Color.FromArgb(215, 220, 240),
            LinkColor        = Color.FromArgb(215, 220, 240),
            ActiveLinkColor  = Color.White,
            VisitedLinkColor = Color.FromArgb(215, 220, 240),
            LinkBehavior     = LinkBehavior.HoverUnderline,
            Location         = new Point(6, 17)
        };
        lnk.LinkClicked += (s, e) => { using var sp = new frmSplash(); sp.ShowDialog(this); };
        pnl.Controls.Add(lnk);
        header.Controls.Add(pnl);
    }

    private void PopulateSummaryTable()
    {
        if (_manifest is null) return;

        // Count totals by type from manifest
        var typeCounts = _manifest.Phases
            .SelectMany(p => p.Files)
            .GroupBy(f => f.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key.ToUpper(), g => g.Count(), StringComparer.OrdinalIgnoreCase);

        lstSummary.Items.Clear();
        _summaryData.Clear();
        _summaryItems.Clear();

        foreach (var (type, label) in ObjectTypes.SummaryRows)
        {
            if (!typeCounts.TryGetValue(type, out var total)) continue;

            _summaryData[type] = new[] { total, 0, 0 };

            var item = new ListViewItem(label)
            {
                ForeColor              = Color.FromArgb(173, 216, 230),
                UseItemStyleForSubItems = false
            };
            item.SubItems.Add(total.ToString()).ForeColor  = Color.FromArgb(173, 216, 230);
            item.SubItems.Add("0").ForeColor               = Color.FromArgb(80, 160, 80);
            item.SubItems.Add("0").ForeColor               = Color.FromArgb(120, 120, 140);
            lstSummary.Items.Add(item);
            _summaryItems[type] = item;
        }
    }

    private void ResetSummaryCounters()
    {
        foreach (var (type, counts) in _summaryData)
        {
            counts[1] = 0;
            counts[2] = 0;
            if (!_summaryItems.TryGetValue(type, out var item)) continue;
            item.SubItems[2].Text      = "0";
            item.SubItems[2].ForeColor = Color.FromArgb(80, 160, 80);
            item.SubItems[3].Text      = "0";
            item.SubItems[3].ForeColor = Color.FromArgb(120, 120, 140);
        }
    }

    private void UpdateSummary(string type, bool success)
    {
        var key = type.ToUpper();
        if (!_summaryData.TryGetValue(key, out var counts))   return;
        if (!_summaryItems.TryGetValue(key, out var item))    return;

        if (success) counts[1]++;
        else         counts[2]++;

        item.SubItems[2].Text      = counts[1].ToString();
        item.SubItems[2].ForeColor = counts[1] > 0
            ? Color.LimeGreen
            : Color.FromArgb(80, 160, 80);

        item.SubItems[3].Text      = counts[2].ToString();
        item.SubItems[3].ForeColor = counts[2] > 0
            ? Color.Salmon
            : Color.FromArgb(120, 120, 140);
    }

    private async void BtnStart_Click(object? sender, EventArgs e)
    {
        if (_manifest is null || string.IsNullOrWhiteSpace(_folder)) return;

        btnStart.Enabled  = false;
        btnLoad.Enabled   = false;
        cmbScriptMode.Enabled = false;
        btnBack.Enabled   = false;
        btnAbort.Enabled  = true;
        progressBar.Value = 0;
        ResetSummaryCounters();

        int errorCount = 0;

        _cts = new CancellationTokenSource();
        var progress = new Progress<InstallProgress>(p =>
        {
            if (!_itemMap.TryGetValue(p.Entry, out var item)) return;

            switch (p.Status)
            {
                case InstallStatus.Running:
                    item.Text             = "►";
                    item.ForeColor        = Color.Yellow;
                    item.SubItems[4].Text = "Running…";
                    lblStatus.Text        = $"Processing {p.Entry.Object}…";
                    lstProgress.EnsureVisible(item.Index);
                    break;

                case InstallStatus.Done:
                    item.Text             = "✔";
                    item.ForeColor        = Color.LimeGreen;
                    item.SubItems[4].Text = "Done";
                    progressBar.Value     = Math.Min(progressBar.Value + 1, progressBar.Maximum);
                    UpdateSummary(p.Entry.Type, success: true);
                    break;

                case InstallStatus.Error:
                    item.Text             = "✘";
                    item.ForeColor        = Color.Red;
                    item.SubItems[4].Text = $"Error: {p.Message[..Math.Min(p.Message.Length, 60)]}";
                    progressBar.Value     = Math.Min(progressBar.Value + 1, progressBar.Maximum);
                    errorCount++;
                    lblErrors.Text        = $"Errors: {errorCount}";
                    lblErrors.ForeColor   = Color.Red;
                    UpdateSummary(p.Entry.Type, success: false);
                    break;
            }
        });

        try
        {
            var svc = new InstallService(_cfg);
            var mode = ((ScriptModeItem)cmbScriptMode.SelectedItem!).Mode;
            await svc.RunAsync(_folder, _manifest, progress, _cts.Token, mode);

            if (errorCount == 0)
            {
                MessageBox.Show("Installation complete with no errors!", "Success",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                lblStatus.Text      = "Installation complete.";
                lblStatus.ForeColor = Color.DarkGreen;
            }
            else
            {
                MessageBox.Show(
                    $"Installation finished with {errorCount} error(s).\n\nSee error.log in the backup folder for details.",
                    "Completed with Errors", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                lblStatus.Text      = $"Finished with {errorCount} error(s). See error.log.";
                lblStatus.ForeColor = Color.DarkOrange;
            }
        }
        catch (OperationCanceledException)
        {
            lblStatus.Text      = "Installation aborted.";
            lblStatus.ForeColor = Color.DarkOrange;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Installation failed:\n{ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            lblStatus.Text      = $"Error: {ex.Message}";
            lblStatus.ForeColor = Color.DarkRed;
        }
        finally
        {
            btnStart.Enabled = true;
            btnLoad.Enabled  = true;
            cmbScriptMode.Enabled = true;
            btnBack.Enabled  = true;
            btnAbort.Enabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }
}
