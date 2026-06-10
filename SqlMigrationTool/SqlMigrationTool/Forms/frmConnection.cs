using SqlMigrationTool.Models;
using SqlMigrationTool.Services;

namespace SqlMigrationTool.Forms;

public class frmConnection : Form
{
    public ConnectionConfig ConnectionConfig { get; private set; } = new();

    private ComboBox   cboProfile  = null!;
    private TextBox    txtServer   = null!;
    private TextBox    txtPort     = null!;
    private RadioButton rbWinAuth  = null!;
    private RadioButton rbSqlAuth  = null!;
    private TextBox    txtUsername = null!;
    private TextBox    txtPassword = null!;
    private TextBox    txtDatabase = null!;
    private Label      lblStatus   = null!;
    private Button     btnTest     = null!;
    private Button     btnNext     = null!;
    private Panel      pnlSqlAuth  = null!;
    private bool       _testPassed = false;

    public frmConnection()
    {
        InitializeComponent();
        Icon = AppIcon.Instance;
    }

    private void InitializeComponent()
    {
        Text            = "SQL Server Migration Tool — Connect";
        Size            = new Size(560, 520);
        MinimumSize     = new Size(560, 520);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        BackColor       = Color.White;

        // Header
        var header = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Color.FromArgb(0, 84, 166) };
        header.Controls.Add(new Label
        {
            Text      = "SQL Server Migration Tool",
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 14, FontStyle.Bold),
            AutoSize  = true,
            Location  = new Point(16, 10)
        });
        AddAboutLink(header, Color.FromArgb(0, 84, 166));

        // Footer
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Color.FromArgb(245, 245, 245) };
        footer.Paint += (s, e) => e.Graphics.DrawLine(Pens.LightGray, 0, 0, footer.Width, 0);

        btnTest = new Button
        {
            Text      = "Test Connection",
            Location  = new Point(14, 12),
            Size      = new Size(140, 30),
            FlatStyle = FlatStyle.Flat
        };
        btnTest.FlatAppearance.BorderColor = Color.FromArgb(0, 84, 166);
        btnTest.ForeColor = Color.FromArgb(0, 84, 166);
        btnTest.Click += BtnTest_Click;

        btnNext = new Button
        {
            Text      = "Next ▶",
            Location  = new Point(430, 12),
            Size      = new Size(100, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 84, 166),
            ForeColor = Color.White,
            Enabled   = false
        };
        btnNext.FlatAppearance.BorderSize = 0;
        btnNext.Click += (s, e) =>
        {
            BuildConfig();
            DialogResult = DialogResult.OK;
            Close();
        };

        footer.Controls.AddRange(new Control[] { btnTest, btnNext });

        // Content
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 8) };

        int y = 12;

        // ── Profile dropdown ─────────────────────────────────────────────────
        content.Controls.Add(MakeLabel("Profile", 16, y));
        cboProfile = new ComboBox
        {
            Location      = new Point(160, y),
            Width         = 340,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font          = new Font("Segoe UI", 9)
        };
        cboProfile.SelectedIndexChanged += CboProfile_SelectedIndexChanged;
        content.Controls.Add(cboProfile);
        y += 38;

        // ── Server Name ──────────────────────────────────────────────────────
        content.Controls.Add(MakeLabel("Server Name / IP", 16, y));
        txtServer = new TextBox { Location = new Point(160, y), Width = 340 };
        txtServer.TextChanged += OnFieldChanged;
        content.Controls.Add(txtServer);
        y += 34;

        // ── Port ─────────────────────────────────────────────────────────────
        content.Controls.Add(MakeLabel("Port", 16, y));
        txtPort = new TextBox { Location = new Point(160, y), Width = 80, Text = "1433" };
        txtPort.TextChanged += OnFieldChanged;
        content.Controls.Add(txtPort);
        content.Controls.Add(MakeLabel("(leave blank for default)", 248, y, Color.Gray));
        y += 34;

        // ── Authentication ───────────────────────────────────────────────────
        content.Controls.Add(MakeLabel("Authentication", 16, y, Color.Black, true));
        y += 26;

        rbWinAuth = new RadioButton
        {
            Text     = "Windows Authentication",
            Location = new Point(24, y),
            AutoSize = true,
            Checked  = true
        };
        rbWinAuth.CheckedChanged += RbAuth_CheckedChanged;
        content.Controls.Add(rbWinAuth);
        y += 26;

        rbSqlAuth = new RadioButton
        {
            Text     = "SQL Server Authentication",
            Location = new Point(24, y),
            AutoSize = true
        };
        rbSqlAuth.CheckedChanged += RbAuth_CheckedChanged;
        content.Controls.Add(rbSqlAuth);
        y += 28;

        // SQL Auth credentials panel
        pnlSqlAuth = new Panel { Location = new Point(16, y), Size = new Size(490, 70), Visible = false };
        pnlSqlAuth.Controls.Add(MakeLabel("Username", 0, 4));
        txtUsername = new TextBox { Location = new Point(100, 2), Width = 260 };
        txtUsername.TextChanged += OnFieldChanged;
        pnlSqlAuth.Controls.Add(txtUsername);

        pnlSqlAuth.Controls.Add(MakeLabel("Password", 0, 38));
        txtPassword = new TextBox { Location = new Point(100, 36), Width = 260, PasswordChar = '●' };
        txtPassword.TextChanged += OnFieldChanged;
        pnlSqlAuth.Controls.Add(txtPassword);
        content.Controls.Add(pnlSqlAuth);
        y += 76;

        // ── Database ─────────────────────────────────────────────────────────
        content.Controls.Add(MakeLabel("Database Name", 16, y));
        txtDatabase = new TextBox { Location = new Point(160, y), Width = 340 };
        txtDatabase.TextChanged += OnFieldChanged;
        content.Controls.Add(txtDatabase);
        y += 38;

        // ── Status ───────────────────────────────────────────────────────────
        var pnlStatus = new Panel
        {
            Location    = new Point(16, y),
            Size        = new Size(490, 32),
            BackColor   = Color.FromArgb(240, 240, 240),
            BorderStyle = BorderStyle.FixedSingle
        };
        lblStatus = new Label
        {
            Text      = "Status: Not tested",
            ForeColor = Color.Gray,
            AutoSize  = false,
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(6, 0, 0, 0),
            Font      = new Font("Segoe UI", 9)
        };
        pnlStatus.Controls.Add(lblStatus);
        content.Controls.Add(pnlStatus);

        Controls.AddRange(new Control[] { content, footer, header });
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RefreshProfiles(null);
    }

    // ── Profile management ───────────────────────────────────────────────────

    private void RefreshProfiles(string? selectName)
    {
        cboProfile.SelectedIndexChanged -= CboProfile_SelectedIndexChanged;
        cboProfile.Items.Clear();

        var profiles = ProfileService.Load();
        if (profiles.Count == 0)
        {
            cboProfile.Items.Add("(No saved profiles)");
            cboProfile.SelectedIndex = 0;
            cboProfile.Enabled       = false;
            cboProfile.ForeColor     = Color.Gray;
        }
        else
        {
            cboProfile.Enabled   = true;
            cboProfile.ForeColor = Color.Black;
            cboProfile.Items.Add("— Select a profile —");
            int selectIdx = 0;
            foreach (var p in profiles)
            {
                cboProfile.Items.Add(p);
                if (p.Name == selectName)
                    selectIdx = cboProfile.Items.Count - 1;
            }
            cboProfile.SelectedIndex = selectIdx;
        }

        cboProfile.SelectedIndexChanged += CboProfile_SelectedIndexChanged;
    }

    private void CboProfile_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (cboProfile.SelectedItem is ConnectionProfile p)
            FillFromProfile(p);
    }

    private void FillFromProfile(ConnectionProfile p)
    {
        // Detach field-change handlers so loading doesn't clear _testPassed
        txtServer.TextChanged   -= OnFieldChanged;
        txtPort.TextChanged     -= OnFieldChanged;
        txtUsername.TextChanged -= OnFieldChanged;
        txtPassword.TextChanged -= OnFieldChanged;
        txtDatabase.TextChanged -= OnFieldChanged;
        rbWinAuth.CheckedChanged -= RbAuth_CheckedChanged;
        rbSqlAuth.CheckedChanged -= RbAuth_CheckedChanged;

        txtServer.Text   = p.Server;
        txtPort.Text     = p.Port > 0 ? p.Port.ToString() : "1433";
        txtDatabase.Text = p.Database;
        txtUsername.Text = p.Username;
        txtPassword.Text = ProfileService.DecodePassword(p.Password);

        if (p.WindowsAuth) rbWinAuth.Checked = true;
        else               rbSqlAuth.Checked = true;
        pnlSqlAuth.Visible = !p.WindowsAuth;

        // Re-attach
        txtServer.TextChanged   += OnFieldChanged;
        txtPort.TextChanged     += OnFieldChanged;
        txtUsername.TextChanged += OnFieldChanged;
        txtPassword.TextChanged += OnFieldChanged;
        txtDatabase.TextChanged += OnFieldChanged;
        rbWinAuth.CheckedChanged += RbAuth_CheckedChanged;
        rbSqlAuth.CheckedChanged += RbAuth_CheckedChanged;

        _testPassed         = false;
        btnNext.Enabled     = false;
        lblStatus.Text      = "Status: Profile loaded — click Test Connection.";
        lblStatus.ForeColor = Color.FromArgb(0, 84, 166);
    }

    private void SaveCurrentProfile()
    {
        var cfg = BuildConfig();
        if (string.IsNullOrWhiteSpace(cfg.Server) || string.IsNullOrWhiteSpace(cfg.Database))
            return;

        var name = ProfileService.MakeName(cfg.Server, cfg.Database);
        ProfileService.Save(new ConnectionProfile
        {
            Name        = name,
            Server      = cfg.Server,
            Port        = cfg.Port,
            WindowsAuth = cfg.WindowsAuth,
            Username    = cfg.Username,
            Password    = ProfileService.EncodePassword(cfg.Password),
            Database    = cfg.Database
        });
        RefreshProfiles(name);
    }

    // ── Existing handlers ────────────────────────────────────────────────────

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

    private void RbAuth_CheckedChanged(object? sender, EventArgs e)
    {
        pnlSqlAuth.Visible = rbSqlAuth.Checked;
        OnFieldChanged(sender, e);
    }

    private void OnFieldChanged(object? sender, EventArgs e)
    {
        if (_testPassed)
        {
            _testPassed         = false;
            btnNext.Enabled     = false;
            lblStatus.Text      = "Status: Connection not tested";
            lblStatus.ForeColor = Color.Gray;
        }
    }

    private async void BtnTest_Click(object? sender, EventArgs e)
    {
        btnTest.Enabled     = false;
        btnTest.Text        = "Testing…";
        lblStatus.Text      = "Status: Testing connection…";
        lblStatus.ForeColor = Color.DarkOrange;

        var cfg        = BuildConfig();
        var (ok, msg)  = await SqlConnectionService.TestAsync(cfg);

        _testPassed         = ok;
        btnNext.Enabled     = ok;
        lblStatus.Text      = $"Status: {msg}";
        lblStatus.ForeColor = ok ? Color.DarkGreen : Color.DarkRed;
        btnTest.Enabled     = true;
        btnTest.Text        = "Test Connection";

        if (ok)
        {
            ConnectionConfig = cfg;
            SaveCurrentProfile();
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

    private ConnectionConfig BuildConfig()
    {
        if (!int.TryParse(txtPort.Text.Trim(), out int port))
            port = 1433;

        return ConnectionConfig = new ConnectionConfig
        {
            Server      = txtServer.Text.Trim(),
            Port        = port,
            WindowsAuth = rbWinAuth.Checked,
            Username    = txtUsername.Text.Trim(),
            Password    = txtPassword.Text,
            Database    = txtDatabase.Text.Trim()
        };
    }
}
