using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlMigrationTool.Models;

namespace SqlMigrationTool.Services;

public static class ProfileService
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    // Saved passwords are encrypted with Windows DPAPI for the current user, so
    // profiles.json is unreadable to other accounts and on other machines.
    // v1 stored plain Base64; such entries are upgraded the first time they load.
    private const string ProtectedPrefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SqlMigrationTool.ConnectionProfile.v2");

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
            var list = JsonSerializer.Deserialize<List<ConnectionProfile>>(text, _json) ?? new();
            if (UpgradePasswords(list))
                Write(list);
            return list;
        }
        catch { return new(); }
    }

    public static void Save(ConnectionProfile profile)
    {
        var list    = Load();
        var idx     = list.FindIndex(p => p.Name == profile.Name);
        if (idx >= 0) list[idx] = profile;
        else          list.Add(profile);
        Write(list);
    }

    private static void Write(List<ConnectionProfile> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
        File.WriteAllText(ProfilePath, JsonSerializer.Serialize(list, _json));
    }

    /// <summary>Re-encrypts v1 Base64 passwords with DPAPI; returns true when any changed.</summary>
    public static bool UpgradePasswords(List<ConnectionProfile> profiles)
    {
        bool changed = false;
        foreach (var p in profiles.Where(p => !string.IsNullOrEmpty(p.Password) && !p.Password.StartsWith(ProtectedPrefix)))
        {
            p.Password = EncodePassword(DecodePassword(p.Password));
            changed    = true;
        }
        return changed;
    }

    public static string MakeName(string server, string database)
        => $"{server}  —  {database}";

    public static string EncodePassword(string pwd)
    {
        if (string.IsNullOrEmpty(pwd)) return "";
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(pwd), Entropy, DataProtectionScope.CurrentUser);
        return ProtectedPrefix + Convert.ToBase64String(cipher);
    }

    public static string DecodePassword(string encoded)
    {
        try
        {
            if (string.IsNullOrEmpty(encoded)) return "";
            if (encoded.StartsWith(ProtectedPrefix))
            {
                var cipher = Convert.FromBase64String(encoded[ProtectedPrefix.Length..]);
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser));
            }
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));   // v1 format
        }
        catch { return ""; }   // e.g. a profile copied from another user/machine
    }
}
