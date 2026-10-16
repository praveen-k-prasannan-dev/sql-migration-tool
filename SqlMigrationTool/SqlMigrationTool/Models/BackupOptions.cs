namespace SqlMigrationTool.Models;

/// <summary>Choices for a backup beyond the object selection and chunk size.</summary>
public sealed record BackupOptions
{
    /// <summary>How the scripts treat objects that already exist on the target (default for installing).</summary>
    public ScriptMode Mode { get; init; } = ScriptMode.DropAndCreate;

    /// <summary>Also write cleanup_drop_all.sql (never run by the tool).</summary>
    public bool WriteCleanupScript { get; init; }

    /// <summary>Export database users, roles, role memberships and permissions.</summary>
    public bool IncludeSecurity { get; init; }
}
