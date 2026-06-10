using System.Text.Json;
using SqlMigrationTool.Models;

namespace SqlMigrationTool.Services;

public class BackupService
{
    private readonly ConnectionConfig _cfg;
    private readonly SchemaExporter   _schema;
    private readonly DataExporter     _data;

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public BackupService(ConnectionConfig cfg)
    {
        _cfg    = cfg;
        _schema = new SchemaExporter(cfg);
        _data   = new DataExporter(cfg);
    }

    public async Task RunAsync(
        string                    outputFolder,
        List<DatabaseObject>      selectedObjects,
        int                       chunkSize,
        IProgress<BackupProgress> progress,
        CancellationToken         ct)
    {
        Directory.CreateDirectory(outputFolder);

        var manifest = new InstallManifest
        {
            SourceServer          = _cfg.Server,
            SourceDatabase        = _cfg.Database,
            SqlCompatibilityLevel = await _schema.GetCompatibilityLevelAsync()
        };

        int orderCounter = 1;

        // ── Phase 1: Schema ──────────────────────────────────────────────────
        progress.Report(new BackupProgress("=== Phase 1: Schema ==="));
        var phase1 = new ManifestPhase { Phase = 1, Name = "Schema (Tables, Views, Functions, Stored Procedures)" };

        var tables   = selectedObjects.Where(o => o.Type == "TABLE").ToList();
        var views    = selectedObjects.Where(o => o.Type == "VIEW").ToList();
        var funcs    = selectedObjects.Where(o => o.Type == "FUNC").ToList();
        var procs    = selectedObjects.Where(o => o.Type == "PROC").ToList();
        var triggers = selectedObjects.Where(o => o.Type == "TRIGGER").ToList();

        // Tables
        var tablesDir = Path.Combine(outputFolder, "01_schema", "tables");
        Directory.CreateDirectory(tablesDir);
        foreach (var obj in tables)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new BackupProgress($"> Scripting TABLE {obj.FullName}"));
            try
            {
                var script   = await _schema.ScriptTableAsync(obj);
                var fileName = $"{orderCounter:D3}_{SanitiseName(obj.FullName)}.sql";
                await File.WriteAllTextAsync(Path.Combine(tablesDir, fileName), script, ct);
                phase1.Files.Add(new ManifestEntry
                {
                    Order  = orderCounter++,
                    Type   = "TABLE",
                    Object = obj.FullName,
                    File   = $"01_schema/tables/{fileName}"
                });
                progress.Report(new BackupProgress($"✔ TABLE {obj.FullName}", "TABLE", true));
            }
            catch (Exception ex)
            {
                progress.Report(new BackupProgress($"✘ TABLE {obj.FullName}: {ex.Message}", "TABLE", false));
            }
        }

        // Views
        var viewsDir = Path.Combine(outputFolder, "01_schema", "views");
        Directory.CreateDirectory(viewsDir);
        foreach (var obj in views)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new BackupProgress($"> Scripting VIEW {obj.FullName}"));
            try
            {
                var script   = await _schema.ScriptViewAsync(obj);
                var fileName = $"{orderCounter:D3}_{SanitiseName(obj.FullName)}.sql";
                await File.WriteAllTextAsync(Path.Combine(viewsDir, fileName), script, ct);
                phase1.Files.Add(new ManifestEntry
                {
                    Order  = orderCounter++,
                    Type   = "VIEW",
                    Object = obj.FullName,
                    File   = $"01_schema/views/{fileName}"
                });
                progress.Report(new BackupProgress($"✔ VIEW {obj.FullName}", "VIEW", true));
            }
            catch (Exception ex)
            {
                progress.Report(new BackupProgress($"✘ VIEW {obj.FullName}: {ex.Message}", "VIEW", false));
            }
        }

        // Functions
        var funcsDir = Path.Combine(outputFolder, "01_schema", "functions");
        Directory.CreateDirectory(funcsDir);
        foreach (var obj in funcs)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new BackupProgress($"> Scripting FUNC {obj.FullName}"));
            try
            {
                var script   = await _schema.ScriptFuncAsync(obj);
                var fileName = $"{orderCounter:D3}_{SanitiseName(obj.FullName)}.sql";
                await File.WriteAllTextAsync(Path.Combine(funcsDir, fileName), script, ct);
                phase1.Files.Add(new ManifestEntry
                {
                    Order  = orderCounter++,
                    Type   = "FUNC",
                    Object = obj.FullName,
                    File   = $"01_schema/functions/{fileName}"
                });
                progress.Report(new BackupProgress($"✔ FUNC {obj.FullName}", "FUNC", true));
            }
            catch (Exception ex)
            {
                progress.Report(new BackupProgress($"✘ FUNC {obj.FullName}: {ex.Message}", "FUNC", false));
            }
        }

        // Stored Procedures
        var procsDir = Path.Combine(outputFolder, "01_schema", "stored_procedures");
        Directory.CreateDirectory(procsDir);
        foreach (var obj in procs)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new BackupProgress($"> Scripting PROC {obj.FullName}"));
            try
            {
                var script   = await _schema.ScriptProcAsync(obj);
                var fileName = $"{orderCounter:D3}_{SanitiseName(obj.FullName)}.sql";
                await File.WriteAllTextAsync(Path.Combine(procsDir, fileName), script, ct);
                phase1.Files.Add(new ManifestEntry
                {
                    Order  = orderCounter++,
                    Type   = "PROC",
                    Object = obj.FullName,
                    File   = $"01_schema/stored_procedures/{fileName}"
                });
                progress.Report(new BackupProgress($"✔ PROC {obj.FullName}", "PROC", true));
            }
            catch (Exception ex)
            {
                progress.Report(new BackupProgress($"✘ PROC {obj.FullName}: {ex.Message}", "PROC", false));
            }
        }

        manifest.Phases.Add(phase1);

        // ── Phase 2: Data ────────────────────────────────────────────────────
        progress.Report(new BackupProgress("=== Phase 2: Data ==="));
        var phase2 = new ManifestPhase { Phase = 2, Name = "Table Data" };

        foreach (var obj in tables)
        {
            ct.ThrowIfCancellationRequested();

            if (obj.RowCount == 0)
            {
                progress.Report(new BackupProgress($"  - DATA {obj.FullName} (0 rows, skipped)", "DATA", true));
                continue;
            }

            progress.Report(new BackupProgress($"> Exporting data: {obj.FullName} ({obj.RowCount:N0} rows)"));
            try
            {
                var tableFolder = Path.Combine(outputFolder, "02_data", SanitiseName(obj.FullName));
                var strProg     = new Progress<string>(msg => progress.Report(new BackupProgress(msg)));
                var chunks      = await _data.ExportTableAsync(obj, tableFolder, chunkSize, strProg, ct);

                foreach (var (_, rowCount) in chunks)
                {
                    var chunkIdx = phase2.Files.Count(f => f.Object == obj.FullName) + 1;
                    var fileName = $"data_{chunkIdx:D3}.json";
                    phase2.Files.Add(new ManifestEntry
                    {
                        Order  = orderCounter++,
                        Type   = "DATA",
                        Object = obj.FullName,
                        File   = $"02_data/{SanitiseName(obj.FullName)}/{fileName}",
                        Rows   = rowCount
                    });
                }

                progress.Report(new BackupProgress($"✔ DATA {obj.FullName} ({chunks.Count} chunks)", "DATA", true));
            }
            catch (Exception ex)
            {
                progress.Report(new BackupProgress($"✘ DATA {obj.FullName}: {ex.Message}", "DATA", false));
            }
        }

        manifest.Phases.Add(phase2);

        // ── Phase 3: Relations ───────────────────────────────────────────────
        progress.Report(new BackupProgress("=== Phase 3: Relations ==="));
        var phase3 = new ManifestPhase { Phase = 3, Name = "Relations, Indexes, Triggers" };
        var relDir = Path.Combine(outputFolder, "03_relations");
        Directory.CreateDirectory(relDir);

        // Foreign Keys
        progress.Report(new BackupProgress("> Scripting foreign keys"));
        try
        {
            var fkScript = await _schema.ScriptForeignKeysAsync();
            await File.WriteAllTextAsync(Path.Combine(relDir, "001_foreign_keys.sql"), fkScript, ct);
            phase3.Files.Add(new ManifestEntry
            {
                Order  = orderCounter++,
                Type   = "FK",
                Object = "Foreign Keys",
                File   = "03_relations/001_foreign_keys.sql"
            });
            progress.Report(new BackupProgress("✔ Foreign keys scripted", "FK", true));
        }
        catch (Exception ex)
        {
            progress.Report(new BackupProgress($"✘ Foreign keys: {ex.Message}", "FK", false));
        }

        // Indexes
        progress.Report(new BackupProgress("> Scripting indexes"));
        try
        {
            var idxScript = await _schema.ScriptIndexesAsync();
            await File.WriteAllTextAsync(Path.Combine(relDir, "002_indexes.sql"), idxScript, ct);
            phase3.Files.Add(new ManifestEntry
            {
                Order  = orderCounter++,
                Type   = "INDEX",
                Object = "Indexes",
                File   = "03_relations/002_indexes.sql"
            });
            progress.Report(new BackupProgress("✔ Indexes scripted", "INDEX", true));
        }
        catch (Exception ex)
        {
            progress.Report(new BackupProgress($"✘ Indexes: {ex.Message}", "INDEX", false));
        }

        // Triggers
        if (triggers.Count > 0)
        {
            progress.Report(new BackupProgress("> Scripting triggers"));
            try
            {
                var trigSb = new System.Text.StringBuilder();
                trigSb.AppendLine("-- Triggers");
                trigSb.AppendLine();
                foreach (var trig in triggers)
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Report(new BackupProgress($"  > Scripting TRIGGER {trig.FullName}"));
                    trigSb.AppendLine(await _schema.ScriptTriggerAsync(trig));
                }
                await File.WriteAllTextAsync(Path.Combine(relDir, "003_triggers.sql"), trigSb.ToString(), ct);
                phase3.Files.Add(new ManifestEntry
                {
                    Order  = orderCounter++,
                    Type   = "TRIGGER",
                    Object = "Triggers",
                    File   = "03_relations/003_triggers.sql"
                });
                progress.Report(new BackupProgress($"✔ {triggers.Count} trigger(s) scripted", "TRIGGER", true));
            }
            catch (Exception ex)
            {
                progress.Report(new BackupProgress($"✘ Triggers: {ex.Message}", "TRIGGER", false));
            }
        }

        manifest.Phases.Add(phase3);

        // ── Manifest ─────────────────────────────────────────────────────────
        manifest.Summary = new ManifestSummary
        {
            TotalTables     = tables.Count,
            TotalViews      = views.Count,
            TotalProcedures = procs.Count,
            TotalFunctions  = funcs.Count,
            TotalDataFiles  = phase2.Files.Count,
            TotalFiles      = manifest.Phases.Sum(p => p.Files.Count)
        };

        var manifestPath = Path.Combine(outputFolder, "app_install.config.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, _jsonOptions), ct);

        progress.Report(new BackupProgress($"✔ Backup complete. {manifest.Summary.TotalFiles} files written."));
        progress.Report(new BackupProgress($"  Manifest: {manifestPath}"));
    }

    private static string SanitiseName(string name)
        => name.Replace('.', '_').Replace('[', '_').Replace(']', '_')
               .Replace(' ', '_').Trim('_');
}
