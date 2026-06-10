namespace SqlMigrationTool.Models;

public class DataChunk
{
    public string          Table   { get; set; } = "";
    public int             Chunk   { get; set; }
    public List<string>    Columns { get; set; } = new();
    public List<object?[]> Rows    { get; set; } = new();
}
