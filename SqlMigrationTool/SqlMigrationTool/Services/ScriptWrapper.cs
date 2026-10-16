using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlMigrationTool.Models;

namespace SqlMigrationTool.Services;

/// <summary>Describes a scripted object; serialised into the script so the installer can re-wrap it.</summary>
public sealed record ScriptObjectInfo
{
    public string  Type          { get; init; } = "";   // ObjectTypes value
    public string  Schema        { get; init; } = "";
    public string  Name          { get; init; } = "";
    public string  SysType       { get; init; } = "";   // sys.objects.type: V, P, FN, IF, TF, TR ...
    public string? ParentSchema  { get; init; }          // triggers: the table or view
    public string? ParentName    { get; init; }
    public string? ParentSysType { get; init; }
    public bool    Disabled      { get; init; }          // triggers disabled in the source

    // Tables
    public List<ColumnInfo>? Columns         { get; init; }
    public List<string>?     UncheckedChecks { get; init; }   // CHECKs to switch off after CREATE TABLE

    [System.Text.Json.Serialization.JsonIgnore]
    public string QuotedName => SqlNames.Quote(Schema, Name);
}

/// <summary>A table column: its scripted definition and the metadata used to detect differences.</summary>
public sealed record ColumnInfo
{
    public string Name       { get; init; } = "";
    public string Definition { get; init; } = "";
    public string TypeName   { get; init; } = "";   // sys.types name (alias types without schema)
    public int    MaxLength  { get; init; }
    public int    Precision  { get; init; }
    public int    Scale      { get; init; }
    public bool   Nullable   { get; init; }
    public bool   Computed   { get; init; }
}

/// <summary>
/// Wraps an object's plain CREATE statement for a <see cref="ScriptMode"/>.
/// Each wrapped object is laid out as
/// <code>
/// -- &lt;&lt;SMT:OBJECT {json}&gt;&gt;
/// ...mode guard...
/// -- &lt;&lt;SMT:BODY&gt;&gt;
/// GO
/// CREATE ...            (ALTER ... in CreateOrAlter mode)
/// GO
/// -- &lt;&lt;SMT:BODY END&gt;&gt;
/// ...post statements, mode epilogue...
/// -- &lt;&lt;SMT:END&gt;&gt;
/// </code>
/// so the file runs as-is in SSMS, and <see cref="Rewrap"/> can rebuild it for another mode.
/// </summary>
public static class ScriptWrapper
{
    private const string ObjectMarker  = "-- <<SMT:OBJECT ";
    private const string BodyMarker    = "-- <<SMT:BODY>>";
    private const string BodyEndMarker = "-- <<SMT:BODY END>>";
    private const string EndMarker     = "-- <<SMT:END>>";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    // Leading CREATE / ALTER / CREATE OR ALTER keyword of a module definition.
    private static readonly Regex LeadingKeyword =
        new(@"\G(CREATE\s+OR\s+ALTER|CREATE|ALTER)\b", RegexOptions.IgnoreCase);

    /// <summary>Wraps a table or module for <paramref name="mode"/>.</summary>
    public static string Wrap(ScriptObjectInfo info, string createBody, ScriptMode mode) =>
        info.SysType == "U" ? WrapTable(info, createBody, mode) : WrapModule(info, createBody, mode);

    // ── Tables ──────────────────────────────────────────────────────────────

    private static string WrapTable(ScriptObjectInfo info, string createBody, ScriptMode mode)
    {
        var name = info.QuotedName;
        var lit  = SqlNames.Literal(name);
        var sb   = new StringBuilder();

        sb.AppendLine($"-- {info.Type}: {info.Schema}.{info.Name}");
        sb.AppendLine($"{ObjectMarker}{JsonSerializer.Serialize(info, Json)}>>");

        if (mode == ScriptMode.DropAndCreate)
        {
            // Remove what would block DROP TABLE. Both come back from this backup:
            // foreign keys in phase 3, schema-bound views/functions in phase 1.
            sb.AppendLine($"IF OBJECT_ID({lit}, 'U') IS NOT NULL");
            sb.AppendLine("BEGIN");
            sb.AppendLine("    DECLARE @drop nvarchar(max) = N'';");
            sb.AppendLine("    SELECT @drop += N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(fk.parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(fk.parent_object_id))");
            sb.AppendLine("                  + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'");
            sb.AppendLine("    FROM sys.foreign_keys fk");
            sb.AppendLine($"    WHERE fk.referenced_object_id = OBJECT_ID({lit}) AND fk.parent_object_id <> fk.referenced_object_id;");
            sb.AppendLine("    SELECT @drop += N'DROP ' + CASE WHEN o.type = 'V' THEN N'VIEW ' ELSE N'FUNCTION ' END");
            sb.AppendLine("                  + QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name) + N';'");
            sb.AppendLine("    FROM sys.objects o");   // one row per object: dependencies list one per column
            sb.AppendLine("    WHERE o.type IN ('V', 'FN', 'IF', 'TF') AND o.object_id IN (");
            sb.AppendLine("        SELECT d.referencing_id FROM sys.sql_expression_dependencies d");
            sb.AppendLine($"        WHERE d.referenced_id = OBJECT_ID({lit}) AND d.is_schema_bound_reference = 1);");
            sb.AppendLine("    EXEC (@drop);");
            sb.AppendLine($"    DROP TABLE {name};");
            sb.AppendLine("END");
        }
        else
        {
            // An existing table is not re-created: NOEXEC skips CREATE TABLE and its follow-ups.
            sb.AppendLine($"IF OBJECT_ID({lit}, 'U') IS NOT NULL SET NOEXEC ON;");
        }

        sb.AppendLine(BodyMarker);
        sb.AppendLine("GO");
        sb.AppendLine(createBody.Trim());
        sb.AppendLine("GO");
        sb.AppendLine(BodyEndMarker);

        foreach (var check in info.UncheckedChecks ?? new())
        {
            sb.AppendLine($"ALTER TABLE {name} NOCHECK CONSTRAINT {SqlNames.Quote(check)};");
            sb.AppendLine("GO");
        }

        if (mode != ScriptMode.DropAndCreate)
        {
            sb.AppendLine("SET NOEXEC OFF;");
            sb.AppendLine("GO");
        }

        if (mode == ScriptMode.CreateOrAlter && info.Columns is { Count: > 0 } columns)
            AppendColumnReconciliation(sb, name, columns);

        sb.AppendLine(EndMarker);
        return sb.ToString();
    }

    /// <summary>
    /// For a table that already existed: adds columns missing on the target, and
    /// reports (severity 10, so the install continues) columns whose type or
    /// nullability differ and columns the backup does not have. Nothing existing
    /// is changed. On a table this script just created, every check is a no-op.
    /// </summary>
    private static void AppendColumnReconciliation(StringBuilder sb, string name, List<ColumnInfo> columns)
    {
        var lit = SqlNames.Literal(name);
        sb.AppendLine("-- Existing table: add missing columns, report differences (nothing is changed or dropped)");

        foreach (var c in columns)
        {
            sb.AppendLine($"IF COL_LENGTH({lit}, {SqlNames.Literal(c.Name)}) IS NULL");
            sb.AppendLine($"    ALTER TABLE {name} ADD {c.Definition};");
            sb.AppendLine("GO");
        }

        foreach (var c in columns.Where(c => !c.Computed))
        {
            var expected = c.Definition.Length > 200 ? c.Definition[..200] + "..." : c.Definition;
            sb.AppendLine($"IF COL_LENGTH({lit}, {SqlNames.Literal(c.Name)}) IS NOT NULL AND NOT EXISTS (");
            sb.AppendLine($"    SELECT 1 FROM sys.columns c WHERE c.object_id = OBJECT_ID({lit}) AND c.name = {SqlNames.Literal(c.Name)}");
            sb.AppendLine($"      AND TYPE_NAME(c.user_type_id) = {SqlNames.Literal(c.TypeName)} AND c.max_length = {c.MaxLength}");
            sb.AppendLine($"      AND c.precision = {c.Precision} AND c.scale = {c.Scale} AND c.is_nullable = {(c.Nullable ? 1 : 0)})");
            sb.AppendLine($"    RAISERROR (N'Table %s: column %s differs from the backup (expected: %s); left unchanged.', 10, 1, {lit}, {SqlNames.Literal(c.Name)}, {SqlNames.Literal(expected.Replace("%", "%%"))}) WITH NOWAIT;");
        }

        var known = string.Join(", ", columns.Select(c => SqlNames.Literal(c.Name)));
        sb.AppendLine("DECLARE @extra nvarchar(2000) = STUFF((");
        sb.AppendLine($"    SELECT N', ' + c.name FROM sys.columns c WHERE c.object_id = OBJECT_ID({lit}) AND c.name NOT IN ({known})");
        sb.AppendLine("    ORDER BY c.column_id FOR XML PATH(''), TYPE).value('.', 'nvarchar(2000)'), 1, 2, N'');");
        sb.AppendLine("IF @extra IS NOT NULL");
        sb.AppendLine($"    RAISERROR (N'Table %s: column(s) not in the backup: %s; left unchanged.', 10, 1, {lit}, @extra) WITH NOWAIT;");
        sb.AppendLine("GO");
    }

    // ── Modules: views, procedures, functions, triggers ─────────────────────

    private static string WrapModule(ScriptObjectInfo info, string createBody, ScriptMode mode)
    {
        // Modes that cannot apply to an object fall back to drop-and-create:
        // CLR modules have no T-SQL stub for CreateOrAlter.
        if (mode == ScriptMode.CreateOrAlter && Stub(info) is null)
            mode = ScriptMode.DropAndCreate;

        var name  = info.QuotedName;
        var body  = NormaliseToCreate(createBody).Trim();
        var sb    = new StringBuilder();

        sb.AppendLine($"-- {info.Type}: {info.Schema}.{info.Name}");
        sb.AppendLine($"{ObjectMarker}{JsonSerializer.Serialize(info, Json)}>>");

        switch (mode)
        {
            case ScriptMode.DropAndCreate:
                sb.AppendLine($"IF EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID({SqlNames.Literal(name)}) AND type IN ({TypeCodes(info)}))");
                sb.AppendLine($"    DROP {DropKeyword(info)} {name};");
                break;

            case ScriptMode.CreateOnly:
                // NOEXEC: the following batches are compiled but not run, so an
                // existing object (and its state) is left untouched.
                sb.AppendLine($"IF OBJECT_ID({SqlNames.Literal(name)}) IS NOT NULL SET NOEXEC ON;");
                break;

            case ScriptMode.CreateOrAlter:
                // ALTER cannot change a function's kind (scalar vs table-valued).
                sb.AppendLine($"IF EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID({SqlNames.Literal(name)}) AND type IN ({TypeCodes(info)}) AND type <> {SqlNames.Literal(info.SysType)})");
                sb.AppendLine($"    DROP {DropKeyword(info)} {name};");
                sb.AppendLine($"IF OBJECT_ID({SqlNames.Literal(name)}) IS NULL");
                sb.AppendLine($"    EXEC ({SqlNames.Literal(Stub(info)!)});");
                body = ReplaceLeadingKeyword(body, "ALTER");
                break;
        }

        // The marker ends the guard batch: comments leading a CREATE batch would
        // become part of the module's stored definition.
        sb.AppendLine(BodyMarker);
        sb.AppendLine("GO");
        sb.AppendLine(body);
        sb.AppendLine("GO");
        sb.AppendLine(BodyEndMarker);

        if (info.Disabled && info.SysType == "TR" && info.ParentName != null)
        {
            sb.AppendLine($"DISABLE TRIGGER {name} ON {SqlNames.Quote(info.ParentSchema!, info.ParentName)};");
            sb.AppendLine("GO");
        }

        if (mode == ScriptMode.CreateOnly)
        {
            sb.AppendLine("SET NOEXEC OFF;");
            sb.AppendLine("GO");
        }

        sb.AppendLine(EndMarker);
        return sb.ToString();
    }

    // ── Re-wrapping at install time ─────────────────────────────────────────

    private static readonly Regex WrappedObject = new(
        @"^-- <<SMT:OBJECT (?<json>[^\r\n]*)>>[ \t]*\r?\n" +
        @"(?<guard>.*?)" +
        @"^-- <<SMT:BODY>>[ \t]*\r?\nGO[ \t]*\r?\n(?<body>.*?)\r?\nGO[ \t]*\r?\n^-- <<SMT:BODY END>>[ \t]*\r?\n" +
        @".*?^-- <<SMT:END>>[ \t]*(\r?\n)?",
        RegexOptions.Multiline | RegexOptions.Singleline);

    /// <summary>True when the script contains objects produced by this wrapper.</summary>
    public static bool IsWrapped(string script) => script.Contains(ObjectMarker, StringComparison.Ordinal);

    /// <summary>
    /// Rebuilds every wrapped object in <paramref name="script"/> for <paramref name="mode"/>.
    /// The line before each object marker (its "-- TYPE: name" comment) is regenerated too;
    /// text outside wrapped objects is kept unchanged.
    /// </summary>
    public static string Rewrap(string script, ScriptMode mode)
    {
        return WrappedObject.Replace(script, m =>
        {
            var info = JsonSerializer.Deserialize<ScriptObjectInfo>(m.Groups["json"].Value)!;
            var body = m.Groups["body"].Value;
            var wrapped = Wrap(info, body, mode);
            // Wrap starts with the "-- TYPE: name" comment, which precedes the match; drop it.
            return wrapped[(wrapped.IndexOf('\n') + 1)..];
        });
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string TypeCodes(ScriptObjectInfo info) => info.SysType switch
    {
        "FN" or "IF" or "TF" or "FS" or "FT" => "'FN','IF','TF','FS','FT'",
        var t                                => $"'{t}'"
    };

    private static string DropKeyword(ScriptObjectInfo info) => info.SysType switch
    {
        "V"                                  => "VIEW",
        "P" or "PC"                          => "PROCEDURE",
        "FN" or "IF" or "TF" or "FS" or "FT" => "FUNCTION",
        "TR"                                 => "TRIGGER",
        _                                    => throw new NotSupportedException($"Unsupported module type {info.SysType}")
    };

    /// <summary>Minimal valid object of the same kind, created so the body can run as ALTER.</summary>
    private static string? Stub(ScriptObjectInfo info)
    {
        var name = info.QuotedName;
        return info.SysType switch
        {
            "V"  => $"CREATE VIEW {name} AS SELECT 1 AS [stub]",
            "P"  => $"CREATE PROCEDURE {name} AS RETURN",
            "FN" => $"CREATE FUNCTION {name}() RETURNS INT AS BEGIN RETURN 0 END",
            "IF" => $"CREATE FUNCTION {name}() RETURNS TABLE AS RETURN SELECT 1 AS [stub]",
            "TF" => $"CREATE FUNCTION {name}() RETURNS @t TABLE ([stub] INT) AS BEGIN RETURN END",
            "TR" when info.ParentName != null
                 => $"CREATE TRIGGER {name} ON {SqlNames.Quote(info.ParentSchema!, info.ParentName)} " +
                    (info.ParentSysType == "V" ? "INSTEAD OF INSERT" : "AFTER INSERT") + " AS RETURN",
            _    => null   // CLR modules
        };
    }

    /// <summary>Turns a leading CREATE OR ALTER / ALTER into CREATE.</summary>
    public static string NormaliseToCreate(string body) => ReplaceLeadingKeyword(body, "CREATE");

    /// <summary>Replaces the first keyword of a definition, skipping leading whitespace and comments.</summary>
    public static string ReplaceLeadingKeyword(string body, string keyword)
    {
        int i = 0;
        while (i < body.Length)
        {
            if (char.IsWhiteSpace(body[i])) { i++; continue; }
            if (string.CompareOrdinal(body, i, "--", 0, 2) == 0)
            {
                var nl = body.IndexOf('\n', i);
                i = nl < 0 ? body.Length : nl + 1;
                continue;
            }
            if (string.CompareOrdinal(body, i, "/*", 0, 2) == 0)
            {
                // block comments nest in T-SQL
                int depth = 0;
                do
                {
                    if (string.CompareOrdinal(body, i, "/*", 0, 2) == 0) { depth++; i += 2; }
                    else if (string.CompareOrdinal(body, i, "*/", 0, 2) == 0) { depth--; i += 2; }
                    else i++;
                } while (depth > 0 && i < body.Length);
                continue;
            }
            break;
        }

        var m = LeadingKeyword.Match(body, i);
        return m.Success ? body[..i] + keyword + body[(i + m.Length)..] : body;
    }
}
