using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlMigrationTool.Models;

namespace SqlMigrationTool.Services;

public class DataExporter
{
    private readonly ConnectionConfig _cfg;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false
    };

    public DataExporter(ConnectionConfig cfg) => _cfg = cfg;

    public async Task<List<(string filePath, int rows)>> ExportTableAsync(
        DatabaseObject    table,
        string            outputDir,
        int               chunkSize,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var results = new List<(string, int)>();
        Directory.CreateDirectory(outputDir);

        var columns = await GetColumnNamesAsync(table, ct);
        if (columns.Count == 0) return results;

        int offset = 0, chunk = 1;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var colSelect = string.Join(", ", columns.Select(c => $"[{c}]"));
            var sql = $@"
                SELECT {colSelect}
                FROM [{table.Schema}].[{table.Name}]
                ORDER BY (SELECT NULL)
                OFFSET {offset} ROWS
                FETCH NEXT {chunkSize} ROWS ONLY";

            var rows = await ReadRowsAsync(sql, ct);
            if (rows.Count == 0) break;

            var fileName = $"data_{chunk:D3}.json";
            var filePath = Path.Combine(outputDir, fileName);

            var chunkObj = new DataChunk
            {
                Table   = table.FullName,
                Chunk   = chunk,
                Columns = columns,
                Rows    = rows
            };

            await File.WriteAllTextAsync(filePath,
                JsonSerializer.Serialize(chunkObj, _jsonOptions), ct);

            progress.Report($"  {table.FullName} → chunk {chunk} ({rows.Count:N0} rows)");
            results.Add((filePath, rows.Count));

            if (rows.Count < chunkSize) break;
            offset += chunkSize;
            chunk++;
        }

        return results;
    }

    private async Task<List<string>> GetColumnNamesAsync(DatabaseObject table, CancellationToken ct)
    {
        const string sql = @"
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
              AND COLUMNPROPERTY(OBJECT_ID(TABLE_SCHEMA+'.'+TABLE_NAME), COLUMN_NAME, 'IsComputed') = 0
            ORDER BY ORDINAL_POSITION";

        await using var conn = await SqlConnectionService.OpenAsync(_cfg);
        await using var cmd  = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table",  table.Name);

        var cols = new List<string>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct)) cols.Add(rdr.GetString(0));
        return cols;
    }

    private async Task<List<object?[]>> ReadRowsAsync(string sql, CancellationToken ct)
    {
        await using var conn = await SqlConnectionService.OpenAsync(_cfg);
        await using var cmd  = new SqlCommand(sql, conn) { CommandTimeout = 300 };

        var rows = new List<object?[]>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        int fieldCount = rdr.FieldCount;
        while (await rdr.ReadAsync(ct))
        {
            var row = new object?[fieldCount];
            for (int i = 0; i < fieldCount; i++)
            {
                if (rdr.IsDBNull(i))
                {
                    row[i] = null;
                    continue;
                }

                var val = rdr.GetValue(i);
                row[i] = val switch
                {
                    byte[]   bytes => Convert.ToBase64String(bytes),
                    DateTime dt    => dt.ToString("O"),
                    DateOnly d     => d.ToString("yyyy-MM-dd"),
                    TimeOnly t     => t.ToString("HH:mm:ss.fffffff"),
                    TimeSpan ts    => ts.ToString("c"),
                    Guid g         => g.ToString(),
                    _              => val
                };
            }
            rows.Add(row);
        }
        return rows;
    }
}
