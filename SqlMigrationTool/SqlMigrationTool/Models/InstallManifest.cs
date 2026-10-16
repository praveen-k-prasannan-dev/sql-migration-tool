namespace SqlMigrationTool.Models;

public class InstallManifest
{
    /// <summary>Manifest format written by this build. 2.0 added Phase 0 (prerequisites) and DatabaseCollation.</summary>
    public const int CurrentMajorVersion = 2;
    public const string CurrentVersion   = "2.0";

    // Defaults to 1.0 so a manifest without a Version is read as the original format.
    public string              Version               { get; set; } = "1.0";
    public string              Created               { get; set; } = DateTime.Now.ToString("O");
    public string              SourceServer          { get; set; } = "";
    public string              SourceDatabase        { get; set; } = "";
    public int                 SqlCompatibilityLevel { get; set; } = 160;
    public string?             DatabaseCollation     { get; set; }
    /// <summary>ScriptMode the scripts were written with; the default for installing. Absent (v1): DropAndCreate.</summary>
    public string              ScriptMode            { get; set; } = nameof(Models.ScriptMode.DropAndCreate);
    public List<ManifestPhase> Phases                { get; set; } = new();
    public ManifestSummary     Summary               { get; set; } = new();
}

public class ManifestPhase
{
    public int                 Phase { get; set; }
    public string              Name  { get; set; } = "";
    public List<ManifestEntry> Files { get; set; } = new();
}

public class ManifestEntry
{
    public int    Order  { get; set; }
    public string Type   { get; set; } = "";
    public string Object { get; set; } = "";
    public string File   { get; set; } = "";
    public int    Rows   { get; set; }
}

public class ManifestSummary
{
    public int TotalTables     { get; set; }
    public int TotalViews      { get; set; }
    public int TotalProcedures { get; set; }
    public int TotalFunctions  { get; set; }
    public int TotalDataFiles  { get; set; }
    public int TotalFiles      { get; set; }
}
