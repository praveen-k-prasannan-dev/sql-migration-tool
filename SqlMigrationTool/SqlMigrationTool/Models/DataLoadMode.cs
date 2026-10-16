namespace SqlMigrationTool.Models;

/// <summary>How table data is loaded when the target table may already contain rows.</summary>
public enum DataLoadMode
{
    /// <summary>Load a table only when it is empty; otherwise skip it with a warning.</summary>
    SkipIfNotEmpty,

    /// <summary>Remove all rows from the table, then load.</summary>
    TruncateAndLoad,

    /// <summary>Insert every row (v1 behaviour); may fail on duplicate keys.</summary>
    Append,

    /// <summary>Update rows whose key exists, insert the rest (MERGE on the primary/unique key).</summary>
    Upsert
}

public static class DataLoadModeText
{
    public static string Label(DataLoadMode mode) => mode switch
    {
        DataLoadMode.SkipIfNotEmpty  => "Skip tables that have rows",
        DataLoadMode.TruncateAndLoad => "Truncate and load",
        DataLoadMode.Append          => "Append",
        DataLoadMode.Upsert          => "Upsert (update or insert by key)",
        _                            => mode.ToString()
    };
}

/// <summary>A DataLoadMode as a combo-box item, shown by its label.</summary>
public sealed record DataLoadModeItem(DataLoadMode Mode)
{
    public override string ToString() => DataLoadModeText.Label(Mode);
}
