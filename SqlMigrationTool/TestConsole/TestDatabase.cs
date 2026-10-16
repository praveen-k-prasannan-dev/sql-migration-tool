using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlMigrationTool.Services;

namespace TestConsole;

/// <summary>Creates, drops and populates the throw-away SqlMigTest_* databases.</summary>
public static class TestDatabase
{
    private const string SafePrefix = "SqlMigTest_";

    private static readonly Regex GoSeparator =
        new(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public static async Task RecreateAsync(Options o, string database)
    {
        if (!database.StartsWith(SafePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to drop '{database}': only databases named {SafePrefix}* are recreated.");

        var quoted = database.Replace("]", "]]");
        var sql = $"""
            IF DB_ID(@db) IS NOT NULL
            BEGIN
                ALTER DATABASE [{quoted}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{quoted}];
            END;
            CREATE DATABASE [{quoted}];
            """;

        // Our own pooled connections to the database would block the drop.
        SqlConnection.ClearAllPools();

        // Generous timeout: SINGLE_USER can wait a long time on locks held by
        // background tasks or other tools (e.g. SSMS Object Explorer).
        await using var conn = await SqlConnectionService.OpenAsync(o.Config("master"));
        await using var cmd  = new SqlCommand(sql, conn) { CommandTimeout = 600 };
        cmd.Parameters.AddWithValue("@db", database);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task RunScriptAsync(Options o, string database, string scriptPath)
    {
        var batches = GoSeparator.Split(await File.ReadAllTextAsync(scriptPath))
                                 .Where(b => !string.IsNullOrWhiteSpace(b));

        await using var conn = await SqlConnectionService.OpenAsync(o.Config(database));
        foreach (var batch in batches)
        {
            await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public static string FindDefaultScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData", "full_feature_db.sql");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("TestData/full_feature_db.sql not found; pass --script <path>.");
    }
}
