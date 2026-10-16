namespace SqlMigrationTool.Models;

/// <summary>
/// Object type codes used in <see cref="DatabaseObject.Type"/>, manifest entries
/// (<see cref="ManifestEntry.Type"/>) and progress reporting. The values are
/// written to app_install.config.json, so existing values must never change.
/// </summary>
public static class ObjectTypes
{
    public const string Table   = "TABLE";
    public const string View    = "VIEW";
    public const string Proc    = "PROC";
    public const string Func    = "FUNC";
    public const string Trigger = "TRIGGER";
    public const string Data    = "DATA";
    public const string Fk      = "FK";
    public const string Index   = "INDEX";
    public const string Schema   = "SCHEMA";
    public const string DataType  = "UDDT";
    public const string TableType = "UDTT";
    public const string Sequence  = "SEQUENCE";
    public const string Synonym   = "SYNONYM";
    public const string DbTrigger = "DBTRIGGER";
    public const string ExtendedProperty = "EXTPROP";
    public const string XmlSchema = "XMLSCHEMA";
    public const string Partition = "PARTITION";

    /// <summary>Display order and labels for the backup/install summary grids.</summary>
    public static readonly (string Type, string Label)[] SummaryRows =
    {
        (Schema,    "Schemas"),
        (XmlSchema, "XML Schema Collections"),
        (DataType,  "Data Types"),
        (TableType, "Table Types"),
        (Partition, "Partitioning"),
        (Sequence,  "Sequences"),
        (Synonym,   "Synonyms"),
        (Table,   "Tables"),
        (Data,    "Data Files"),
        (View,    "Views"),
        (Proc,    "Stored Procs"),
        (Func,    "Functions"),
        (Trigger, "Triggers"),
        (DbTrigger, "Database Triggers"),
        (ExtendedProperty, "Extended Properties"),
        (Fk,      "Foreign Keys"),
        (Index,   "Indexes"),
    };
}
