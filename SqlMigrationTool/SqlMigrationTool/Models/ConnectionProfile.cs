namespace SqlMigrationTool.Models;

public class ConnectionProfile
{
    public string Name        { get; set; } = "";
    public string Server      { get; set; } = "";
    public int    Port        { get; set; }
    public bool   WindowsAuth { get; set; }
    public string Username    { get; set; } = "";
    public string Password    { get; set; } = ""; // base64-encoded
    public string Database    { get; set; } = "";

    public override string ToString() => Name;
}
