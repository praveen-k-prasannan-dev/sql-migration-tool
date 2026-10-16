namespace SqlMigrationTool.Services;

/// <summary>Safe quoting for names and literals embedded in generated T-SQL.</summary>
public static class SqlNames
{
    /// <summary>[name] with any ] doubled.</summary>
    public static string Quote(string name) => $"[{name.Replace("]", "]]")}]";

    /// <summary>[schema].[name]</summary>
    public static string Quote(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";

    /// <summary>N'text' with any ' doubled.</summary>
    public static string Literal(string text) => $"N'{text.Replace("'", "''")}'";
}
