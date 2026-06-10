using SqlMigrationTool.Models;

namespace SqlMigrationTool.Forms;

public class frmOperation : Form
{
    private readonly ConnectionConfig _cfg;

    public frmOperation(ConnectionConfig cfg)
    {
        _cfg = cfg;
        InitializeComponent();
        Icon = AppIcon.Instance;
    }

    private void InitializeComponent()
    {
        Text            = "SQL Server Migration Tool — Select Operation";
        Size            = new Size(560, 380);
        MinimumSize     = new Size(560, 380);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        BackColor       = Color.White;

        // Header
        var header = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Color.FromArgb(0, 84, 166) };
        header.Controls.Add(new Label
        {
            Text      = $"Connected: {_cfg.Server} \\ {_cfg.Database}",
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 11, FontStyle.Bold),
            AutoSize  = true,
            Location  = new Point(16, 13)
        });
        AddAboutLink(header, Color.FromArgb(0, 84, 166));

        // Footer
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Color.FromArgb(245, 245, 245) };
        footer.Paint += (s, e) => e.Graphics.DrawLine(Pens.LightGray, 0, 0, footer.Width, 0);

        var btnBack = new Button
        {
            Text      = "◀ Back",
            Location  = new Point(14, 12),
            Size      = new Size(90, 30),
            FlatStyle = FlatStyle.Flat
        };
        btnBack.FlatAppearance.BorderColor = Color.Gray;
        btnBack.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
        footer.Controls.Add(btnBack);

        // Center question label
        var lblQuestion = new Label
        {
            Text      = "What would you like to do?",
            Font      = new Font("Segoe UI", 13, FontStyle.Bold),
            AutoSize  = true,
            ForeColor = Color.FromArgb(30, 30, 30)
        };

        // Cards panel
        var cardsPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
            Padding       = new Padding(0),
            WrapContents  = false
        };

        var backupCard  = MakeCard("📦  BACKUP",
            "Export database\nto portable files",
            Color.FromArgb(0, 84, 166));
        var installCard = MakeCard("▶  INSTALL",
            "Apply exported\nfiles to server",
            Color.FromArgb(0, 128, 0));

        backupCard.Click  += (s, e) => OpenBackup();
        installCard.Click += (s, e) => OpenInstall();

        // Forward clicks on child labels/controls to the card
        ForwardClicks(backupCard,  () => OpenBackup());
        ForwardClicks(installCard, () => OpenInstall());

        cardsPanel.Controls.AddRange(new Control[] { backupCard, installCard });

        // Content panel
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };

        lblQuestion.Location   = new Point(16, 16);
        cardsPanel.Location    = new Point(16, 60);

        content.Controls.Add(lblQuestion);
        content.Controls.Add(cardsPanel);

        Controls.AddRange(new Control[] { content, footer, header });
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

    private static Panel MakeCard(string title, string description, Color accentColor)
    {
        var card = new Panel
        {
            Size        = new Size(220, 150),
            Margin      = new Padding(0, 0, 20, 0),
            BackColor   = Color.White,
            Cursor      = Cursors.Hand,
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblTitle = new Label
        {
            Text      = title,
            Font      = new Font("Segoe UI", 13, FontStyle.Bold),
            ForeColor = accentColor,
            AutoSize  = true,
            Location  = new Point(14, 20)
        };

        var lblDesc = new Label
        {
            Text      = description,
            Font      = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(80, 80, 80),
            AutoSize  = false,
            Size      = new Size(195, 60),
            Location  = new Point(14, 68)
        };

        card.Controls.AddRange(new Control[] { lblTitle, lblDesc });

        // Hover highlight
        card.MouseEnter += (s, e) => card.BackColor = Color.FromArgb(240, 248, 255);
        card.MouseLeave += (s, e) => card.BackColor = Color.White;

        return card;
    }

    private static void ForwardClicks(Panel card, Action onClick)
    {
        foreach (Control ctrl in card.Controls)
        {
            ctrl.Click      += (s, e) => onClick();
            ctrl.MouseEnter += (s, e) => card.BackColor = Color.FromArgb(240, 248, 255);
            ctrl.MouseLeave += (s, e) => card.BackColor = Color.White;
        }
    }

    private void OpenBackup()
    {
        using var frm = new frmBackup(_cfg);
        frm.ShowDialog(this);
    }

    private void OpenInstall()
    {
        using var frm = new frmInstall(_cfg);
        frm.ShowDialog(this);
    }
}
