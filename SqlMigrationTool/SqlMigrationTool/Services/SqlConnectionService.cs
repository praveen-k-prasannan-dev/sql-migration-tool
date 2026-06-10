using Microsoft.Data.SqlClient;
using SqlMigrationTool.Models;

namespace SqlMigrationTool.Services;

public static class SqlConnectionService
{
    public static async Task<(bool ok, string message)> TestAsync(ConnectionConfig cfg)
    {
        try
        {
            await using var conn = new SqlConnection(cfg.BuildConnectionString());
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
            var dbName = (string?)await cmd.ExecuteScalarAsync();
            return (true, $"Connected to {cfg.Server} \\ {dbName}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message.Length > 250
                ? ex.Message[..250] + "…"
                : ex.Message);
        }
    }

    public static async Task<SqlConnection> OpenAsync(ConnectionConfig cfg)
    {
        var conn = new SqlConnection(cfg.BuildConnectionString());
        await conn.OpenAsync();
        return conn;
    }
}
