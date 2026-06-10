using System.Text;
using System.Text.Json;
using SqlMigrationTool.Models;

namespace SqlMigrationTool.Services;

public static class ProfileService
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private static string ProfilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SqlMigrationTool",
        "profiles.json");

    public static List<ConnectionProfile> Load()
    {
        if (!File.Exists(ProfilePath)) return new();
        try
        {
            var text = File.ReadAllText(ProfilePath);
            return JsonSerializer.Deserialize<List<ConnectionProfile>>(text, _json) ?? new();
        }
        catch { return new(); }
    }

    public static void Save(ConnectionProfile profile)
    {
        var list    = Load();
        var idx     = list.FindIndex(p => p.Name == profile.Name);
        if (idx >= 0) list[idx] = profile;
        else          list.Add(profile);

        Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
        File.WriteAllText(ProfilePath, JsonSerializer.Serialize(list, _json));
    }

    public static string MakeName(string server, string database)
        => $"{server}  —  {database}";

    public static string EncodePassword(string pwd)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(pwd));

    public static string DecodePassword(string encoded)
    {
        try   { return Encoding.UTF8.GetString(Convert.FromBase64String(encoded ?? "")); }
        catch { return ""; }
    }
}
