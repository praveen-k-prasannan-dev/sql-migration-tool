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

        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();
            var trimmed = batch.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            await using var cmd = new SqlCommand(trimmed, conn)
            {
                CommandTimeout = 300
            };
            await cmd.ExecuteNonQueryAsync(ct);
        }
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
                cmd.Parameters.Add(new SqlParameter($"@p{i}", DBNull.Value));

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

    private static object? ConvertJsonElement(JsonElement el, string? sqlType = null)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Null    => null,
            JsonValueKind.True    => true,
            JsonValueKind.False   => false,
            JsonValueKind.Number  => el.TryGetInt64(out var l)  ? l
                                   : el.TryGetDouble(out var d) ? d
                                   : (object?)el.GetDecimal(),
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
        return JsonSerializer.Deserialize<InstallManifest>(json, _jsonOptions)
            ?? throw new InvalidDataException("Failed to parse app_install.config.json");
    }
}
