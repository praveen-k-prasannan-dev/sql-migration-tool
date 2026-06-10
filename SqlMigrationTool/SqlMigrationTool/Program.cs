using SqlMigrationTool.Forms;

namespace SqlMigrationTool;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Splash screen (blocks until animation completes)
        using (var splash = new frmSplash())
            splash.ShowDialog();

        // Navigation loop: connection screen -> operation screen -> back
        while (true)
        {
            using var frmConn = new frmConnection();
            if (frmConn.ShowDialog() != DialogResult.OK)
                break;

            var config = frmConn.ConnectionConfig;

            while (true)
            {
                using var frmOp = new frmOperation(config);
                var result = frmOp.ShowDialog();

                if (result == DialogResult.Cancel)
                    break;
                if (result == DialogResult.Abort)
                    return;
            }
        }
    }
}
