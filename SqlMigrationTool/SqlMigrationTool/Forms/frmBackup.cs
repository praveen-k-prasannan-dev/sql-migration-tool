using SqlMigrationTool.Models;
using SqlMigrationTool.Services;

namespace SqlMigrationTool.Forms;

public class frmBackup : Form
{
    private readonly ConnectionConfig  _cfg;
    private readonly SchemaExporter    _exporter;

    private TextBox         txtOutputFolder = null!;
    private CheckBox        chkTables       = null!;
    private CheckBox        chkViews        = null!;
    private CheckBox        chkProcs        = null!;
    private CheckBox        chkFuncs        = null!;
    private CheckBox        chkTriggers     = null!;
    private CheckedListBox  lstObjects      = null!;
    private NumericUpDown   nudChunkSize    = null!;
    private ListBox         lstLog          = null!;
    private ListView        lstSummary      = null!;
    private Label           lblStatus       = null!;
    private ProgressBar     progressBar     = null!;
    private Button          btnStart        = null!;
    private Button          btnBack         = null!;

    private List<DatabaseObject> _allObjects = new();
    private CancellationTokenSource? _cts;

    private readonly Dictionary<string, int[]>        _summaryData  = new(); // [total, success, failed]
    private readonly Dictionary<string, ListViewItem> _summaryItems = new();

    public frmBackup(ConnectionConfig cfg)
    {
        _cfg      = cfg;
        _exporter = new SchemaExporter(cfg);
        InitializeComponent();
        Icon = AppIcon.Instance;
    }

    private void InitializeComponent()
    {
        Text            = $"Backup — {_cfg.Server} \\ {_cfg.Database}";
        Size            = new Size(1000, 750);
        MinimumSize     = new Size(1000, 750);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        BackColor       = Color.White;

        // Header
        var header = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Color.FromArgb(0, 84, 166) };
        header.Controls.Add(new Label
        {
            Text      = $"Backup: {_cfg.Server} \\ {_cfg.Database}",
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 11, FontStyle.Bold),
            AutoSize  = true,
            Location  = new Point(16, 13)
        });
        AddAboutLink(header, Color.FromArgb(0, 84, 166));

        // Footer
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Color.FromArgb(245, 245, 245) };
        footer.Paint += (s, e) => e.Graphics.DrawLine(Pens.LightGray, 0, 0, footer.Width, 0);

        btnBack = new Button
        {
            Text      = "◀ Back",
            Location  = new Point(14, 12),
            Size      = new Size(90, 30),
            FlatStyle = FlatStyle.Flat
        };
        btnBack.FlatAppearance.BorderColor = Color.Gray;
        btnBack.Click += (s, e) => Close();

        btnStart = new Button
        {
            Text      = "▶ Start Backup",
            Location  = new Point(828, 12),
            Size      = new Size(140, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 84, 166),
            ForeColor = Color.White
        };
        btnStart.FlatAppearance.BorderSize = 0;
        btnStart.Click += BtnStart_Click;

        footer.Controls.AddRange(new Control[] { btnBack, btnStart });

        // Main content
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 8) };

        int y = 10;

        // Output folder row
        content.Controls.Add(MakeLabel("Output Folder", 0, y));
        txtOutputFolder = new TextBox
        {
            Location = new Point(110, y),
            Width    = 700,
            Text     = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"{_cfg.Database}_{DateTime.Now:yyyyMMdd_HHmmss}")
        };
        content.Controls.Add(txtOutputFolder);

        var btnBrowse = new Button
        {
            Text      = "Browse…",
            Location  = new Point(818, y - 1),
            Size      = new Size(80, 26),
            FlatStyle = FlatStyle.Flat
        };
        btnBrowse.Click += BtnBrowse_Click;
        content.Controls.Add(btnBrowse);
        y += 36;

        // Type filter checkboxes
        content.Controls.Add(MakeLabel("Include:", 0, y + 2));
        chkTables   = MakeTypeChk("Tables",    90,  y, ObjectTypes.Table);
        chkViews    = MakeTypeChk("Views",     200, y, ObjectTypes.View);
        chkProcs    = MakeTypeChk("Procs",     290, y, ObjectTypes.Proc);
        chkFuncs    = MakeTypeChk("Functions", 380, y, ObjectTypes.Func);
        chkTriggers = MakeTypeChk("Triggers",  490, y, ObjectTypes.Trigger);
        content.Controls.AddRange(new Control[] { chkTables, chkViews, chkProcs, chkFuncs, chkTriggers });
        y += 34;

        // Object list
        lstObjects = new CheckedListBox
        {
            Location       = new Point(0, y),
            Size           = new Size(958, 220),
            CheckOnClick   = true,
            Font           = new Font("Consolas", 9),
            IntegralHeight = false
        };
        content.Controls.Add(lstObjects);
        y += 228;

        // Chunk size
        content.Controls.Add(MakeLabel("Rows per file:", 0, y + 2));
        nudChunkSize = new NumericUpDown
        {
            Location = new Point(110, y),
            Width    = 90,
            Minimum  = 100,
            Maximum  = 100000,
            Value    = 1000
        };
        content.Controls.Add(nudChunkSize);
        content.Controls.Add(MakeLabel("(split large tables into chunks)", 210, y + 2, Color.Gray));
        y += 36;

        // Section headers
        const int logW     = 630;
        const int gap      = 12;
        const int sumX     = logW + gap;
        const int sumW     = 316;

        content.Controls.Add(MakeLabel("Progress Log", 0, y, null, true));
        content.Controls.Add(MakeLabel("Summary", sumX, y, null, true));
        y += 22;

        // Progress log
        lstLog = new ListBox
        {
            Location       = new Point(0, y),
            Size           = new Size(logW, 220),
            BackColor      = Color.FromArgb(18, 28, 42),
            ForeColor      = Color.FromArgb(173, 216, 230),
            Font           = new Font("Consolas", 9),
            IntegralHeight = false,
            BorderStyle    = BorderStyle.FixedSingle
        };
        content.Controls.Add(lstLog);

        // Summary table
        lstSummary = new ListView
        {
            Location               = new Point(sumX, y),
            Size                   = new Size(sumW, 220),
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

        y += 228;

        // Progress bar
        progressBar = new ProgressBar
        {
            Location = new Point(0, y),
            Size     = new Size(958, 6),
            Style    = ProgressBarStyle.Marquee,
            Visible  = false
        };
        content.Controls.Add(progressBar);
        y += 10;

        lblStatus = new Label
        {
            Location  = new Point(0, y),
            AutoSize  = false,
            Width     = 958,
            Text      = "Ready",
            ForeColor = Color.Gray,
            Font      = new Font("Segoe UI", 9)
        };
        content.Controls.Add(lblStatus);

        Controls.AddRange(new Control[] { content, footer, header });
    }

    private CheckBox MakeTypeChk(string text, int x, int y, string type)
    {
        var chk = new CheckBox
        {
            Text     = text,
            Location = new Point(x, y),
            AutoSize = true,
            Checked  = true,
            Tag      = type
        };
        chk.CheckedChanged += TypeChk_Changed;
        return chk;
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

    private void TypeChk_Changed(object? sender, EventArgs e)
    {
        if (sender is not CheckBox chk || chk.Tag is not string type) return;
        for (int i = 0; i < lstObjects.Items.Count; i++)
        {
            if (lstObjects.Items[i] is DatabaseObject obj && obj.Type == type)
                lstObjects.SetItemChecked(i, chk.Checked);
        }
    }

    private void BtnBrowse_Click(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description            = "Select output folder",
            SelectedPath           = txtOutputFolder.Text,
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog() == DialogResult.OK)
            txtOutputFolder.Text = dlg.SelectedPath;
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        await LoadObjectsAsync();
    }

    private async Task LoadObjectsAsync()
    {
        lblStatus.Text      = "Loading database objects…";
        lblStatus.ForeColor = Color.DarkOrange;
        btnStart.Enabled    = false;

        try
        {
            _allObjects = await _exporter.ListAllObjectsAsync();
            lstObjects.BeginUpdate();
            lstObjects.Items.Clear();
            foreach (var obj in _allObjects)
                lstObjects.Items.Add(obj, true);
            lstObjects.EndUpdate();

            UpdateTypeCounts();

            lblStatus.Text      = $"Loaded {_allObjects.Count} objects.";
            lblStatus.ForeColor = Color.DarkGreen;
            btnStart.Enabled    = true;
        }
        catch (Exception ex)
        {
            lblStatus.Text      = $"Error loading objects: {ex.Message}";
            lblStatus.ForeColor = Color.DarkRed;
        }
    }

    private void UpdateTypeCounts()
    {
        foreach (CheckBox chk in new[] { chkTables, chkViews, chkProcs, chkFuncs, chkTriggers })
        {
            if (chk.Tag is string type)
            {
                int count = _allObjects.Count(o => o.Type == type);
                chk.Text = type switch
                {
                    ObjectTypes.Table   => $"Tables ({count})",
                    ObjectTypes.View    => $"Views ({count})",
                    ObjectTypes.Proc    => $"Procs ({count})",
                    ObjectTypes.Func    => $"Functions ({count})",
                    ObjectTypes.Trigger => $"Triggers ({count})",
                    _         => chk.Text
                };
            }
        }
    }

    // ── Summary table ────────────────────────────────────────────────────────

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

    private void InitSummary(List<DatabaseObject> selected)
    {
        lstSummary.Items.Clear();
        _summaryData.Clear();
        _summaryItems.Clear();

        var hasTable   = selected.Any(o => o.Type == ObjectTypes.Table);
        var hasView    = selected.Any(o => o.Type == ObjectTypes.View);
        var hasProc    = selected.Any(o => o.Type == ObjectTypes.Proc);
        var hasFunc    = selected.Any(o => o.Type == ObjectTypes.Func);
        var hasTrigger = selected.Any(o => o.Type == ObjectTypes.Trigger);

        // Prerequisites (schemas, types, sequences, synonyms) are always exported.
        var show = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ObjectTypes.Schema, ObjectTypes.DataType, ObjectTypes.TableType,
            ObjectTypes.Sequence, ObjectTypes.Synonym
        };
        if (hasTable)   { show.Add(ObjectTypes.Table); show.Add(ObjectTypes.Data); show.Add(ObjectTypes.Fk); show.Add(ObjectTypes.Index); }
        if (hasView)    show.Add(ObjectTypes.View);
        if (hasProc)    show.Add(ObjectTypes.Proc);
        if (hasFunc)    show.Add(ObjectTypes.Func);
        if (hasTrigger) show.Add(ObjectTypes.Trigger);

        foreach (var (type, label) in ObjectTypes.SummaryRows)
        {
            if (!show.Contains(type)) continue;

            _summaryData[type] = new[] { 0, 0, 0 };

            var item = new ListViewItem(label)
            {
                ForeColor               = Color.FromArgb(173, 216, 230),
                UseItemStyleForSubItems = false
            };
            item.SubItems.Add("0").ForeColor = Color.FromArgb(173, 216, 230);
            item.SubItems.Add("0").ForeColor = Color.FromArgb(80, 160, 80);
            item.SubItems.Add("0").ForeColor = Color.FromArgb(120, 120, 140);
            lstSummary.Items.Add(item);
            _summaryItems[type] = item;
        }
    }

    private void UpdateSummary(string type, bool success)
    {
        var key = type.ToUpper();
        if (!_summaryData.TryGetValue(key, out var counts))  return;
        if (!_summaryItems.TryGetValue(key, out var item))   return;

        counts[0]++;
        if (success) counts[1]++;
        else         counts[2]++;

        item.SubItems[1].Text = counts[0].ToString();

        item.SubItems[2].Text      = counts[1].ToString();
        item.SubItems[2].ForeColor = counts[1] > 0 ? Color.LimeGreen : Color.FromArgb(80, 160, 80);

        item.SubItems[3].Text      = counts[2].ToString();
        item.SubItems[3].ForeColor = counts[2] > 0 ? Color.Salmon : Color.FromArgb(120, 120, 140);
    }

    // ── Backup ───────────────────────────────────────────────────────────────

    private async void BtnStart_Click(object? sender, EventArgs e)
    {
        var folder = txtOutputFolder.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            MessageBox.Show("Please specify an output folder.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var selected = lstObjects.CheckedItems.OfType<DatabaseObject>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Please select at least one object to back up.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        btnStart.Enabled    = false;
        btnBack.Enabled     = false;
        progressBar.Visible = true;
        lstLog.Items.Clear();
        InitSummary(selected);

        _cts = new CancellationTokenSource();
        var progress = new Progress<BackupProgress>(bp =>
        {
            lstLog.Items.Add(bp.Message);
            lstLog.TopIndex = lstLog.Items.Count - 1;
            lblStatus.Text  = bp.Message;

            if (bp.Type != null && bp.Success.HasValue)
                UpdateSummary(bp.Type, bp.Success.Value);
        });

        try
        {
            var svc = new BackupService(_cfg);
            await svc.RunAsync(folder, selected, (int)nudChunkSize.Value, progress, _cts.Token);

            MessageBox.Show(
                $"Backup complete!\n\nOutput folder:\n{folder}",
                "Backup Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            lblStatus.Text = "Backup cancelled.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Backup failed:\n{ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            lblStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            btnStart.Enabled    = true;
            btnBack.Enabled     = true;
            progressBar.Visible = false;
            _cts?.Dispose();
            _cts = null;
        }
    }
}
