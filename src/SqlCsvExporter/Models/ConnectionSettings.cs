namespace SqlCsvExporter.Models;

/// <summary>
/// In-memory SQL Server connection settings. Never persisted to disk.
/// </summary>
public sealed class ConnectionSettings
{
    public string Server { get; set; } = string.Empty;

    /// <summary>True for Windows integrated auth, false for SQL login.</summary>
    public bool UseWindowsAuth { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>
    /// When true, the server certificate is trusted without validation.
    /// Only enable for internal servers with self-signed certificates.
    /// </summary>
    public bool TrustServerCertificate { get; set; }

    public int ConnectTimeoutSeconds { get; set; } = 15;

    public void ClearCredentials()
    {
        Username = null;
        Password = null;
    }
}
