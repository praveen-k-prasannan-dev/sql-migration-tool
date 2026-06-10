using Microsoft.Data.SqlClient;

namespace SqlMigrationTool.Models;

public class ConnectionConfig
{
    public string Server      { get; set; } = "";
    public int    Port        { get; set; } = 1433;
    public bool   WindowsAuth { get; set; } = true;
    public string Username    { get; set; } = "";
    public string Password    { get; set; } = "";
    public string Database    { get; set; } = "";

    public string BuildConnectionString()
    {
        var dataSource = (Port == 1433 || Port == 0) ? Server : $"{Server},{Port}";
        var sb = new SqlConnectionStringBuilder
        {
            DataSource             = dataSource,
            InitialCatalog         = Database,
            IntegratedSecurity     = WindowsAuth,
            TrustServerCertificate = true,
            ConnectTimeout         = 30
        };
        if (!WindowsAuth) { sb.UserID = Username; sb.Password = Password; }
        return sb.ConnectionString;
    }
}
