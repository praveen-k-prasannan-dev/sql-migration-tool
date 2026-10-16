using System.Text;
using Microsoft.Data.SqlClient;
using SqlMigrationTool.Models;

namespace SqlMigrationTool.Services;

public class SchemaExporter
{
    private readonly ConnectionConfig _cfg;

    public SchemaExporter(ConnectionConfig cfg) => _cfg = cfg;

    // ── Object listing ──────────────────────────────────────────────────────

    public async Task<List<DatabaseObject>> ListAllObjectsAsync()
    {
        var result = new List<DatabaseObject>();
        await using var conn = await SqlConnectionService.OpenAsync(_cfg);

        result.AddRange(await ListTablesAsync(conn));
        result.AddRange(await ListViewsAsync(conn));
        result.AddRange(await ListStoredProcsAsync(conn));
        result.AddRange(await ListFunctionsAsync(conn));
        result.AddRange(await ListTriggersAsync(conn));

        return result;
    }

    private static async Task<List<DatabaseObject>> ListTablesAsync(SqlConnection conn)
    {
        const string sql = @"
            SELECT SCHEMA_NAME(t.schema_id), t.name,
                   ISNULL((SELECT SUM(p.rows) FROM sys.partitions p           -- one row per partition
                           WHERE p.object_id = t.object_id AND p.index_id IN (0,1)), 0) AS [RowCount]
            FROM sys.tables t
            WHERE t.is_ms_shipped = 0
            ORDER BY 1, 2";

        return await QueryObjectsAsync(conn, sql, ObjectTypes.Table, r => (long)r.GetInt64(2));
    }

    private static async Task<List<DatabaseObject>> ListViewsAsync(SqlConnection conn)
    {
        const string sql = @"
            SELECT TABLE_SCHEMA, TABLE_NAME, 0
            FROM INFORMATION_SCHEMA.VIEWS
            ORDER BY TABLE_SCHEMA, TABLE_NAME";

        return await QueryObjectsAsync(conn, sql, ObjectTypes.View);
    }

    private static async Task<List<DatabaseObject>> ListStoredProcsAsync(SqlConnection conn)
    {
        const string sql = @"
            SELECT ROUTINE_SCHEMA, ROUTINE_NAME, 0
            FROM INFORMATION_SCHEMA.ROUTINES
            WHERE ROUTINE_TYPE = 'PROCEDURE'
            ORDER BY ROUTINE_SCHEMA, ROUTINE_NAME";

        return await QueryObjectsAsync(conn, sql, ObjectTypes.Proc);
    }

    private static async Task<List<DatabaseObject>> ListFunctionsAsync(SqlConnection conn)
    {
        const string sql = @"
            SELECT ROUTINE_SCHEMA, ROUTINE_NAME, 0
            FROM INFORMATION_SCHEMA.ROUTINES
            WHERE ROUTINE_TYPE = 'FUNCTION'
            ORDER BY ROUTINE_SCHEMA, ROUTINE_NAME";

        return await QueryObjectsAsync(conn, sql, ObjectTypes.Func);
    }

    private static async Task<List<DatabaseObject>> ListTriggersAsync(SqlConnection conn)
    {
        const string sql = @"
            SELECT OBJECT_SCHEMA_NAME(parent_id), name, 0
            FROM sys.triggers
            WHERE is_ms_shipped = 0
              AND parent_class = 1   -- table/view triggers only; database triggers have no schema
            ORDER BY name";

        return await QueryObjectsAsync(conn, sql, ObjectTypes.Trigger);
    }

    private static async Task<List<DatabaseObject>> QueryObjectsAsync(
        SqlConnection conn, string sql, string type,
        Func<SqlDataReader, long>? rowCountSelector = null)
    {
        var list = new List<DatabaseObject>();
        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            list.Add(new DatabaseObject
            {
                Schema   = rdr.GetString(0),
                Name     = rdr.GetString(1),
                Type     = type,
                RowCount = rowCountSelector?.Invoke(rdr) ?? 0
            });
        }
        return list;
    }

    // ── Script generation ───────────────────────────────────────────────────

    public async Task<string> ScriptTableAsync(DatabaseObject tbl)
    {
        await using var conn = await SqlConnectionService.OpenAsync(_cfg);

        var objectId = await GetObjectIdAsync(conn, tbl.Schema, tbl.Name);
        var lines    = await GetColumnDefsAsync(conn, objectId, forTableType: false);
        var (constraints, disabledChecks) = await GetTableConstraintsAsync(conn, objectId);
        lines.AddRange(constraints);

        var name = SqlNames.Quote(tbl.Schema, tbl.Name);
        var sb = new StringBuilder();
        sb.AppendLine($"-- Table: {tbl.FullName}");
        sb.AppendLine($"IF OBJECT_ID({SqlNames.Literal(name)},'U') IS NOT NULL");
        sb.AppendLine($"    DROP TABLE {name}");
        sb.AppendLine("GO");
        sb.AppendLine($"CREATE TABLE {name} (");
        sb.AppendLine(string.Join(",\r\n", lines.Select(l => $"    {l}")));
        sb.AppendLine(")");
        sb.AppendLine("GO");

        foreach (var check in disabledChecks)
        {
            sb.AppendLine($"ALTER TABLE {name} NOCHECK CONSTRAINT {SqlNames.Quote(check)}");
            sb.AppendLine("GO");
        }
        return sb.ToString();
    }

    public async Task<string> ScriptViewAsync(DatabaseObject obj)    => await ScriptDefinitionObject(obj, "V");
    public async Task<string> ScriptProcAsync(DatabaseObject obj)    => await ScriptDefinitionObject(obj, "P");
    public async Task<string> ScriptFuncAsync(DatabaseObject obj)    => await ScriptDefinitionObject(obj, "FN,IF,TF");
    public async Task<string> ScriptTriggerAsync(DatabaseObject obj) => await ScriptDefinitionObject(obj, "TR");

    private async Task<string> ScriptDefinitionObject(DatabaseObject obj, string typeCode)
    {
        await using var conn = await SqlConnectionService.OpenAsync(_cfg);
        await using var cmd  = new SqlCommand(
            "SELECT OBJECT_DEFINITION(OBJECT_ID(@n))", conn);
        cmd.Parameters.AddWithValue("@n", obj.FullName);

        var def = (string?)await cmd.ExecuteScalarAsync();
        if (string.IsNullOrWhiteSpace(def))
            return $"-- WARNING: Could not retrieve definition for {obj.FullName}\r\nGO\r\n";

        // Ensure it starts with CREATE (not ALTER)
        var trimmed = def.TrimStart();

        var typeLabel = obj.Type switch
        {
            ObjectTypes.View    => "V",
            ObjectTypes.Proc    => "P",
            ObjectTypes.Func    => "FN",
            ObjectTypes.Trigger => "TR",
            _         => "U"
        };

        var sb = new StringBuilder();
        sb.AppendLine($"-- {obj.Type}: {obj.FullName}");
        sb.AppendLine($"IF OBJECT_ID(N'[{obj.Schema}].[{obj.Name}]','{typeLabel}') IS NOT NULL");

        var dropType = obj.Type switch
        {
            ObjectTypes.View    => "VIEW",
            ObjectTypes.Proc    => "PROCEDURE",
            ObjectTypes.Func    => "FUNCTION",
            ObjectTypes.Trigger => "TRIGGER",
            _         => "OBJECT"
        };
        sb.AppendLine($"    DROP {dropType} [{obj.Schema}].[{obj.Name}]");
        sb.AppendLine("GO");
        sb.AppendLine(trimmed);
        sb.AppendLine("GO");
        return sb.ToString();
    }

    // ── Prerequisites ───────────────────────────────────────────────────────

    /// <summary>CREATE SCHEMA for every user schema, or null when there are none besides dbo.</summary>
    public async Task<string?> ScriptSchemasAsync()
    {
        // schema_id 1-4 are dbo/guest/INFORMATION_SCHEMA/sys; 16384+ are fixed-role schemas.
        const string sql = @"
            SELECT name FROM sys.schemas
            WHERE schema_id BETWEEN 5 AND 16383
            ORDER BY name";

        await using var conn = await SqlConnectionService.OpenAsync(_cfg);
        await using var cmd  = new SqlCommand(sql, conn);
        await using var rdr  = await cmd.ExecuteReaderAsync();

        var sb = new StringBuilder();
        sb.AppendLine("-- Schemas");
        sb.AppendLine();
        int count = 0;
        while (await rdr.ReadAsync())
        {
            var name = rdr.GetString(0);
            sb.AppendLine($"IF SCHEMA_ID({SqlNames.Literal(name)}) IS NULL");
            sb.AppendLine($"    EXEC({SqlNames.Literal($"CREATE SCHEMA {SqlNames.Quote(name)}")});");
            sb.AppendLine("GO");
            count++;
        }
        return count == 0 ? null : sb.ToString();
    }

    /// <summary>CREATE TYPE ... FROM for every alias (user-defined data) type, or null when there are none.</summary>
    public async Task<string?> ScriptDataTypesAsync()
    {
        const string sql = @"
            SELECT SCHEMA_NAME(t.schema_id), t.name, TYPE_NAME(t.system_type_id),
                   t.max_length, t.precision, t.scale, t.is_nullable
            FROM sys.types t
            WHERE t.is_user_defined = 1 AND t.is_table_type = 0 AND t.is_assembly_type = 0
            ORDER BY 1, 2";

        await using var conn = await SqlConnectionService.OpenAsync(_cfg);
        await using var cmd  = new SqlCommand(sql, conn);
        await using var rdr  = await cmd.ExecuteReaderAsync();

        var sb = new StringBuilder();
        sb.AppendLine("-- User-Defined Data Types");
        sb.AppendLine();
        int count = 0;
        while (await rdr.ReadAsync())
        {
            var name     = SqlNames.Quote(rdr.GetString(0), rdr.GetString(1));
            var baseType = BuildTypeDef(rdr.GetString(2), rdr.GetInt16(3), rdr.GetByte(4), rdr.GetByte(5),
                                        null, null, false);
            var nullable = rdr.GetBoolean(6) ? "NULL" : "NOT NULL";

            sb.AppendLine($"IF TYPE_ID({SqlNames.Literal(name)}) IS NULL");
            sb.AppendLine($"    CREATE TYPE {name} FROM {baseType} {nullable};");
            sb.AppendLine("GO");
            count++;
        }
        return count == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// CREATE TYPE ... AS TABLE for every user-defined table type, with its columns,
    /// PRIMARY KEY / UNIQUE / CHECK constraints and inline indexes; null when there are none.
    /// Constraint names are omitted because table types do not allow them.
    /// </summary>
    public async Task<string?> ScriptTableTypesAsync()
    {
        const string typesSql = @"
            SELECT SCHEMA_NAME(tt.schema_id), tt.name, tt.type_table_object_id, tt.is_memory_optimized
            FROM sys.table_types tt
            WHERE tt.is_user_defined = 1
            ORDER BY 1, 2";

        await using var conn = await SqlConnectionService.OpenAsync(_cfg);

        var types = new List<(string schema, string name, int objectId, bool memoryOptimized)>();
        await using (var cmd = new SqlCommand(typesSql, conn))
        await using (var rdr = await cmd.ExecuteReaderAsync())
            while (await rdr.ReadAsync())
                types.Add((rdr.GetString(0), rdr.GetString(1), rdr.GetInt32(2), rdr.GetBoolean(3)));

        if (types.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("-- User-Defined Table Types");
        sb.AppendLine();

        foreach (var (schema, name, objectId, memoryOptimized) in types)
        {
            var lines = await GetColumnDefsAsync(conn, objectId, forTableType: true);
            lines.AddRange(await GetTableTypeConstraintsAsync(conn, objectId));

            var fullName = SqlNames.Quote(schema, name);
            sb.AppendLine($"IF TYPE_ID({SqlNames.Literal(fullName)}) IS NULL");
            sb.AppendLine($"    CREATE TYPE {fullName} AS TABLE (");
            sb.AppendLine(string.Join(",\r\n", lines.Select(l => $"        {l}")));
            sb.AppendLine(memoryOptimized ? "    ) WITH (MEMORY_OPTIMIZED = ON);" : "    );");
            sb.AppendLine("GO");
        }
        return sb.ToString();
    }

    /// <summary>
    /// CREATE SEQUENCE for every sequence with its original definition (START WITH is
    /// the original start value), or null when there are none. The current position
    /// is restored after the data load by <see cref="ScriptSequenceRestartsAsync"/>.
    /// </summary>
    public async Task<string?> ScriptSequencesAsync()
    {
        const string sql = @"
            SELECT SCHEMA_NAME(s.schema_id), s.name,
                   ty.is_user_defined, SCHEMA_NAME(ty.schema_id), ty.name, TYPE_NAME(s.system_type_id),
                   s.precision, s.scale,
                   CAST(s.start_value   AS varchar(40)), CAST(s.increment     AS varchar(40)),
                   CAST(s.minimum_value AS varchar(40)), CAST(s.maximum_value AS varchar(40)),
                   s.is_cycling, s.is_cached, s.cache_size
            FROM sys.sequences s
            JOIN sys.types ty ON ty.user_type_id = s.user_type_id
            ORDER BY 1, 2";

        await using var conn = await SqlConnectionService.OpenAsync(_cfg);
        await using var cmd  = new SqlCommand(sql, conn);
        await using var rdr  = await cmd.ExecuteReaderAsync();

        var sb = new StringBuilder();
        sb.AppendLine("-- Sequences");
        sb.AppendLine();
        int count = 0;
        while (await rdr.ReadAsync())
        {
            var name = SqlNames.Quote(rdr.GetString(0), rdr.GetString(1));
            var type = rdr.GetBoolean(2)
                ? SqlNames.Quote(rdr.GetString(3), rdr.GetString(4))
                : BuildTypeDef(rdr.GetString(5), 0, rdr.GetByte(6), rdr.GetByte(7), null, null, false);
            var cache = !rdr.GetBoolean(13) ? "NO CACHE"
                      : rdr.IsDBNull(14)    ? "CACHE"
                      : $"CACHE {rdr.GetInt32(14)}";

            sb.AppendLine($"IF OBJECT_ID({SqlNames.Literal(name)}, 'SO') IS NULL");
            sb.AppendLine($"    CREATE SEQUENCE {name} AS {type}");
            sb.AppendLine($"        START WITH {rdr.GetString(8)} INCREMENT BY {rdr.GetString(9)}");
            sb.AppendLine($"        MINVALUE {rdr.GetString(10)} MAXVALUE {rdr.GetString(11)}");
            sb.AppendLine($"        {(rdr.GetBoolean(12) ? "CYCLE" : "NO CYCLE")} {cache};");
            sb.AppendLine("GO");
            count++;
        }
        return count == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// ALTER SEQUENCE ... RESTART WITH the source's next value for every sequence
    /// that has been used, so the target continues where the source left off;
    /// null when no sequence needs it.
    /// </summary>
    public async Task<string?> ScriptSequenceRestartsAsync()
    {
        await using var conn = await SqlConnectionService.OpenAsync(_cfg);

        // last_used_value exists from SQL Server 2017; before that current_value is the best available.
        await using var probe = new SqlCommand("SELECT COL_LENGTH('sys.sequences', 'last_used_value')", conn);
        var lastUsed = await probe.ExecuteScalarAsync() is DBNull or null ? "s.current_value" : "s.last_used_value";

        var sql = $@"
            SELECT SCHEMA_NAME(s.schema_id), s.name,
                   CAST({lastUsed}       AS decimal(38,0)),
                   CAST(s.increment     AS decimal(38,0)),
                   CAST(s.minimum_value AS decimal(38,0)),
                   CAST(s.maximum_value AS decimal(38,0)),
                   s.is_cycling
            FROM sys.sequences s
            ORDER BY 1, 2";

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync();

        var sb = new StringBuilder();
        sb.AppendLine("-- Sequences: continue from the source's next value");
        sb.AppendLine();
        int count = 0;
        while (await rdr.ReadAsync())
        {
            if (rdr.IsDBNull(2)) continue;   // never used: START WITH is already correct

            var name = SqlNames.Quote(rdr.GetString(0), rdr.GetString(1));
            decimal last = rdr.GetDecimal(2), incr = rdr.GetDecimal(3),
                    min  = rdr.GetDecimal(4), max  = rdr.GetDecimal(5);
            var next = last + incr;

            if (next > max || next < min)
            {
                if (!rdr.GetBoolean(6))
                {
                    sb.AppendLine($"-- {name} is exhausted in the source (no CYCLE); left at its start value.");
                    continue;
                }
                next = incr > 0 ? min : max;
            }

            sb.AppendLine($"ALTER SEQUENCE {name} RESTART WITH {next};");
            sb.AppendLine("GO");
            count++;
        }
        return count == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// CREATE SYNONYM for every synonym, or null when there are none. A target named
    /// with this database's own name ([ThisDb].[schema].[object]) is reduced to a
    /// two-part name so the restored synonym points into the target database, not
    /// back at the source.
    /// </summary>
    public async Task<string?> ScriptSynonymsAsync()
    {
        const string sql = @"
            SELECT SCHEMA_NAME(s.schema_id), s.name,
                   CASE WHEN PARSENAME(s.base_object_name, 4) IS NULL
                         AND PARSENAME(s.base_object_name, 3) = DB_NAME()
                        THEN QUOTENAME(PARSENAME(s.base_object_name, 2)) + '.' + QUOTENAME(PARSENAME(s.base_object_name, 1))
                        ELSE s.base_object_name
                   END
            FROM sys.synonyms s
            ORDER BY 1, 2";

        await using var conn = await SqlConnectionService.OpenAsync(_cfg);
        await using var cmd  = new SqlCommand(sql, conn);
        await using var rdr  = await cmd.ExecuteReaderAsync();

        var sb = new StringBuilder();
        sb.AppendLine("-- Synonyms");
        sb.AppendLine();
        int count = 0;
        while (await rdr.ReadAsync())
        {
            var name = SqlNames.Quote(rdr.GetString(0), rdr.GetString(1));
            sb.AppendLine($"IF OBJECT_ID({SqlNames.Literal(name)}, 'SN') IS NULL");
            sb.AppendLine($"    CREATE SYNONYM {name} FOR {rdr.GetString(2)};");
            sb.AppendLine("GO");
            count++;
        }
        return count == 0 ? null : sb.ToString();
    }

    private static async Task<List<string>> GetTableTypeConstraintsAsync(SqlConnection conn, int objectId)
    {
        const string sql = @"
            SELECT 'K' AS Kind, i.index_id, i.name, i.type_desc, i.is_unique,
                   i.is_primary_key, NULL AS Definition
            FROM sys.indexes i
            WHERE i.object_id = @id AND i.type > 0
            UNION ALL
            SELECT 'C', NULL, NULL, NULL, NULL, NULL, cc.definition
            FROM sys.check_constraints cc
            WHERE cc.parent_object_id = @id
            ORDER BY Kind DESC, index_id";

        var keyColumns = await GetIndexKeyColumnsAsync(conn, objectId);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", objectId);
        await using var rdr = await cmd.ExecuteReaderAsync();

        var lines = new List<string>();
        while (await rdr.ReadAsync())
        {
            if (rdr.GetString(0) == "C")
            {
                lines.Add($"CHECK {rdr.GetString(6)}");
                continue;
            }

            var cols      = keyColumns[rdr.GetInt32(1)];
            var clustered = rdr.GetString(3);              // CLUSTERED / NONCLUSTERED / HASH ...
            var unique    = rdr.GetBoolean(4);

            if (rdr.GetBoolean(5))
                lines.Add($"PRIMARY KEY {clustered} ({cols})");
            else if (rdr.IsDBNull(2) || unique && IsSystemIndexName(rdr.GetString(2)))
                lines.Add($"UNIQUE {clustered} ({cols})"); // inline UNIQUE constraint
            else
                lines.Add($"INDEX {SqlNames.Quote(rdr.GetString(2))}{(unique ? " UNIQUE" : "")} {clustered} ({cols})");
        }
        return lines;
    }

    // System-generated constraint index names look like UQ__IdList__3214EC27A1B2C3D4.
    private static bool IsSystemIndexName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^(UQ|PK)__.+__[0-9A-F]{16}$");

    /// <summary>Key column list per index_id, e.g. "[A], [B] DESC".</summary>
    private static async Task<Dictionary<int, string>> GetIndexKeyColumnsAsync(SqlConnection conn, int objectId)
    {
        const string sql = @"
            SELECT ic.index_id, c.name, ic.is_descending_key
            FROM sys.index_columns ic
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = @id AND ic.key_ordinal > 0
            ORDER BY ic.index_id, ic.key_ordinal";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", objectId);
        await using var rdr = await cmd.ExecuteReaderAsync();

        var cols = new Dictionary<int, List<string>>();
        while (await rdr.ReadAsync())
        {
            var indexId = rdr.GetInt32(0);
            if (!cols.TryGetValue(indexId, out var list)) cols[indexId] = list = new();
            list.Add(SqlNames.Quote(rdr.GetString(1)) + (rdr.GetBoolean(2) ? " DESC" : ""));
        }
        return cols.ToDictionary(kv => kv.Key, kv => string.Join(", ", kv.Value));
    }

    public async Task<string> ScriptForeignKeysAsync()
    {
        const string sql = @"
            SELECT fk.name,
                   OBJECT_SCHEMA_NAME(fk.parent_object_id)      AS ParentSchema,
                   OBJECT_NAME(fk.parent_object_id)             AS ParentTable,
                   pc.name                                       AS ParentColumn,
                   OBJECT_SCHEMA_NAME(fk.referenced_object_id)  AS RefSchema,
                   OBJECT_NAME(fk.referenced_object_id)         AS RefTable,
                   rc.name                                       AS RefColumn,
                   fk.delete_referential_action_desc,
                   fk.update_referential_action_desc
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            ORDER BY ParentSchema, ParentTable, fk.name";

        await using var conn = await SqlConnectionService.OpenAsync(_cfg);
        await using var cmd  = new SqlCommand(sql, conn);
        await using var rdr  = await cmd.ExecuteReaderAsync();

        var sb = new StringBuilder();
        sb.AppendLine("-- Foreign Key Constraints");
        sb.AppendLine();

        var fkLines = new Dictionary<string, List<(string parent, string pCol, string refSchema, string refTable, string rCol, string del, string upd)>>();

        while (await rdr.ReadAsync())
        {
            var fkName    = rdr.GetString(0);
            var pSchema   = rdr.GetString(1);
            var pTable    = rdr.GetString(2);
            var pCol      = rdr.GetString(3);
            var rSchema   = rdr.GetString(4);
            var rTable    = rdr.GetString(5);
            var rCol      = rdr.GetString(6);
            var delAction = rdr.GetString(7);
            var updAction = rdr.GetString(8);

            var key = $"{pSchema}.{pTable}|{fkName}";
            if (!fkLines.ContainsKey(key))
                fkLines[key] = new();
            fkLines[key].Add((pSchema + "." + pTable, pCol, rSchema, rTable, rCol, delAction, updAction));
        }

        foreach (var (key, cols) in fkLines)
        {
            var parts   = key.Split('|');
            var fkName  = parts[1];
            var (pFull, pCol, rSchema, rTable, rCol, del, upd) = cols[0];
            var pParts  = pFull.Split('.');

            var pColList = string.Join(", ", cols.Select(c => $"[{c.pCol}]"));
            var rColList = string.Join(", ", cols.Select(c => $"[{c.rCol}]"));

            sb.AppendLine($"IF OBJECT_ID(N'{fkName}','F') IS NULL");
            sb.AppendLine($"ALTER TABLE [{pParts[0]}].[{pParts[1]}]");
            sb.AppendLine($"    ADD CONSTRAINT [{fkName}]");
            sb.AppendLine($"    FOREIGN KEY ({pColList})");
            sb.AppendLine($"    REFERENCES [{rSchema}].[{rTable}] ({rColList})");
            sb.AppendLine($"    ON DELETE {del.Replace("_", " ")}");
            sb.AppendLine($"    ON UPDATE {upd.Replace("_", " ")};");
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async Task<string> ScriptIndexesAsync()
    {
        const string sql = @"
            SELECT OBJECT_SCHEMA_NAME(i.object_id) AS [Schema],
                   OBJECT_NAME(i.object_id)         AS TableName,
                   i.name                           AS IndexName,
                   i.type_desc,
                   i.is_unique,
                   c.name                           AS ColumnName,
                   ic.key_ordinal,
                   ic.is_descending_key
            FROM sys.indexes i
            JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.is_primary_key = 0
              AND i.is_unique_constraint = 0
              AND i.type > 0
              AND ic.is_included_column = 0
              AND OBJECTPROPERTY(i.object_id,'IsUserTable') = 1
            ORDER BY TableName, IndexName, ic.key_ordinal";

        await using var conn = await SqlConnectionService.OpenAsync(_cfg);
        await using var cmd  = new SqlCommand(sql, conn);
        await using var rdr  = await cmd.ExecuteReaderAsync();

        var indexes = new Dictionary<string, (string schema, string table, string type, bool unique, List<(string col, bool desc)> cols)>();
        while (await rdr.ReadAsync())
        {
            var schema   = rdr.GetString(0);
            var table    = rdr.GetString(1);
            var idxName  = rdr.GetString(2);
            var idxType  = rdr.GetString(3);
            var isUnique = rdr.GetBoolean(4);
            var col      = rdr.GetString(5);
            var desc     = rdr.GetBoolean(7);

            if (!indexes.ContainsKey(idxName))
                indexes[idxName] = (schema, table, idxType, isUnique, new());
            indexes[idxName].cols.Add((col, desc));
        }

        var sb = new StringBuilder();
        sb.AppendLine("-- Indexes");
        sb.AppendLine();

        foreach (var (name, (schema, table, type, unique, cols)) in indexes)
        {
            var colList = string.Join(", ", cols.Select(c => $"[{c.col}]{(c.desc ? " DESC" : " ASC")}"));
            var uniqueKw = unique ? "UNIQUE " : "";
            sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{name}' AND object_id = OBJECT_ID('[{schema}].[{table}]'))");
            sb.AppendLine($"CREATE {uniqueKw}{type} INDEX [{name}]");
            sb.AppendLine($"    ON [{schema}].[{table}] ({colList});");
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ── Column helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Column definitions for a table or table type, read from sys.columns so that
    /// alias types, collations, precision and column options survive the round trip.
    /// Named defaults keep their names except inside table types, where constraint
    /// names are not allowed.
    /// </summary>
    private static async Task<List<string>> GetColumnDefsAsync(
        SqlConnection conn, int objectId, bool forTableType)
    {
        const string sql = @"
            SELECT c.name,
                   ty.name                          AS TypeName,
                   ty.is_user_defined               AS IsAliasType,
                   SCHEMA_NAME(ty.schema_id)        AS TypeSchema,
                   c.max_length, c.precision, c.scale,
                   c.is_nullable,
                   c.collation_name,
                   CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS sysname) AS DbCollation,
                   c.is_identity,
                   CAST(ic.seed_value      AS varchar(40)) AS IdentSeed,
                   CAST(ic.increment_value AS varchar(40)) AS IdentIncr,
                   ic.is_not_for_replication        AS IdentNotForRepl,
                   cc.definition                    AS ComputedDef,
                   cc.is_persisted,
                   c.is_sparse, c.is_rowguidcol, c.is_filestream,
                   dc.name                          AS DefaultName,
                   dc.definition                    AS DefaultDef,
                   dc.is_system_named               AS DefaultAutoNamed,
                   SCHEMA_NAME(x.schema_id)         AS XmlSchema,
                   x.name                           AS XmlCollection,
                   c.is_xml_document
            FROM sys.columns c
            JOIN sys.types ty                      ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.identity_columns ic      ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.computed_columns cc      ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.default_constraints dc   ON dc.object_id = c.default_object_id
            LEFT JOIN sys.xml_schema_collections x ON x.xml_collection_id = c.xml_collection_id
                                                  AND c.xml_collection_id > 1
            WHERE c.object_id = @id
            ORDER BY c.column_id";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", objectId);

        var defs = new List<string>();
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            string? Str(string col)  => rdr[col] is DBNull ? null : (string)rdr[col];
            bool    Bool(string col) => rdr[col] is bool b && b;

            var colName = SqlNames.Quote(rdr.GetString(0));

            var computedDef = Str("ComputedDef");
            if (computedDef != null)
            {
                var persisted = Bool("is_persisted")
                    ? " PERSISTED" + (Bool("is_nullable") ? "" : " NOT NULL")
                    : "";
                defs.Add($"{colName} AS {computedDef}{persisted}");
                continue;
            }

            var sb = new StringBuilder(colName).Append(' ');

            sb.Append(Bool("IsAliasType")
                ? SqlNames.Quote(rdr.GetString(3), rdr.GetString(1))
                : BuildTypeDef(rdr.GetString(1),
                               Convert.ToInt32(rdr["max_length"]),
                               Convert.ToInt32(rdr["precision"]),
                               Convert.ToInt32(rdr["scale"]),
                               Str("XmlSchema"), Str("XmlCollection"), Bool("is_xml_document")));

            var collation = Str("collation_name");
            if (collation != null && collation != Str("DbCollation"))
                sb.Append($" COLLATE {collation}");

            if (Bool("is_filestream")) sb.Append(" FILESTREAM");
            if (Bool("is_sparse"))     sb.Append(" SPARSE");

            if (Bool("is_identity"))
            {
                sb.Append($" IDENTITY({Str("IdentSeed")},{Str("IdentIncr")})");
                if (Bool("IdentNotForRepl")) sb.Append(" NOT FOR REPLICATION");
            }

            if (Bool("is_rowguidcol")) sb.Append(" ROWGUIDCOL");

            sb.Append(Bool("is_nullable") ? " NULL" : " NOT NULL");

            var defaultDef = Str("DefaultDef");
            if (defaultDef != null)
            {
                if (!forTableType && !Bool("DefaultAutoNamed"))
                    sb.Append($" CONSTRAINT {SqlNames.Quote(Str("DefaultName")!)}");
                sb.Append($" DEFAULT {defaultDef}");
            }

            defs.Add(sb.ToString());
        }
        return defs;
    }

    /// <summary>T-SQL type declaration for a built-in type, from sys.columns metadata.</summary>
    private static string BuildTypeDef(string typeName, int maxLength, int precision, int scale,
                                       string? xmlSchema, string? xmlCollection, bool xmlDocument)
    {
        var dt = typeName.ToUpperInvariant();
        return dt switch
        {
            "VARCHAR" or "CHAR" or "VARBINARY" or "BINARY"
                => maxLength == -1 ? $"{dt}(MAX)" : $"{dt}({maxLength})",

            // n-types store max_length in bytes: two per character
            "NVARCHAR" or "NCHAR"
                => maxLength == -1 ? $"{dt}(MAX)" : $"{dt}({maxLength / 2})",

            "DECIMAL" or "NUMERIC"
                => $"{dt}({precision},{scale})",

            // fractional-seconds precision is held in scale
            "DATETIME2" or "TIME" or "DATETIMEOFFSET"
                => $"{dt}({scale})",

            "XML" when xmlCollection != null
                => $"XML({(xmlDocument ? "DOCUMENT" : "CONTENT")} {SqlNames.Quote(xmlSchema!, xmlCollection)})",

            _ => dt
        };
    }

    private static async Task<int> GetObjectIdAsync(SqlConnection conn, string schema, string name)
    {
        await using var cmd = new SqlCommand("SELECT OBJECT_ID(@n)", conn);
        cmd.Parameters.AddWithValue("@n", SqlNames.Quote(schema, name));
        return await cmd.ExecuteScalarAsync() is int id
            ? id
            : throw new InvalidOperationException($"Object {schema}.{name} not found.");
    }

    /// <summary>
    /// PRIMARY KEY, UNIQUE and CHECK constraint lines for CREATE TABLE, plus the
    /// names of CHECK constraints that are disabled in the source.
    /// </summary>
    private static async Task<(List<string> lines, List<string> disabledChecks)> GetTableConstraintsAsync(
        SqlConnection conn, int objectId)
    {
        const string sql = @"
            SELECT k.type, k.name, k.is_system_named, i.index_id, i.type_desc, NULL, 0,
                   CASE k.type WHEN 'PK' THEN 0 ELSE 1 END AS SortKey
            FROM sys.key_constraints k
            JOIN sys.indexes i ON i.object_id = k.parent_object_id AND i.index_id = k.unique_index_id
            WHERE k.parent_object_id = @id
            UNION ALL
            SELECT 'C', cc.name, cc.is_system_named, NULL, NULL, cc.definition, cc.is_disabled, 2
            FROM sys.check_constraints cc
            WHERE cc.parent_object_id = @id
            ORDER BY SortKey, 2";   // PK, UQ, then CHECK

        var keyColumns = await GetIndexKeyColumnsAsync(conn, objectId);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", objectId);
        await using var rdr = await cmd.ExecuteReaderAsync();

        var lines    = new List<string>();
        var disabled = new List<string>();
        while (await rdr.ReadAsync())
        {
            var kind   = rdr.GetString(0).Trim();
            var name   = rdr.GetString(1);
            var prefix = rdr.GetBoolean(2) ? "" : $"CONSTRAINT {SqlNames.Quote(name)} ";

            switch (kind)
            {
                case "PK":
                    lines.Add($"{prefix}PRIMARY KEY {rdr.GetString(4)} ({keyColumns[rdr.GetInt32(3)]})");
                    break;
                case "UQ":
                    lines.Add($"{prefix}UNIQUE {rdr.GetString(4)} ({keyColumns[rdr.GetInt32(3)]})");
                    break;
                default:
                    lines.Add($"{prefix}CHECK {rdr.GetString(5)}");
                    // A system-named constraint gets a new name on the target, so it cannot be addressed.
                    if (Convert.ToBoolean(rdr.GetValue(6)) && !rdr.GetBoolean(2)) disabled.Add(name);
                    break;
            }
        }
        return (lines, disabled);
    }

    public async Task<string?> GetDatabaseCollationAsync()
    {
        await using var conn = await SqlConnectionService.OpenAsync(_cfg);
        await using var cmd  = new SqlCommand("SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS sysname)", conn);
        return await cmd.ExecuteScalarAsync() as string;
    }

    public async Task<int> GetCompatibilityLevelAsync()
    {
        const string sql = "SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()";
        await using var conn = await SqlConnectionService.OpenAsync(_cfg);
        await using var cmd  = new SqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync();
        return result is not null ? Convert.ToInt32(result) : 160;
    }
}
