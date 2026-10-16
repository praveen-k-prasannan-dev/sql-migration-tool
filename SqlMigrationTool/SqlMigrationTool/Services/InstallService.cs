using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlMigrationTool.Models;

namespace SqlMigrationTool.Services;

public class InstallService
{
    private readonly ConnectionConfig _cfg;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public InstallService(ConnectionConfig cfg) => _cfg = cfg;

    public async Task RunAsync(
        string                     folder,
        InstallManifest            manifest,
        IProgress<InstallProgress> progress,
        CancellationToken          ct,
        ScriptMode?                mode     = null,
        DataLoadMode               dataMode = DataLoadMode.SkipIfNotEmpty)
    {
        var logger = new ErrorLogger(folder);
        var loader = new DataLoader(_cfg, dataMode);

        // A mode other than the one the backup was written with: scripts are
        // re-wrapped in memory before running; the backup files stay as they are.
        _rewrapTo = mode is { } m && m != ManifestMode(manifest) ? m : null;

        void Report(ManifestEntry entry, List<string> warnings)
        {
            if (warnings.Count == 0)
            {
                progress.Report(new InstallProgress(entry, InstallStatus.Done));
                return;
            }
            logger.LogWarnings(entry, warnings);
            var more = warnings.Count > 1 ? $" (+{warnings.Count - 1} more, see error.log)" : "";
            progress.Report(new InstallProgress(entry, InstallStatus.Warning, warnings[0] + more));
        }

        foreach (var phase in manifest.Phases)
        {
            var dataEntries = phase.Files.Where(f => f.Type == ObjectTypes.Data).ToList();
            if (dataEntries.Count > 0)
                await loader.BeginPhaseAsync(dataEntries.Select(f => f.Object), ct);

            try
            {
                foreach (var entry in phase.Files)
                {
                    ct.ThrowIfCancellationRequested();

                    progress.Report(new InstallProgress(entry, InstallStatus.Running));
                    try
                    {
                        var fullPath = Path.Combine(folder,
                            entry.File.Replace('/', Path.DirectorySeparatorChar));

                        Report(entry, entry.Type == ObjectTypes.Data
                            ? await loader.LoadChunkAsync(fullPath, ct)
                            : await ExecuteSqlFileAsync(fullPath, ct));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        logger.Log(entry, ex);
                        progress.Report(new InstallProgress(entry, InstallStatus.Error, ex.Message));
                    }
                }
            }
            finally
            {
                // Foreign keys and triggers switched off for the load come back on,
                // even after a cancel; problems are reported on the last data file.
                if (dataEntries.Count > 0)
                {
                    var problems = await loader.EndPhaseAsync();
                    if (problems.Count > 0)
                        Report(dataEntries[^1], problems);
                }
            }
        }

        logger.WriteSummary();
    }

    /// <summary>The script mode a backup was written with (DropAndCreate for v1 backups).</summary>
    public static ScriptMode ManifestMode(InstallManifest manifest) =>
        Enum.TryParse<ScriptMode>(manifest.ScriptMode, ignoreCase: true, out var m) ? m : ScriptMode.DropAndCreate;

    private ScriptMode? _rewrapTo;

    /// <summary>Runs a script; returns the warnings it raised (RAISERROR severity <= 10).</summary>
    private async Task<List<string>> ExecuteSqlFileAsync(string filePath, CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(filePath, ct);
        if (_rewrapTo is { } mode && ScriptWrapper.IsWrapped(content))
            content = ScriptWrapper.Rewrap(content, mode);

        // Split on GO batch separator (SQL Server Management Studio directive)
        var batches = content.Split(
            new[] { "\nGO\n", "\nGO\r\n", "\r\nGO\r\n", "\r\nGO\n", "\nGO", "\r\nGO" },
            StringSplitOptions.RemoveEmptyEntries);

        await using var conn = await SqlConnectionService.OpenAsync(_cfg);

        // Informational RAISERRORs from generated scripts (number 50000) are warnings,
        // e.g. CreateOrAlter column differences. Server chatter has other numbers.
        var warnings = new List<string>();
        conn.InfoMessage += (_, e) =>
        {
            foreach (SqlError err in e.Errors)
                if (err.Number == 50000) warnings.Add(err.Message);
        };

        // Files such as indexes.sql hold many independent objects: a failing batch
        // must not stop the rest. Errors are collected and reported together.
        var errors = new List<string>();
        int executed = 0;
        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();
            var trimmed = batch.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            executed++;
            await using var cmd = new SqlCommand(trimmed, conn)
            {
                CommandTimeout = 300
            };
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException ex)
            {
                errors.Add(ex.Message);
            }
        }

        if (errors.Count == 1)
            throw new InvalidOperationException(errors[0]);
        if (errors.Count > 1)
            throw new InvalidOperationException(
                $"{errors.Count} of {executed} statements failed: " + string.Join(" | ", errors));

        return warnings;
    }

    public static InstallManifest LoadManifest(string folder)
    {
        var path = Path.Combine(folder, "app_install.config.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("app_install.config.json not found in the selected folder.", path);

        var json = File.ReadAllText(path);
        var manifest = JsonSerializer.Deserialize<InstallManifest>(json, _jsonOptions)
            ?? throw new InvalidDataException("Failed to parse app_install.config.json");

        // Newer manifests may contain object types or phases this build cannot install correctly.
        var major = int.TryParse(manifest.Version.Split('.')[0], out var m) ? m : 0;
        if (major < 1 || major > InstallManifest.CurrentMajorVersion)
            throw new InvalidDataException(
                $"This backup uses manifest version {manifest.Version}, which this version of SQL Migration Tool " +
                $"cannot install (supported: 1.x - {InstallManifest.CurrentMajorVersion}.x). Please update the tool.");

        return manifest;
    }
}
