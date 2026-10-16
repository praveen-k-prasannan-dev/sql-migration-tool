using System.Text.Json;
using SqlMigrationTool.Models;

namespace SqlMigrationTool.Services;

public class BackupService
{
    private readonly ConnectionConfig _cfg;
    private readonly SchemaExporter   _schema;
    private readonly DataExporter     _data;

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private int _order;   // manifest order and file number, shared by all phases

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
            Version               = InstallManifest.CurrentVersion,
            SourceServer          = _cfg.Server,
            SourceDatabase        = _cfg.Database,
            SqlCompatibilityLevel = await _schema.GetCompatibilityLevelAsync(),
            DatabaseCollation     = await _schema.GetDatabaseCollationAsync()
        };

        _order = 1;

        // ── Phase 0: Prerequisites ───────────────────────────────────────────
        // Database-wide objects that schema objects depend on. Always exported
        // in full, whatever the object selection, because they are cheap and a
        // missing one makes every dependent CREATE fail.
        progress.Report(new BackupProgress("=== Phase 0: Prerequisites ==="));
        var phase0 = new ManifestPhase { Phase = 0, Name = "Prerequisites (Schemas, Types, Sequences)" };

        await RunScriptStepsAsync(phase0, outputFolder, "00_prerequisites", new ScriptStep[]
        {
            new(ObjectTypes.Schema,    "schemas",                  "Schemas",                  _schema.ScriptSchemasAsync),
            new(ObjectTypes.DataType,  "user_defined_data_types",  "User-Defined Data Types",  _schema.ScriptDataTypesAsync),
            new(ObjectTypes.TableType, "user_defined_table_types", "User-Defined Table Types", _schema.ScriptTableTypesAsync),
            new(ObjectTypes.Sequence,  "sequences",                "Sequences",                _schema.ScriptSequencesAsync),
        }, progress, ct);

        manifest.Phases.Add(phase0);

        // ── Phase 1: Schema ──────────────────────────────────────────────────
        progress.Report(new BackupProgress("=== Phase 1: Schema ==="));
        var phase1 = new ManifestPhase { Phase = 1, Name = "Schema (Tables, Views, Functions, Stored Procedures)" };

        var tables   = selectedObjects.Where(o => o.Type == ObjectTypes.Table).ToList();
        var views    = selectedObjects.Where(o => o.Type == ObjectTypes.View).ToList();
        var funcs    = selectedObjects.Where(o => o.Type == ObjectTypes.Func).ToList();
        var procs    = selectedObjects.Where(o => o.Type == ObjectTypes.Proc).ToList();
        var triggers = selectedObjects.Where(o => o.Type == ObjectTypes.Trigger).ToList();

        // Views, functions and procedures share one folder, written in dependency
        // order so each object is created after the objects it references.
        var programmable = selectedObjects
            .Where(o => o.Type is ObjectTypes.View or ObjectTypes.Func or ObjectTypes.Proc)
            .ToList();
        var (orderedProgrammable, cyclic) = await DependencyResolver.OrderAsync(_cfg, programmable);
        foreach (var obj in cyclic)
            progress.Report(new BackupProgress($"  ! {obj.Type} {obj.FullName} is part of a dependency cycle; it may need a second install run"));

        var schemaSteps = new SchemaStep[]
        {
            new("tables",          tables),
            new("programmability", orderedProgrammable),
        };

        foreach (var step in schemaSteps)
        {
            var stepDir = Path.Combine(outputFolder, "01_schema", step.Folder);
            Directory.CreateDirectory(stepDir);

            foreach (var obj in step.Objects)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new BackupProgress($"> Scripting {obj.Type} {obj.FullName}"));
                try
                {
                    var script   = await ScriptObjectAsync(obj);
                    var fileName = $"{_order:D3}_{SanitiseName(obj.FullName)}.sql";
                    await File.WriteAllTextAsync(Path.Combine(stepDir, fileName), script, ct);
                    phase1.Files.Add(new ManifestEntry
                    {
                        Order  = _order++,
                        Type   = obj.Type,
                        Object = obj.FullName,
                        File   = $"01_schema/{step.Folder}/{fileName}"
                    });
                    progress.Report(new BackupProgress($"✔ {obj.Type} {obj.FullName}", obj.Type, true));
                }
                catch (Exception ex)
                {
                    progress.Report(new BackupProgress($"✘ {obj.Type} {obj.FullName}: {ex.Message}", obj.Type, false));
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
                        Order  = _order++,
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

        await RunScriptStepsAsync(phase3, outputFolder, "03_relations", new ScriptStep[]
        {
            new(ObjectTypes.Fk,       "foreign_keys",     "Foreign keys",       async () => await _schema.ScriptForeignKeysAsync()),
            new(ObjectTypes.Index,    "indexes",          "Indexes",            async () => await _schema.ScriptIndexesAsync()),
            new(ObjectTypes.Trigger,  "triggers",         "Triggers",           () => ScriptTriggersAsync(triggers, progress, ct)),
            new(ObjectTypes.Sequence, "sequence_restart", "Sequence positions", _schema.ScriptSequenceRestartsAsync),
        }, progress, ct);

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

    /// <summary>
    /// Runs each step's scripter and writes its output to {folder}/NNN_{FileBase}.sql
    /// with a manifest entry. A step whose scripter returns null has nothing to
    /// export and is skipped; a failing step is reported and the backup continues.
    /// </summary>
    private async Task RunScriptStepsAsync(
        ManifestPhase phase, string outputFolder, string folder, IEnumerable<ScriptStep> steps,
        IProgress<BackupProgress> progress, CancellationToken ct)
    {
        var dir = Path.Combine(outputFolder, folder);
        Directory.CreateDirectory(dir);

        foreach (var step in steps)
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

                var fileName = $"{_order:D3}_{step.FileBase}.sql";
                await File.WriteAllTextAsync(Path.Combine(dir, fileName), script, ct);
                phase.Files.Add(new ManifestEntry
                {
                    Order  = _order++,
                    Type   = step.Type,
                    Object = step.Label,
                    File   = $"{folder}/{fileName}"
                });
                progress.Report(new BackupProgress($"✔ {step.Label} scripted", step.Type, true));
            }
            catch (Exception ex)
            {
                progress.Report(new BackupProgress($"✘ {step.Label}: {ex.Message}", step.Type, false));
            }
        }
    }

    private async Task<string?> ScriptTriggersAsync(
        List<DatabaseObject> triggers, IProgress<BackupProgress> progress, CancellationToken ct)
    {
        if (triggers.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("-- Triggers");
        sb.AppendLine();
        foreach (var trig in triggers)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new BackupProgress($"  > Scripting TRIGGER {trig.FullName}"));
            sb.AppendLine(await _schema.ScriptTriggerAsync(trig));
        }
        return sb.ToString();
    }

    private sealed record ScriptStep(string Type, string FileBase, string Label, Func<Task<string?>> Script);

    private sealed record SchemaStep(string Folder, List<DatabaseObject> Objects);

    private Task<string> ScriptObjectAsync(DatabaseObject obj) => obj.Type switch
    {
        ObjectTypes.Table => _schema.ScriptTableAsync(obj),
        ObjectTypes.View  => _schema.ScriptViewAsync(obj),
        ObjectTypes.Func  => _schema.ScriptFuncAsync(obj),
        ObjectTypes.Proc  => _schema.ScriptProcAsync(obj),
        _ => throw new NotSupportedException($"No scripter for object type {obj.Type}")
    };

    private static string SanitiseName(string name)
        => name.Replace('.', '_').Replace('[', '_').Replace(']', '_')
               .Replace(' ', '_').Trim('_');
}
