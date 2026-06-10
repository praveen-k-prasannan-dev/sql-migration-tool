using System.Reflection;

namespace SqlMigrationTool;

internal static class AppImage
{
    private static Bitmap? _cached;

    // Application logo bitmap (image1.png embedded resource).
    // NOTE: the stream must remain open for the lifetime of the Bitmap.
    public static Bitmap Logo => _cached ??= Load();

    private static Bitmap Load()
    {
        const string name   = "SqlMigrationTool.Resources.image1.png";
        var          stream = Assembly.GetExecutingAssembly()
                                      .GetManifestResourceStream(name);
        return stream is not null ? new Bitmap(stream) : new Bitmap(1, 1);
    }
}
