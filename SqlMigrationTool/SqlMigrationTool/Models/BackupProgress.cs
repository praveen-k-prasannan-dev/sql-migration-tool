namespace SqlMigrationTool.Models;

public record BackupProgress(
    string  Message,
    string? Type    = null,
    bool?   Success = null
);
