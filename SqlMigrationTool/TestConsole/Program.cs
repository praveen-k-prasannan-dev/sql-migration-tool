using SqlMigrationTool.Models;
using SqlMigrationTool.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var cfg = new ConnectionConfig
{
    Server      = @"(localdb)\MSSQLLocalDB",
    Port        = 0,
    WindowsAuth = false,
    Username    = "sa",
    Password    = "pass@word1",
    Database    = "AllyOneLP"
};

Console.WriteLine("=== SQL Migration Tool — Service Test ===");
Console.WriteLine($"Server  : {cfg.Server}");
Console.WriteLine($"Database: {cfg.Database}");
Console.WriteLine($"Auth    : SQL Server ({cfg.Username})");
Console.WriteLine();

// ── Step 1: Connection test ──────────────────────────────────────────────────
Console.Write("[1/3] Testing connection... ");
var (ok, msg) = await SqlConnectionService.TestAsync(cfg);
if (!ok)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"FAILED\n      {msg}");
    Console.ResetColor();

    // Try Windows Auth as fallback
    Console.WriteLine();
    Console.Write("      Retrying with Windows Authentication... ");
    cfg.WindowsAuth = true;
    (ok, msg) = await SqlConnectionService.TestAsync(cfg);
    if (!ok)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAILED\n      {msg}");
        Console.ResetColor();
        Console.WriteLine("\nCannot proceed without a working connection.");
        return;
    }
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"OK (Windows Auth)\n      {msg}");
    Console.ResetColor();
}
else
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"OK\n      {msg}");
    Console.ResetColor();
}

// ── Step 2: List all objects ─────────────────────────────────────────────────
Console.WriteLine();
Console.Write("[2/3] Loading database objects... ");
var exporter = new SchemaExporter(cfg);
List<DatabaseObject> objects;
try
{
    objects = await exporter.ListAllObjectsAsync();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"OK — {objects.Count} objects found");
    Console.ResetColor();

    var byType = objects.GroupBy(o => o.Type)
                        .OrderBy(g => g.Key)
                        .ToList();
    foreach (var g in byType)
        Console.WriteLine($"      {g.Key,-10} {g.Count(),3}  —  {string.Join(", ", g.Take(5).Select(o => o.FullName))}{(g.Count() > 5 ? " …" : "")}");
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"FAILED\n      {ex.Message}");
    Console.ResetColor();
    return;
}

// ── Step 3: Backup to temp folder ────────────────────────────────────────────
Console.WriteLine();
var outDir = Path.Combine(Path.GetTempPath(), $"MigrationTest_{DateTime.Now:yyyyMMdd_HHmmss}");
Console.WriteLine($"[3/3] Running backup to: {outDir}");
Console.WriteLine("      (selecting all objects, chunk size 500)");
Console.WriteLine();

var progress = new Progress<BackupProgress>(bp =>
{
    var color = bp.Success == true    ? ConsoleColor.Green
              : bp.Success == false   ? ConsoleColor.Red
              : bp.Message.StartsWith(">") ? ConsoleColor.Cyan
              : ConsoleColor.Gray;
    Console.ForegroundColor = color;
    Console.WriteLine("      " + bp.Message);
    Console.ResetColor();
});

try
{
    var svc = new BackupService(cfg);
    await svc.RunAsync(outDir, objects, 500, progress, CancellationToken.None);

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("=== BACKUP COMPLETE ===");
    Console.ResetColor();

    // Show output structure
    Console.WriteLine();
    Console.WriteLine("Output folder structure:");
    foreach (var entry in Directory.EnumerateFileSystemEntries(outDir, "*", SearchOption.AllDirectories)
                                   .Take(30))
    {
        var rel = Path.GetRelativePath(outDir, entry);
        var isDir = Directory.Exists(entry);
        Console.WriteLine($"  {(isDir ? "📁" : "  ")} {rel}");
    }

    var allFiles = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories);
    if (allFiles.Length > 30)
        Console.WriteLine($"  ... and {allFiles.Length - 30} more files");

    // Show manifest summary
    var manifestPath = Path.Combine(outDir, "app_install.config.json");
    if (File.Exists(manifestPath))
    {
        Console.WriteLine();
        Console.WriteLine("Manifest (app_install.config.json):");
        var manifest = InstallService.LoadManifest(outDir);
        var s = manifest.Summary;
        Console.WriteLine($"  Tables:{s.TotalTables}  Views:{s.TotalViews}  Procs:{s.TotalProcedures}  Functions:{s.TotalFunctions}  DataFiles:{s.TotalDataFiles}  TotalFiles:{s.TotalFiles}");
    }
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\n      BACKUP FAILED: {ex.Message}");
    if (ex.InnerException != null)
        Console.WriteLine($"      Inner: {ex.InnerException.Message}");
    Console.ResetColor();
}

Console.WriteLine();
Console.WriteLine("Press any key to exit...");
Console.ReadKey();
