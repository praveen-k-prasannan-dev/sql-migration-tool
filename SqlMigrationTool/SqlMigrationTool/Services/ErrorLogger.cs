using SqlMigrationTool.Models;

namespace SqlMigrationTool.Services;

public class ErrorLogger
{
    private readonly string _path;
    private readonly object _lock = new();
    public  int ErrorCount { get; private set; }

    public ErrorLogger(string folder)
    {
        _path = Path.Combine(folder, "error.log");
        var header = $"""
            ================================================================================
            SQL Migration Tool — Error Log
            Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
            ================================================================================

            """;
        File.WriteAllText(_path, header);
    }

    public void Log(ManifestEntry entry, Exception ex, string? extraContext = null)
    {
        lock (_lock)
        {
            ErrorCount++;
            var text = $"""
                [{DateTime.Now:HH:mm:ss}] [ERROR] File: {entry.File}
                  Object: {entry.Object}
                  Error:  {ex.Message}
                {(extraContext != null ? $"  Detail: {extraContext}\n" : "")}  Action: Skipped — continuing.

                """;
            File.AppendAllText(_path, text);
        }
    }

    public int WarningCount { get; private set; }

    /// <summary>Warnings raised by a script that otherwise installed (e.g. column differences).</summary>
    public void LogWarnings(ManifestEntry entry, IReadOnlyList<string> warnings)
    {
        lock (_lock)
        {
            WarningCount += warnings.Count;
            var text = $"[{DateTime.Now:HH:mm:ss}] [WARNING] File: {entry.File}\n" +
                       $"  Object: {entry.Object}\n" +
                       string.Concat(warnings.Select(w => $"  - {w}\n")) +
                       "  Action: Installed; review the items above.\n\n";
            File.AppendAllText(_path, text);
        }
    }

    public void WriteSummary()
    {
        lock (_lock)
        {
            var summary = $"""

                ================================================================================
                Summary: {ErrorCount} error(s), {WarningCount} warning(s). See entries above for details.
                ================================================================================
                """;
            File.AppendAllText(_path, summary);
        }
    }
}
