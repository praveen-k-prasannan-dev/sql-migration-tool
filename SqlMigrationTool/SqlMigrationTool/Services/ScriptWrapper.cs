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

    [System.Text.Json.Serialization.JsonIgnore]
    public string QuotedName => SqlNames.Quote(Schema, Name);
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

    // ── Modules: views, procedures, functions, triggers ─────────────────────

    public static string WrapModule(ScriptObjectInfo info, string createBody, ScriptMode mode)
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
            var wrapped = WrapModule(info, body, mode);
            // WrapModule starts with the "-- TYPE: name" comment, which precedes the match; drop it.
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
