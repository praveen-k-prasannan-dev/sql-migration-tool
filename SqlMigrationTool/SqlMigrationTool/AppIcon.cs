using System.Reflection;

namespace SqlMigrationTool;

internal static class AppIcon
{
    private static Icon? _cached;

    public static Icon Instance => _cached ??= Load();

    private static Icon Load()
    {
        const string name   = "SqlMigrationTool.Resources.app.ico";
        var          stream = Assembly.GetExecutingAssembly()
                                      .GetManifestResourceStream(name);
        return stream is not null ? new Icon(stream) : SystemIcons.Application;
    }
}
