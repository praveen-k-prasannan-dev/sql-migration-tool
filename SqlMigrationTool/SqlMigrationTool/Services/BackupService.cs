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

        // ── Phase 0: Prerequisites ───────────────────────────────────────────
        // Database-wide objects that schema objects depend on. Always exported
        // in full, whatever the object selection, because they are cheap and a
        // missing one makes every dependent CREATE fail.
        progress.Report(new BackupProgress("=== Phase 0: Prerequisites ==="));
        var phase0    = new ManifestPhase { Phase = 0, Name = "Prerequisites (Schemas, Types)" };
        var prereqDir = Path.Combine(outputFolder, "00_prerequisites");
        Directory.CreateDirectory(prereqDir);

        var prereqSteps = new PrereqStep[]
        {
            new(ObjectTypes.Schema, "schemas", "Schemas", _schema.ScriptSchemasAsync),
        };

        foreach (var step in prereqSteps)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new BackupProgress($"> Scripting {step.Label.ToLowerInvariant()}"));
            try
            {
                var script = await step.Script();
                if (script is null)
                {
                    progress.Report(new BackupProgress($"  - {step.Label}: none"));
                    continue;
                }

                var fileName = $"{orderCounter:D3}_{step.FileBase}.sql";
                await File.WriteAllTextAsync(Path.Combine(prereqDir, fileName), script, ct);
                phase0.Files.Add(new ManifestEntry
                {
                    Order  = orderCounter++,
                    Type   = step.Type,
                    Object = step.Label,
                    File   = $"00_prerequisites/{fileName}"
                });
                progress.Report(new BackupProgress($"✔ {step.Label} scripted", step.Type, true));
            }
            catch (Exception ex)
            {
                progress.Report(new BackupProgress($"✘ {step.Label}: {ex.Message}", step.Type, false));
            }
        }

        manifest.Phases.Add(phase0);

        // ── Phase 1: Schema ──────────────────────────────────────────────────
        progress.Report(new BackupProgress("=== Phase 1: Schema ==="));
        var phase1 = new ManifestPhase { Phase = 1, Name = "Schema (Tables, Views, Functions, Stored Procedures)" };

        var tables   = selectedObjects.Where(o => o.Type == ObjectTypes.Table).ToList();
        var views    = selectedObjects.Where(o => o.Type == ObjectTypes.View).ToList();
        var funcs    = selectedObjects.Where(o => o.Type == ObjectTypes.Func).ToList();
        var procs    = selectedObjects.Where(o => o.Type == ObjectTypes.Proc).ToList();
        var triggers = selectedObjects.Where(o => o.Type == ObjectTypes.Trigger).ToList();

        var schemaSteps = new SchemaStep[]
        {
            new(ObjectTypes.Table, "tables",            _schema.ScriptTableAsync),
            new(ObjectTypes.View,  "views",             _schema.ScriptViewAsync),
            new(ObjectTypes.Func,  "functions",         _schema.ScriptFuncAsync),
            new(ObjectTypes.Proc,  "stored_procedures", _schema.ScriptProcAsync),
        };

        foreach (var step in schemaSteps)
        {
            var stepDir = Path.Combine(outputFolder, "01_schema", step.Folder);
            Directory.CreateDirectory(stepDir);

            foreach (var obj in selectedObjects.Where(o => o.Type == step.Type))
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new BackupProgress($"> Scripting {step.Type} {obj.FullName}"));
                try
                {
                    var script   = await step.Script(obj);
                    var fileName = $"{orderCounter:D3}_{SanitiseName(obj.FullName)}.sql";
                    await File.WriteAllTextAsync(Path.Combine(stepDir, fileName), script, ct);
                    phase1.Files.Add(new ManifestEntry
                    {
                        Order  = orderCounter++,
                        Type   = step.Type,
                        Object = obj.FullName,
                        File   = $"01_schema/{step.Folder}/{fileName}"
                    });
                    progress.Report(new BackupProgress($"✔ {step.Type} {obj.FullName}", step.Type, true));
                }
                catch (Exception ex)
                {
                    progress.Report(new BackupProgress($"✘ {step.Type} {obj.FullName}: {ex.Message}", step.Type, false));
                }
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
                progress.Report(new BackupProgress($"  - DATA {obj.FullName} (0 rows, skipped)", ObjectTypes.Data, true));
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
                        Type   = ObjectTypes.Data,
                        Object = obj.FullName,
                        File   = $"02_data/{SanitiseName(obj.FullName)}/{fileName}",
                        Rows   = rowCount
                    });
                }

                progress.Report(new BackupProgress($"✔ DATA {obj.FullName} ({chunks.Count} chunks)", ObjectTypes.Data, true));
            }
            catch (Exception ex)
            {
                progress.Report(new BackupProgress($"✘ DATA {obj.FullName}: {ex.Message}", ObjectTypes.Data, false));
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
                Type   = ObjectTypes.Fk,
                Object = "Foreign Keys",
                File   = "03_relations/001_foreign_keys.sql"
            });
            progress.Report(new BackupProgress("✔ Foreign keys scripted", ObjectTypes.Fk, true));
        }
        catch (Exception ex)
        {
            progress.Report(new BackupProgress($"✘ Foreign keys: {ex.Message}", ObjectTypes.Fk, false));
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
                Type   = ObjectTypes.Index,
                Object = "Indexes",
                File   = "03_relations/002_indexes.sql"
            });
            progress.Report(new BackupProgress("✔ Indexes scripted", ObjectTypes.Index, true));
        }
        catch (Exception ex)
        {
            progress.Report(new BackupProgress($"✘ Indexes: {ex.Message}", ObjectTypes.Index, false));
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
                    Type   = ObjectTypes.Trigger,
                    Object = "Triggers",
                    File   = "03_relations/003_triggers.sql"
                });
                progress.Report(new BackupProgress($"✔ {triggers.Count} trigger(s) scripted", ObjectTypes.Trigger, true));
            }
            catch (Exception ex)
            {
                progress.Report(new BackupProgress($"✘ Triggers: {ex.Message}", ObjectTypes.Trigger, false));
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

    private sealed record PrereqStep(string Type, string FileBase, string Label, Func<Task<string?>> Script);

    private sealed record SchemaStep(string Type, string Folder, Func<DatabaseObject, Task<string>> Script);

    private static string SanitiseName(string name)
        => name.Replace('.', '_').Replace('[', '_').Replace(']', '_')
               .Replace(' ', '_').Trim('_');
}
