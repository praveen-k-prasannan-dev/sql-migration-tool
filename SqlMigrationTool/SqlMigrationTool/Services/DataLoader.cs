using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlMigrationTool.Models;

namespace SqlMigrationTool.Services;

/// <summary>
/// Loads data chunk files into the target for a <see cref="DataLoadMode"/>.
/// Per table, the first chunk decides what happens (skip, truncate, upsert...).
/// Around the whole data phase, <see cref="BeginPhaseAsync"/> switches off the
/// foreign keys and triggers involving the loaded tables (rows may arrive child
/// before parent, and existing triggers must not fire on restored rows), and
/// <see cref="EndPhaseAsync"/> switches them back on.
/// </summary>
public sealed class DataLoader
{
    private readonly ConnectionConfig _cfg;
    private readonly DataLoadMode     _mode;

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class TableState
    {
        public bool          Skip;
        public string?       SkipReason;
        public List<string>? KeyColumns;   // Upsert
    }

    private readonly Dictionary<string, TableState> _tables = new(StringComparer.OrdinalIgnoreCase);

    // Constraints (foreign keys, CHECKs) and triggers switched off by BeginPhaseAsync, to restore afterwards.
    private readonly List<(string table, string fk, bool trusted)> _disabledFks      = new();
    private readonly List<(string table, string trigger)>          _disabledTriggers = new();

    public DataLoader(ConnectionConfig cfg, DataLoadMode mode)
    {
        _cfg  = cfg;
        _mode = mode;
    }

    // ── Phase guard ─────────────────────────────────────────────────────────

    /// <param name="tables">Tables about to be loaded, as schema.name.</param>
    public async Task BeginPhaseAsync(IEnumerable<string> tables, CancellationToken ct)
    {
        var ids = tables.Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(n => $"SELECT OBJECT_ID({SqlNames.Literal(QuoteName(n))}, 'U')")
                        .ToList();
        if (ids.Count == 0) return;

        var sql = $@"
            DECLARE @ids TABLE (id int);
            INSERT @ids {string.Join(" UNION ALL ", ids)};

            -- Foreign keys from or to the loaded tables, and CHECK constraints on them
            -- (an enabled but untrusted CHECK may reject rows the source holds).
            SELECT QUOTENAME(OBJECT_SCHEMA_NAME(fk.parent_object_id)) + '.' + QUOTENAME(OBJECT_NAME(fk.parent_object_id)),
                   fk.name, CAST(CASE WHEN fk.is_not_trusted = 0 THEN 1 ELSE 0 END AS bit)
            FROM sys.foreign_keys fk
            WHERE fk.is_disabled = 0
              AND (fk.parent_object_id IN (SELECT id FROM @ids) OR fk.referenced_object_id IN (SELECT id FROM @ids))
            UNION ALL
            SELECT QUOTENAME(OBJECT_SCHEMA_NAME(cc.parent_object_id)) + '.' + QUOTENAME(OBJECT_NAME(cc.parent_object_id)),
                   cc.name, CAST(CASE WHEN cc.is_not_trusted = 0 THEN 1 ELSE 0 END AS bit)
            FROM sys.check_constraints cc
            WHERE cc.is_disabled = 0 AND cc.parent_object_id IN (SELECT id FROM @ids);

            SELECT QUOTENAME(OBJECT_SCHEMA_NAME(t.parent_id)) + '.' + QUOTENAME(OBJECT_NAME(t.parent_id)),
                   QUOTENAME(OBJECT_SCHEMA_NAME(t.object_id)) + '.' + QUOTENAME(t.name)
            FROM sys.triggers t
            WHERE t.parent_class = 1 AND t.is_disabled = 0 AND t.parent_id IN (SELECT id FROM @ids);";

        await using var conn = await SqlConnectionService.OpenAsync(_cfg);
        await using (var cmd = new SqlCommand(sql, conn))
        await using (var rdr = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rdr.ReadAsync(ct)) _disabledFks.Add((rdr.GetString(0), rdr.GetString(1), rdr.GetBoolean(2)));
            await rdr.NextResultAsync(ct);
            while (await rdr.ReadAsync(ct)) _disabledTriggers.Add((rdr.GetString(0), rdr.GetString(1)));
        }

        foreach (var (table, fk, _) in _disabledFks)
            await ExecAsync(conn, $"ALTER TABLE {table} NOCHECK CONSTRAINT {SqlNames.Quote(fk)};", ct);
        foreach (var (table, trigger) in _disabledTriggers)
            await ExecAsync(conn, $"DISABLE TRIGGER {trigger} ON {table};", ct);
    }

    /// <summary>
    /// Switches the foreign keys and triggers back on. Keys that were trusted are
    /// re-checked (WITH CHECK); a key the loaded rows violate stays disabled and
    /// is returned as a problem.
    /// </summary>
    public async Task<List<string>> EndPhaseAsync()
    {
        var problems = new List<string>();
        if (_disabledFks.Count == 0 && _disabledTriggers.Count == 0) return problems;

        await using var conn = await SqlConnectionService.OpenAsync(_cfg);
        foreach (var (table, fk, trusted) in _disabledFks)
        {
            try
            {
                await ExecAsync(conn, $"ALTER TABLE {table} WITH {(trusted ? "CHECK" : "NOCHECK")} CHECK CONSTRAINT {SqlNames.Quote(fk)};", CancellationToken.None);
            }
            catch (SqlException ex)
            {
                problems.Add($"Constraint {SqlNames.Quote(fk)} on {table} left disabled: {ex.Message}");
            }
        }
        foreach (var (table, trigger) in _disabledTriggers)
        {
            try   { await ExecAsync(conn, $"ENABLE TRIGGER {trigger} ON {table};", CancellationToken.None); }
            catch (SqlException ex) { problems.Add($"Trigger {trigger} on {table} could not be re-enabled: {ex.Message}"); }
        }

        _disabledFks.Clear();
        _disabledTriggers.Clear();
        return problems;
    }

    // ── Chunk loading ───────────────────────────────────────────────────────

    /// <summary>Loads one chunk file; returns warnings (e.g. the table was skipped).</summary>
    public async Task<List<string>> LoadChunkAsync(string filePath, CancellationToken ct)
    {
        var warnings = new List<string>();
        var json  = await File.ReadAllTextAsync(filePath, ct);
        var chunk = JsonSerializer.Deserialize<DataChunk>(json, _jsonOptions);
        if (chunk is null || chunk.Rows.Count == 0) return warnings;

        var (schema, table) = SplitName(chunk.Table);
        var name = SqlNames.Quote(schema, table);

        await using var conn = await SqlConnectionService.OpenAsync(_cfg);

        if (!_tables.TryGetValue(chunk.Table, out var state))
        {
            _tables[chunk.Table] = state = await DecideAsync(conn, name, ct);
            if (state.Skip) warnings.Add(state.SkipReason!);
        }
        if (state.Skip) return warnings;

        var hasIdentity = await HasIdentityColumnAsync(conn, schema, table, ct);
        var colTypes    = await GetColumnTypesAsync(conn, schema, table, chunk.Columns, ct);
        var cols        = string.Join(", ", chunk.Columns.Select(SqlNames.Quote));

        await using var tran = conn.BeginTransaction();
        try
        {
            if (state.KeyColumns is { } keys)
            {
                // Upsert: rows go to a staging table, then MERGE into the target.
                // UNION ALL keeps SELECT INTO from copying the IDENTITY property; typed
                // XML is staged untyped (its schema collection cannot exist in tempdb)
                // and validated again when MERGE writes it.
                var stageCols = string.Join(", ", chunk.Columns.Select((c, i) =>
                    i < colTypes.Count && colTypes[i] == "XML"
                        ? $"CAST({SqlNames.Quote(c)} AS xml) AS {SqlNames.Quote(c)}"
                        : SqlNames.Quote(c)));
                await ExecAsync(conn, $"SELECT TOP 0 {stageCols} INTO #stage FROM {name} UNION ALL SELECT TOP 0 {stageCols} FROM {name};", ct, tran);
                await InsertRowsAsync(conn, tran, "#stage", chunk, colTypes, ct);

                var identityCols = hasIdentity ? await GetIdentityColumnsAsync(conn, tran, name, ct) : new();
                var on      = string.Join(" AND ", keys.Select(k => $"t.{SqlNames.Quote(k)} = s.{SqlNames.Quote(k)}"));
                var updates = chunk.Columns
                    .Where(c => !keys.Contains(c, StringComparer.OrdinalIgnoreCase) &&
                                !identityCols.Contains(c, StringComparer.OrdinalIgnoreCase))   // identity cannot be updated
                    .Select(c => $"t.{SqlNames.Quote(c)} = s.{SqlNames.Quote(c)}")
                    .ToList();
                var values  = string.Join(", ", chunk.Columns.Select(c => $"s.{SqlNames.Quote(c)}"));

                var merge = $"MERGE {name} AS t USING #stage AS s ON {on}" +
                            (updates.Count > 0 ? $" WHEN MATCHED THEN UPDATE SET {string.Join(", ", updates)}" : "") +
                            $" WHEN NOT MATCHED BY TARGET THEN INSERT ({cols}) VALUES ({values});";

                if (hasIdentity) await ExecAsync(conn, $"SET IDENTITY_INSERT {name} ON;", ct, tran);
                await ExecAsync(conn, merge, ct, tran);
                if (hasIdentity) await ExecAsync(conn, $"SET IDENTITY_INSERT {name} OFF;", ct, tran);
                await ExecAsync(conn, "DROP TABLE #stage;", ct, tran);
            }
            else
            {
                if (hasIdentity) await ExecAsync(conn, $"SET IDENTITY_INSERT {name} ON;", ct, tran);
                await InsertRowsAsync(conn, tran, name, chunk, colTypes, ct);
                if (hasIdentity) await ExecAsync(conn, $"SET IDENTITY_INSERT {name} OFF;", ct, tran);
            }

            await tran.CommitAsync(ct);
        }
        catch
        {
            await tran.RollbackAsync(CancellationToken.None);
            throw;
        }
        return warnings;
    }

    /// <summary>What to do with a table, decided when its first chunk arrives.</summary>
    private async Task<TableState> DecideAsync(SqlConnection conn, string name, CancellationToken ct)
    {
        await using var countCmd = new SqlCommand($"SELECT CASE WHEN EXISTS (SELECT 1 FROM {name}) THEN 1 ELSE 0 END", conn);
        var hasRows = (int)(await countCmd.ExecuteScalarAsync(ct))! == 1;

        switch (_mode)
        {
            case DataLoadMode.SkipIfNotEmpty when hasRows:
                return new() { Skip = true, SkipReason = $"{name} already has rows; data not loaded (Skip tables that have rows)." };

            case DataLoadMode.TruncateAndLoad when hasRows:
                try
                {
                    await ExecAsync(conn, $"TRUNCATE TABLE {name};", ct);
                }
                catch (SqlException)
                {
                    // TRUNCATE is refused for tables referenced by foreign keys or
                    // indexed views even when the keys are disabled; DELETE is not.
                    await ExecAsync(conn, $"DELETE FROM {name};", ct);
                }
                return new();

            case DataLoadMode.Upsert:
                var keys = await GetKeyColumnsAsync(conn, name, ct);
                if (keys.Count > 0) return new() { KeyColumns = keys };
                return hasRows
                    ? new() { Skip = true, SkipReason = $"{name} has no primary or unique key to upsert on and already has rows; data not loaded." }
                    : new();

            default:   // Append, or an empty table
                return new();
        }
    }

    private static async Task InsertRowsAsync(SqlConnection conn, SqlTransaction tran, string target,
        DataChunk chunk, List<string?> colTypes, CancellationToken ct)
    {
        var colList   = string.Join(", ", chunk.Columns.Select(SqlNames.Quote));
        var paramList = string.Join(", ", chunk.Columns.Select((_, i) => $"@p{i}"));

        await using var cmd = new SqlCommand($"INSERT INTO {target} ({colList}) VALUES ({paramList})", conn, tran);
        for (int i = 0; i < chunk.Columns.Count; i++)
        {
            var p = new SqlParameter($"@p{i}", DBNull.Value);
            ConfigureParameterType(p, i < colTypes.Count ? colTypes[i] : null);
            cmd.Parameters.Add(p);
        }

        foreach (var row in chunk.Rows)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < chunk.Columns.Count; i++)
            {
                var raw     = i < row.Length ? row[i] : null;
                var sqlType = i < colTypes.Count ? colTypes[i] : null;
                var val     = raw is JsonElement je ? ConvertJsonElement(je, sqlType) : raw;
                cmd.Parameters[$"@p{i}"].Value = val ?? (object)DBNull.Value;
            }
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Primary key columns, else those of the first unfiltered unique index.</summary>
    private static async Task<List<string>> GetKeyColumnsAsync(SqlConnection conn, string name, CancellationToken ct)
    {
        var sql = $@"
            SELECT TOP 1 WITH TIES c.name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
            JOIN sys.columns c        ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID({SqlNames.Literal(name)}) AND i.is_unique = 1 AND i.has_filter = 0
              AND i.type IN (1, 2) AND i.is_disabled = 0
            ORDER BY DENSE_RANK() OVER (ORDER BY i.is_primary_key DESC, i.index_id)";

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var keys = new List<string>();
        while (await rdr.ReadAsync(ct)) keys.Add(rdr.GetString(0));
        return keys;
    }

    private static async Task<List<string>> GetIdentityColumnsAsync(SqlConnection conn, SqlTransaction tran, string name, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            $"SELECT name FROM sys.identity_columns WHERE object_id = OBJECT_ID({SqlNames.Literal(name)})", conn, tran);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var cols = new List<string>();
        while (await rdr.ReadAsync(ct)) cols.Add(rdr.GetString(0));
        return cols;
    }

    private static async Task ExecAsync(SqlConnection conn, string sql, CancellationToken ct, SqlTransaction? tran = null)
    {
        await using var cmd = new SqlCommand(sql, conn, tran) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static (string schema, string table) SplitName(string fullName)
    {
        var parts = fullName.Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", parts[0]);
    }

    private static string QuoteName(string fullName)
    {
        var (schema, table) = SplitName(fullName);
        return SqlNames.Quote(schema, table);
    }

    // ── Column types and value conversion (moved from InstallService) ───────

    private static async Task<bool> HasIdentityColumnAsync(
        SqlConnection conn, string schema, string table, CancellationToken ct)
    {
        const string sql = @"
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(@fqn) AND is_identity = 1";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@fqn", $"{schema}.{table}");
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result) > 0;
    }

    private static async Task<List<string?>> GetColumnTypesAsync(
        SqlConnection conn, string schema, string table,
        List<string> columns, CancellationToken ct)
    {
        const string sql = @"
            SELECT c.name, t.name
            FROM sys.columns c
            JOIN sys.types t ON c.user_type_id = t.user_type_id
            WHERE c.object_id = OBJECT_ID(@fqn)";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@fqn", $"{schema}.{table}");

        var typeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync())
            typeMap[rdr.GetString(0)] = rdr.GetString(1).ToUpper();

        return columns.Select(c => typeMap.TryGetValue(c, out var t) ? t : (string?)null).ToList();
    }

    /// <summary>
    /// Fixes the parameter type where inferring it from the .NET value loses data:
    /// a DateTime would be sent as legacy DATETIME (~3 ms precision, rounding
    /// datetime2 values), and an untyped NULL as nvarchar, which SQL Server will
    /// not convert implicitly to a binary column.
    /// </summary>
    private static void ConfigureParameterType(SqlParameter p, string? sqlType)
    {
        switch (sqlType)
        {
            case "DATETIME2":
                p.SqlDbType = System.Data.SqlDbType.DateTime2;
                p.Scale     = 7;
                break;
            case "DATE":
                p.SqlDbType = System.Data.SqlDbType.Date;
                break;
            case "TIME":
                p.SqlDbType = System.Data.SqlDbType.Time;
                p.Scale     = 7;
                break;
            case "DATETIMEOFFSET":
                p.SqlDbType = System.Data.SqlDbType.DateTimeOffset;
                p.Scale     = 7;
                break;
            case "BINARY" or "VARBINARY" or "IMAGE":
                p.SqlDbType = System.Data.SqlDbType.VarBinary;
                p.Size      = -1;
                break;
        }
    }

    private static object? ConvertJsonElement(JsonElement el, string? sqlType = null)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Null    => null,
            JsonValueKind.True    => true,
            JsonValueKind.False   => false,
            // Prefer decimal over double so DECIMAL/NUMERIC/MONEY keep every digit;
            // only FLOAT/REAL columns (and values too large for decimal) use double.
            JsonValueKind.Number  => el.TryGetInt64(out var l)                    ? l
                                   : sqlType is "FLOAT" or "REAL"                 ? el.GetDouble()
                                   : el.TryGetDecimal(out var m)                  ? m
                                   : (object?)el.GetDouble(),
            JsonValueKind.String  => ConvertStringValue(el.GetString(), sqlType),
            _                     => el.ToString()
        };
    }

    private static object? ConvertStringValue(string? s, string? sqlType)
    {
        if (s is null) return null;

        switch (sqlType)
        {
            case "IMAGE" or "VARBINARY" or "BINARY" or "TIMESTAMP" or "ROWVERSION":
                try { return Convert.FromBase64String(s); }
                catch { return s; }

            case "DATETIME" or "DATETIME2" or "SMALLDATETIME" or "DATE":
                if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    return dt;
                break;

            case "DATETIMEOFFSET":
                if (DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dto))
                    return dto;
                break;

            case "TIME":
                if (TimeSpan.TryParse(s, out var ts))
                    return ts;
                break;
        }

        return s;
    }

}
