namespace SqlMigrationTool.Models;

public class DatabaseObject
{
    public string Schema   { get; set; } = "dbo";
    public string Name     { get; set; } = "";

    // One of the ObjectTypes constants
    public string Type     { get; set; } = "";

    public string FullName => $"{Schema}.{Name}";

    public long RowCount   { get; set; }

    public bool Selected   { get; set; } = true;

    public override string ToString() => $"[{Type,-8}] {FullName}";
}
