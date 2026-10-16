using System.Text;
using Microsoft.Data.SqlClient;
using SqlMigrationTool.Models;

namespace SqlMigrationTool.Services;

/// <summary>
/// Builds cleanup_drop_all.sql: a script that removes from a target database
/// everything this backup installs, in reverse dependency order. The tool never
/// runs it. It is inert until edited (SET NOEXEC ON) and refuses to run against
/// the database it was generated from.
/// </summary>
public static class CleanupScriptBuilder
{
    public const string FileName = "cleanup_drop_all.sql";

    /// <param name="programmable">Views, functions and procedures in creation (dependency) order.</param>
    public static async Task<string> BuildAsync(
        ConnectionConfig cfg, IReadOnlyList<DatabaseObject> tables,
        IReadOnlyList<DatabaseObject> programmable, IReadOnlyList<DatabaseObject> triggers)
    {
        await using var conn = await SqlConnectionService.OpenAsync(cfg);

        async Task<List<string[]>> Query(string sql)
        {
            await using var cmd = new SqlCommand(sql, conn);
            await using var rdr = await cmd.ExecuteReaderAsync();
            var rows = new List<string[]>();
            while (await rdr.ReadAsync())
                rows.Add(Enumerable.Range(0, rdr.FieldCount).Select(rdr.GetString).ToArray());
            return rows;
        }

        var server = (await Query("SELECT CAST(@@SERVERNAME AS nvarchar(256)), DB_NAME()"))[0];

        var sb = new StringBuilder();
        sb.AppendLine("-- ============================================================================");
        sb.AppendLine("-- SQL Migration Tool - CLEANUP SCRIPT (drops every object this backup installs)");
        sb.AppendLine($"-- Generated {DateTime.Now:yyyy-MM-dd HH:mm} from {server[0]} / {server[1]}");
        sb.AppendLine("--");
        sb.AppendLine("-- DESTRUCTIVE: run only against a TARGET database you intend to empty.");
        sb.AppendLine("-- Tables are dropped WITH THEIR DATA. Objects not in the backup are kept,");
        sb.AppendLine("-- and schemas are dropped only when empty.");
        sb.AppendLine("--");
        sb.AppendLine("-- The script does nothing as generated. To run it, change the next line to");
        sb.AppendLine("--     SET NOEXEC OFF;");
        sb.AppendLine("-- ============================================================================");
        sb.AppendLine("SET NOEXEC ON;");
        sb.AppendLine("GO");
        sb.AppendLine($"IF CAST(@@SERVERNAME AS nvarchar(256)) = {SqlNames.Literal(server[0])} AND DB_NAME() = {SqlNames.Literal(server[1])}");
        sb.AppendLine("BEGIN");
        sb.AppendLine("    RAISERROR (N'This cleanup script was generated from this database and will not run against it.', 16, 1);");
        sb.AppendLine("    SET NOEXEC ON;");
        sb.AppendLine("END");
        sb.AppendLine("GO");
        sb.AppendLine();

        void Section(string title) { sb.AppendLine($"-- ── {title} "); }
        void Stmt(string sql)      { sb.AppendLine(sql); sb.AppendLine("GO"); }

        // Database triggers first: they could block the drops below.
        Section("Database triggers");
        foreach (var r in await Query("SELECT name FROM sys.triggers WHERE parent_class = 0 AND is_ms_shipped = 0 ORDER BY name"))
            Stmt($"IF EXISTS (SELECT 1 FROM sys.triggers WHERE parent_class = 0 AND name = {SqlNames.Literal(r[0])}) DROP TRIGGER {SqlNames.Quote(r[0])} ON DATABASE;");

        Section("Table triggers");
        foreach (var t in triggers)
            Stmt($"IF OBJECT_ID({SqlNames.Literal(SqlNames.Quote(t.Schema, t.Name))}, 'TR') IS NOT NULL DROP TRIGGER {SqlNames.Quote(t.Schema, t.Name)};");

        // Reverse creation order: dependents before what they depend on.
        Section("Procedures, views and functions (dependents first)");
        foreach (var o in programmable.Reverse())
        {
            var name = SqlNames.Quote(o.Schema, o.Name);
            var (codes, keyword) = o.Type switch
            {
                ObjectTypes.View => ("'V'", "VIEW"),
                ObjectTypes.Proc => ("'P'", "PROCEDURE"),
                _                => ("'FN','IF','TF','FS','FT'", "FUNCTION")
            };
            Stmt($"IF EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID({SqlNames.Literal(name)}) AND type IN ({codes})) DROP {keyword} {name};");
        }

        Section("Synonyms");
        foreach (var r in await Query("SELECT SCHEMA_NAME(schema_id), name FROM sys.synonyms ORDER BY 1, 2"))
            Stmt($"IF OBJECT_ID({SqlNames.Literal(SqlNames.Quote(r[0], r[1]))}, 'SN') IS NOT NULL DROP SYNONYM {SqlNames.Quote(r[0], r[1])};");

        Section("Tables (foreign keys referencing each table are dropped first)");
        foreach (var t in tables)
        {
            var name = SqlNames.Quote(t.Schema, t.Name);
            var lit  = SqlNames.Literal(name);
            sb.AppendLine($"IF OBJECT_ID({lit}, 'U') IS NOT NULL");
            sb.AppendLine("BEGIN");
            sb.AppendLine("    DECLARE @fk nvarchar(max) = N'';");
            sb.AppendLine("    SELECT @fk += N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.' + QUOTENAME(OBJECT_NAME(parent_object_id))");
            sb.AppendLine("                + N' DROP CONSTRAINT ' + QUOTENAME(name) + N';'");
            sb.AppendLine($"    FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID({lit}) AND parent_object_id <> referenced_object_id;");
            sb.AppendLine("    EXEC (@fk);");
            sb.AppendLine($"    DROP TABLE {name};");
            sb.AppendLine("END");
            sb.AppendLine("GO");
        }

        Section("Sequences");
        foreach (var r in await Query("SELECT SCHEMA_NAME(schema_id), name FROM sys.sequences ORDER BY 1, 2"))
            Stmt($"IF OBJECT_ID({SqlNames.Literal(SqlNames.Quote(r[0], r[1]))}, 'SO') IS NOT NULL DROP SEQUENCE {SqlNames.Quote(r[0], r[1])};");

        Section("Table types, then alias data types");
        foreach (var r in await Query("SELECT SCHEMA_NAME(schema_id), name FROM sys.types WHERE is_user_defined = 1 ORDER BY is_table_type DESC, 1, 2"))
            Stmt($"IF TYPE_ID({SqlNames.Literal(SqlNames.Quote(r[0], r[1]))}) IS NOT NULL DROP TYPE {SqlNames.Quote(r[0], r[1])};");

        Section("Partition schemes, then functions");
        foreach (var r in await Query("SELECT name FROM sys.partition_schemes ORDER BY name"))
            Stmt($"IF EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = {SqlNames.Literal(r[0])}) DROP PARTITION SCHEME {SqlNames.Quote(r[0])};");
        foreach (var r in await Query("SELECT name FROM sys.partition_functions ORDER BY name"))
            Stmt($"IF EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = {SqlNames.Literal(r[0])}) DROP PARTITION FUNCTION {SqlNames.Quote(r[0])};");

        Section("XML schema collections");
        foreach (var r in await Query("SELECT SCHEMA_NAME(schema_id), name FROM sys.xml_schema_collections WHERE xml_collection_id > 1 ORDER BY 1, 2"))
            Stmt($"IF EXISTS (SELECT 1 FROM sys.xml_schema_collections WHERE schema_id = SCHEMA_ID({SqlNames.Literal(r[0])}) AND name = {SqlNames.Literal(r[1])}) DROP XML SCHEMA COLLECTION {SqlNames.Quote(r[0], r[1])};");

        Section("Schemas (only when empty)");
        foreach (var r in await Query("SELECT name FROM sys.schemas WHERE schema_id BETWEEN 5 AND 16383 ORDER BY name"))
        {
            var lit = SqlNames.Literal(r[0]);
            Stmt($"IF SCHEMA_ID({lit}) IS NOT NULL" +
                 $" AND NOT EXISTS (SELECT 1 FROM sys.objects WHERE schema_id = SCHEMA_ID({lit}))" +
                 $" AND NOT EXISTS (SELECT 1 FROM sys.types WHERE schema_id = SCHEMA_ID({lit}))" +
                 $" AND NOT EXISTS (SELECT 1 FROM sys.xml_schema_collections WHERE schema_id = SCHEMA_ID({lit}))" +
                 $" EXEC ({SqlNames.Literal($"DROP SCHEMA {SqlNames.Quote(r[0])}")});");
        }

        sb.AppendLine("SET NOEXEC OFF;");
        sb.AppendLine("GO");
        return sb.ToString();
    }
}
