using SqlMigrationTool.Models;
using SqlMigrationTool.Services;

namespace TestConsole;

public static class Commands
{
    public static async Task<int> SetupAsync(Options o)
    {
        var script = o.ScriptPath ?? TestDatabase.FindDefaultScript();
        Out.Step($"Recreating {o.SourceDb} from {Path.GetFileName(script)}");
        await TestDatabase.RecreateAsync(o, o.SourceDb);
        await TestDatabase.RunScriptAsync(o, o.SourceDb, script);
        Out.Ok("Source database ready");
        return 0;
    }

    public static async Task<int> RoundTripAsync(Options o)
    {
        if (!o.SkipSetup)
            await SetupAsync(o);

        Out.Step($"Recreating empty target {o.TargetDb}");
        await TestDatabase.RecreateAsync(o, o.TargetDb);

        // ── Backup ──────────────────────────────────────────────────────────
        var source  = o.Config(o.SourceDb);
        var objects = await new SchemaExporter(source).ListAllObjectsAsync();
        Out.Step($"Backing up {objects.Count} objects "
               + $"({string.Join(", ", objects.GroupBy(x => x.Type).Select(g => $"{g.Key} {g.Count()}"))})");

        var folder = Path.Combine(Path.GetTempPath(), "SqlMigTest", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        int backupFailures = 0;
        var backupProgress = new SyncProgress<BackupProgress>(p =>
        {
            if (p.Success == false) { backupFailures++; Out.Error("  " + p.Message); }
            else if (o.Verbose)     Out.Info("  " + p.Message);
        });
        await new BackupService(source).RunAsync(folder, objects, o.ChunkSize, backupProgress, CancellationToken.None, o.Mode, o.Cleanup);
        Out.Info($"  Script mode: {o.Mode}");
        Out.Info($"  Backup folder: {folder}");

        var installErrors = await InstallCoreAsync(o, folder);
        if (o.Reinstall)
        {
            Out.Step("Re-installing over the populated target");
            installErrors += await InstallCoreAsync(o, folder);
        }

        // ── Compare ─────────────────────────────────────────────────────────
        var differences = await CompareCoreAsync(o);

        Console.WriteLine();
        Out.Step("Round-trip result");
        Report("Backup failures", backupFailures);
        Report("Install errors",  installErrors);
        Report("Differences",     differences);
        return backupFailures + installErrors + differences == 0 ? 0 : 1;
    }

    /// <summary>Installs an existing backup folder into a freshly recreated target.</summary>
    public static async Task<int> InstallAsync(Options o)
    {
        if (o.Folder is null) throw new ArgumentException("install needs --folder <backup folder>");

        if (!o.KeepTarget)
        {
            Out.Step($"Recreating empty target {o.TargetDb}");
            await TestDatabase.RecreateAsync(o, o.TargetDb);
        }
        var installErrors = await InstallCoreAsync(o, o.Folder);
        Report("Install errors", installErrors);
        return installErrors == 0 ? 0 : 1;
    }

    private static async Task<int> InstallCoreAsync(Options o, string folder)
    {
        Out.Step($"Installing into {o.TargetDb}");
        int installErrors = 0;
        var installProgress = new SyncProgress<InstallProgress>(p =>
        {
            if (p.Status == InstallStatus.Error)
            {
                installErrors++;
                Out.Error($"  {p.Entry.Type} {p.Entry.Object} ({p.Entry.File}): {p.Message}");
            }
            else if (p.Status == InstallStatus.Warning)
                Out.Warn($"  ! {p.Entry.Type} {p.Entry.Object}: {p.Message}");
            else if (o.Verbose && p.Status == InstallStatus.Done)
                Out.Info($"  ✔ {p.Entry.Type} {p.Entry.Object}");
        });
        var manifest = InstallService.LoadManifest(folder);
        Out.Info($"  Manifest version {manifest.Version}");
        await new InstallService(o.Config(o.TargetDb)).RunAsync(folder, manifest, installProgress, CancellationToken.None, o.InstallMode, o.DataMode);
        return installErrors;
    }

    /// <summary>
    /// Backs the source up once per script mode, then re-wraps every script of each
    /// backup into every other mode and requires the result to equal the script that
    /// backup in that mode produced directly.
    /// </summary>
    public static async Task<int> RewrapCheckAsync(Options o)
    {
        var source  = o.Config(o.SourceDb);
        var objects = await new SchemaExporter(source).ListAllObjectsAsync();
        var root    = Path.Combine(Path.GetTempPath(), "SqlMigTest", "rewrap_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        var modes   = Enum.GetValues<ScriptMode>();
        var quiet   = new SyncProgress<BackupProgress>(_ => { });

        foreach (var mode in modes)
            await new BackupService(source).RunAsync(Path.Combine(root, mode.ToString()), objects, o.ChunkSize, quiet, CancellationToken.None, mode);

        int checkedFiles = 0, failures = 0;
        foreach (var from in modes)
        foreach (var to in modes.Where(m => m != from))
        {
            var fromDir = Path.Combine(root, from.ToString());
            foreach (var file in Directory.GetFiles(fromDir, "*.sql", SearchOption.AllDirectories))
            {
                var text = await File.ReadAllTextAsync(file);
                if (!ScriptWrapper.IsWrapped(text)) continue;

                var expected = await File.ReadAllTextAsync(Path.Combine(root, to.ToString(), Path.GetRelativePath(fromDir, file)));
                checkedFiles++;
                if (ScriptWrapper.Rewrap(text, to) != expected)
                {
                    failures++;
                    Out.Error($"  {from} -> {to}: {Path.GetRelativePath(fromDir, file)} differs");
                }
            }
        }

        Out.Info($"  Re-wrapped files checked: {checkedFiles}");
        Report("Differing", failures);
        Out.Info($"  Backups: {root}");
        return failures == 0 && checkedFiles > 0 ? 0 : 1;
    }

    public static async Task<int> CompareAsync(Options o)
        => await CompareCoreAsync(o) == 0 ? 0 : 1;

    private static async Task<int> CompareCoreAsync(Options o)
    {
        Out.Step($"Comparing {o.SourceDb} → {o.TargetDb}");
        var src = await DbSnapshot.CaptureAsync(o.Config(o.SourceDb));
        var tgt = await DbSnapshot.CaptureAsync(o.Config(o.TargetDb));
        var diffs = DbSnapshot.Compare(src, tgt);

        foreach (var d in diffs.Where(d => d.Differences > 0))
        {
            Console.WriteLine();
            Out.Warn($"[{d.Category}]  source {d.SourceCount} / target {d.TargetCount}");
            foreach (var line in d.Missing) Out.Error($"  - {line}");
            foreach (var line in d.Extra)   Out.Info ($"  + {line}");
        }

        Console.WriteLine();
        Console.WriteLine($"  {"Category",-24}{"Source",8}{"Target",8}{"Diffs",8}");
        foreach (var d in diffs)
        {
            var row = $"  {d.Category,-24}{d.SourceCount,8}{d.TargetCount,8}{d.Differences,8}";
            if (d.Differences == 0) Out.Ok(row); else Out.Error(row);
        }
        Console.WriteLine("  (- missing from target, + only in target / different)");

        return diffs.Sum(d => d.Differences);
    }

    private static void Report(string label, int count)
    {
        var line = $"  {label,-18}{count}";
        if (count == 0) Out.Ok(line); else Out.Error(line);
    }
}

/// <summary>IProgress that reports synchronously, so console output stays in order.</summary>
public sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

public static class Out
{
    public static void Step (string s) => Write(ConsoleColor.Cyan,     "» " + s);
    public static void Ok   (string s) => Write(ConsoleColor.Green,    s);
    public static void Info (string s) => Write(ConsoleColor.Gray,     s);
    public static void Warn (string s) => Write(ConsoleColor.Yellow,   s);
    public static void Error(string s) => Write(ConsoleColor.Red,      s);

    private static void Write(ConsoleColor color, string s)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(s);
        Console.ResetColor();
    }
}
