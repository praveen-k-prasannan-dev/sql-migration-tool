namespace SqlMigrationTool.Models;

public enum InstallStatus { Pending, Running, Done, Warning, Error }

public class InstallProgress
{
    public ManifestEntry Entry   { get; }
    public InstallStatus Status  { get; }
    public string        Message { get; }

    public InstallProgress(ManifestEntry entry, InstallStatus status, string message = "")
    {
        Entry   = entry;
        Status  = status;
        Message = message;
    }
}
