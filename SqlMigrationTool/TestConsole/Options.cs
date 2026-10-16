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
    public string? Folder     { get; private set; }

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
        if (o.Command is not ("setup" or "roundtrip" or "compare" or "install")) return null;

        for (int i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");

            switch (args[i])
            {
                case "--server":     o.Server     = Next(); break;
                case "--user":       o.User       = Next(); break;
                case "--password":   o.Password   = Next(); break;
                case "--source":     o.SourceDb   = Next(); break;
                case "--target":     o.TargetDb   = Next(); break;
                case "--script":     o.ScriptPath = Next(); break;
                case "--chunk":      o.ChunkSize  = int.Parse(Next()); break;
                case "--skip-setup": o.SkipSetup  = true; break;
                case "--verbose":    o.Verbose    = true; break;
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

            Options:
              --server <name>      SQL Server instance (default: .)
              --user <login>       SQL authentication login (default: Windows authentication)
              --password <pwd>     SQL authentication password
              --source <db>        Source database (default: SqlMigTest_Source)
              --target <db>        Target database (default: SqlMigTest_Target)
              --script <path>      Test database script (default: TestData/full_feature_db.sql)
              --chunk <rows>       Backup chunk size (default: 2)
              --skip-setup         roundtrip: use the existing source database as-is
              --verbose            Show every backup/install progress line
              --folder <path>      install: backup folder containing app_install.config.json

            Only databases named SqlMigTest_* are ever dropped or recreated.
            """);
    }
}
