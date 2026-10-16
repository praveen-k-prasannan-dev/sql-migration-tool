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
        CancellationToken          ct)
    {
        var logger = new ErrorLogger(folder);

        foreach (var phase in manifest.Phases)
        {
            foreach (var entry in phase.Files)
            {
                ct.ThrowIfCancellationRequested();

                progress.Report(new InstallProgress(entry, InstallStatus.Running));
                try
                {
                    var fullPath = Path.Combine(folder,
                        entry.File.Replace('/', Path.DirectorySeparatorChar));

                    if (entry.Type == ObjectTypes.Data)
                        await InsertDataFileAsync(fullPath, ct);
                    else
                        await ExecuteSqlFileAsync(fullPath, ct);

                    progress.Report(new InstallProgress(entry, InstallStatus.Done));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.Log(entry, ex);
                    progress.Report(new InstallProgress(entry, InstallStatus.Error, ex.Message));
                }
            }
        }

        logger.WriteSummary();
    }

    private async Task ExecuteSqlFileAsync(string filePath, CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(filePath, ct);

        // Split on GO batch separator (SQL Server Management Studio directive)
        var batches = content.Split(
            new[] { "\nGO\n", "\nGO\r\n", "\r\nGO\r\n", "\r\nGO\n", "\nGO", "\r\nGO" },
            StringSplitOptions.RemoveEmptyEntries);

        await using var conn = await SqlConnectionService.OpenAsync(_cfg);

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
    }

    private async Task InsertDataFileAsync(string filePath, CancellationToken ct)
    {
        var json  = await File.ReadAllTextAsync(filePath, ct);
        var chunk = JsonSerializer.Deserialize<DataChunk>(json, _jsonOptions);
        if (chunk is null || chunk.Rows.Count == 0) return;

        var schemaParts = chunk.Table.Split('.', 2);
        var schema      = schemaParts.Length == 2 ? schemaParts[0] : "dbo";
        var table       = schemaParts.Length == 2 ? schemaParts[1] : schemaParts[0];

        await using var conn = await SqlConnectionService.OpenAsync(_cfg);

        var hasIdentity = await HasIdentityColumnAsync(conn, schema, table, ct);
        var colTypes    = await GetColumnTypesAsync(conn, schema, table, chunk.Columns, ct);

        var colList   = string.Join(", ", chunk.Columns.Select(c => $"[{c}]"));
        var paramList = string.Join(", ", chunk.Columns.Select((_, i) => $"@p{i}"));
        var insertSql = $"INSERT INTO [{schema}].[{table}] ({colList}) VALUES ({paramList})";

        await using var tran = conn.BeginTransaction();
        try
        {
            if (hasIdentity)
            {
                await using var onCmd = new SqlCommand(
                    $"SET IDENTITY_INSERT [{schema}].[{table}] ON", conn, tran);
                await onCmd.ExecuteNonQueryAsync(ct);
            }

            await using var cmd = new SqlCommand(insertSql, conn, tran);
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
                    var val     = raw is JsonElement je
                        ? ConvertJsonElement(je, sqlType)
                        : raw;
                    cmd.Parameters[$"@p{i}"].Value = val ?? (object)DBNull.Value;
                }
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (hasIdentity)
            {
                await using var offCmd = new SqlCommand(
                    $"SET IDENTITY_INSERT [{schema}].[{table}] OFF", conn, tran);
                await offCmd.ExecuteNonQueryAsync(ct);
            }

            await tran.CommitAsync(ct);
        }
        catch
        {
            await tran.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

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
