using SqlMigrationTool.Models;

namespace TestConsole;

public sealed class Options
{
    public string  Command    { get; private set; } = "";
    public string  Server     { get; private set; } = ".";
    public string? User       { get; private set; }
    public string? Password   { get; private set; }
    public string  SourceDb   { get; private set; } = "SqlMigTest_Source";
    public string  TargetDb   { get; private set; } = "SqlMigTest_Target";
    public string? ScriptPath { get; private set; }
    public int     ChunkSize  { get; private set; } = 2;   // small on purpose: exercises chunk paging
    public bool    SkipSetup  { get; private set; }
    public bool    Verbose    { get; private set; }
    public bool    Reinstall  { get; private set; }
    public bool    KeepTarget { get; private set; }
    public bool    Cleanup    { get; private set; }
    public bool    Preview    { get; private set; }
    public bool    ApplyCompat { get; private set; }
    public bool    MatchCollation { get; private set; }
    public bool    Security   { get; private set; }
    public ScriptMode Mode    { get; private set; } = ScriptMode.DropAndCreate;
    public ScriptMode? InstallMode { get; private set; }
    public DataLoadMode DataMode { get; private set; } = DataLoadMode.SkipIfNotEmpty;
    public string? Folder     { get; private set; }

    public string? SourceServer { get; private set; }

    /// <summary>The source database; on --source-server when given, else on --server.</summary>
    public ConnectionConfig SourceConfig()
    {
        var cfg = Config(SourceDb);
        if (SourceServer != null) cfg.Server = SourceServer;
        return cfg;
    }

    public ConnectionConfig Config(string database) => new()
    {
        Server      = Server,
        Port        = 0,
        WindowsAuth = User is null,
        Username    = User ?? "",
        Password    = Password ?? "",
        Database    = database
    };

    public static Options? Parse(string[] args)
    {
        if (args.Length == 0) return null;

        var o = new Options { Command = args[0].ToLowerInvariant() };
        if (o.Command is not ("setup" or "roundtrip" or "compare" or "install" or "rewrapcheck" or "selftest")) return null;

        for (int i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");

            switch (args[i])
            {
                case "--server":     o.Server     = Next(); break;
                case "--source-server": o.SourceServer = Next(); break;
                case "--user":       o.User       = Next(); break;
                case "--password":   o.Password   = Next(); break;
                case "--source":     o.SourceDb   = Next(); break;
                case "--target":     o.TargetDb   = Next(); break;
                case "--script":     o.ScriptPath = Next(); break;
                case "--chunk":      o.ChunkSize  = int.Parse(Next()); break;
                case "--skip-setup": o.SkipSetup  = true; break;
                case "--verbose":    o.Verbose    = true; break;
                case "--reinstall":  o.Reinstall  = true; break;
                case "--keep-target": o.KeepTarget = true; break;
                case "--cleanup":    o.Cleanup    = true; break;
                case "--preview":    o.Preview    = true; break;
                case "--apply-compat": o.ApplyCompat = true; break;
                case "--match-collation": o.MatchCollation = true; break;
                case "--security":   o.Security   = true; break;
                case "--mode":       o.Mode       = Enum.Parse<ScriptMode>(Next(), ignoreCase: true); break;
                case "--install-mode": o.InstallMode = Enum.Parse<ScriptMode>(Next(), ignoreCase: true); break;
                case "--data-mode":  o.DataMode   = Enum.Parse<DataLoadMode>(Next(), ignoreCase: true); break;
                case "--folder":     o.Folder     = Next(); break;
                default: return null;
            }
        }
        return o;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            SQL Migration Tool — round-trip test harness

            Usage: TestConsole <command> [options]

            Commands:
              setup       Recreate the source test database from TestData/full_feature_db.sql
              roundtrip   setup + back up source + install into a fresh target + compare
              compare     Compare source and target databases only
              install     Install an existing backup (--folder) into a fresh target
              rewrapcheck Back up in every mode and check re-wrapping between modes is exact
              selftest    Offline checks of helpers (GO splitter, ...)

            Options:
              --server <name>      SQL Server instance (default: .)
              --source-server <n>  instance of the source database, when it differs from --server
              --user <login>       SQL authentication login (default: Windows authentication)
              --password <pwd>     SQL authentication password
              --source <db>        Source database (default: SqlMigTest_Source)
              --target <db>        Target database (default: SqlMigTest_Target)
              --script <path>      Test database script (default: TestData/full_feature_db.sql)
              --chunk <rows>       Backup chunk size (default: 2)
              --skip-setup         roundtrip: use the existing source database as-is
              --mode <mode>        roundtrip: script mode DropAndCreate | CreateOnly | CreateOrAlter
              --reinstall          roundtrip: install a second time over the populated target
              --cleanup            roundtrip: also write the cleanup (drop-all) script
              --security           roundtrip: include users, roles and permissions
              --install-mode <m>   install with this mode instead of the backup's (re-wraps scripts)
              --data-mode <m>      SkipIfNotEmpty (default) | TruncateAndLoad | Append | Upsert
              --verbose            Show every backup/install progress line
              --folder <path>      install: backup folder containing app_install.config.json
              --keep-target        install: install over the existing target instead of recreating it
              --preview            install: write preview_install.sql only; execute nothing
              --apply-compat       set the target to the source's compatibility level (capped by the server)
              --match-collation    give an empty target the source's database collation

            Only databases named SqlMigTest_* are ever dropped or recreated.
            """);
    }
}
