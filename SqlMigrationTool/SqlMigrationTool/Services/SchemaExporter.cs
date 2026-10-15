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
            SELECT t.TABLE_SCHEMA, t.TABLE_NAME, ISNULL(p.rows, 0) AS [RowCount]
            FROM INFORMATION_SCHEMA.TABLES t
            LEFT JOIN sys.partitions p
                ON p.object_id = OBJECT_ID(t.TABLE_SCHEMA + '.' + t.TABLE_NAME)
               AND p.index_id IN (0,1)
            WHERE t.TABLE_TYPE = 'BASE TABLE'
            ORDER BY t.TABLE_SCHEMA, t.TABLE_NAME";

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

        var columns = await GetColumnDefsAsync(conn, tbl.Schema, tbl.Name);
        var pkCols  = await GetPrimaryKeyColsAsync(conn, tbl.Schema, tbl.Name);
        var pkName  = await GetPrimaryKeyNameAsync(conn, tbl.Schema, tbl.Name);

        var sb = new StringBuilder();
        sb.AppendLine($"-- Table: {tbl.FullName}");
        sb.AppendLine($"IF OBJECT_ID(N'[{tbl.Schema}].[{tbl.Name}]','U') IS NOT NULL");
        sb.AppendLine($"    DROP TABLE [{tbl.Schema}].[{tbl.Name}]");
        sb.AppendLine("GO");
        sb.AppendLine($"CREATE TABLE [{tbl.Schema}].[{tbl.Name}] (");

        for (int i = 0; i < columns.Count; i++)
        {
            var col  = columns[i];
            var comma = (i < columns.Count - 1 || pkCols.Count > 0) ? "," : "";
            sb.AppendLine($"    {col}{comma}");
        }

        if (pkCols.Count > 0 && !string.IsNullOrEmpty(pkName))
        {
            var pkColList = string.Join(", ", pkCols.Select(c => $"[{c}]"));
            sb.AppendLine($"    CONSTRAINT [{pkName}] PRIMARY KEY CLUSTERED ({pkColList})");
        }

        sb.AppendLine(")");
        sb.AppendLine("GO");
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

    private static async Task<List<string>> GetColumnDefsAsync(
        SqlConnection conn, string schema, string table)
    {
        const string sql = @"
            SELECT c.COLUMN_NAME, c.DATA_TYPE,
                   c.CHARACTER_MAXIMUM_LENGTH,
                   c.NUMERIC_PRECISION, c.NUMERIC_SCALE,
                   c.IS_NULLABLE, c.COLUMN_DEFAULT,
                   COLUMNPROPERTY(OBJECT_ID(c.TABLE_SCHEMA+'.'+c.TABLE_NAME),c.COLUMN_NAME,'IsIdentity') AS IsIdentity,
                   IDENT_SEED(c.TABLE_SCHEMA+'.'+c.TABLE_NAME)  AS IdentSeed,
                   IDENT_INCR(c.TABLE_SCHEMA+'.'+c.TABLE_NAME)  AS IdentIncr,
                   COLUMNPROPERTY(OBJECT_ID(c.TABLE_SCHEMA+'.'+c.TABLE_NAME),c.COLUMN_NAME,'IsComputed') AS IsComputed,
                   cc.definition  AS ComputedDef,
                   cc.is_persisted AS IsPersisted
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN sys.computed_columns cc
                ON cc.object_id = OBJECT_ID(c.TABLE_SCHEMA+'.'+c.TABLE_NAME)
               AND cc.name = c.COLUMN_NAME
            WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
            ORDER BY c.ORDINAL_POSITION";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table",  table);

        var defs = new List<string>();
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var colName    = rdr.GetString(0);
            var dataType   = rdr.GetString(1).ToUpper();
            var charMax    = rdr.IsDBNull(2) ? (int?)null : rdr.GetInt32(2);
            var numPrec    = rdr.IsDBNull(3) ? (int?)null : rdr.GetByte(3);
            var numScale   = rdr.IsDBNull(4) ? (int?)null : rdr.GetInt32(4);
            var isNullable = rdr.GetString(5) == "YES";
            var defVal     = rdr.IsDBNull(6)  ? null : rdr.GetString(6);
            var isIdent    = !rdr.IsDBNull(7) && rdr.GetInt32(7) == 1;
            var identSeed  = rdr.IsDBNull(8)  ? 1    : Convert.ToInt32(rdr.GetValue(8));
            var identIncr  = rdr.IsDBNull(9)  ? 1    : Convert.ToInt32(rdr.GetValue(9));
            var isComputed  = !rdr.IsDBNull(10) && rdr.GetInt32(10) == 1;
            var computedDef = rdr.IsDBNull(11)  ? null : rdr.GetString(11);
            var isPersisted = !rdr.IsDBNull(12) && rdr.GetBoolean(12);

            if (isComputed)
            {
                if (computedDef != null)
                {
                    var persistedKw = isPersisted ? " PERSISTED" : "";
                    defs.Add($"[{colName}] AS {computedDef}{persistedKw}");
                }
                continue;
            }

            var typeDef = BuildTypeDef(dataType, charMax, numPrec, numScale);
            var identDef = isIdent ? $" IDENTITY({identSeed},{identIncr})" : "";
            var nullDef  = isNullable ? " NULL" : " NOT NULL";
            var defDef   = defVal != null ? $" DEFAULT {defVal}" : "";

            defs.Add($"[{colName}] {typeDef}{identDef}{defDef}{nullDef}");
        }
        return defs;
    }

    private static string BuildTypeDef(string dt, int? charMax, int? numPrec, int? numScale)
    {
        return dt switch
        {
            "VARCHAR"   or "NVARCHAR"   or "CHAR" or "NCHAR" or "BINARY" or "VARBINARY"
                => (charMax == null || charMax == -1)
                    ? $"{dt}(MAX)"
                    : $"{dt}({charMax})",

            "DECIMAL"   or "NUMERIC"
                => $"{dt}({numPrec ?? 18},{numScale ?? 2})",

            "FLOAT"     => $"FLOAT({numPrec ?? 53})",

            "DATETIME2" or "TIME" or "DATETIMEOFFSET"
                => numScale.HasValue ? $"{dt}({numScale})" : dt,

            _ => dt
        };
    }

    private static async Task<List<string>> GetPrimaryKeyColsAsync(
        SqlConnection conn, string schema, string table)
    {
        const string sql = @"
            SELECT c.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE c
                ON tc.CONSTRAINT_NAME = c.CONSTRAINT_NAME
               AND tc.TABLE_SCHEMA    = c.TABLE_SCHEMA
               AND tc.TABLE_NAME      = c.TABLE_NAME
            WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
              AND tc.TABLE_SCHEMA = @schema AND tc.TABLE_NAME = @table
            ORDER BY c.ORDINAL_POSITION";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table",  table);

        var cols = new List<string>();
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync()) cols.Add(rdr.GetString(0));
        return cols;
    }

    private static async Task<string?> GetPrimaryKeyNameAsync(
        SqlConnection conn, string schema, string table)
    {
        const string sql = @"
            SELECT CONSTRAINT_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
            WHERE CONSTRAINT_TYPE = 'PRIMARY KEY'
              AND TABLE_SCHEMA = @schema AND TABLE_NAME = @table";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table",  table);
        return (string?)await cmd.ExecuteScalarAsync();
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
