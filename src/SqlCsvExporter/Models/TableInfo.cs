namespace SqlCsvExporter.Models;

/// <summary>Identifies a user table within one database.</summary>
public sealed class TableInfo
{
    public int ObjectId { get; set; }

    public string Schema { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Whether the login appears to have SELECT permission.</summary>
    public bool HasSelectAccess { get; set; } = true;

    public string DisplayName => $"{Schema}.{Name}";

    public override string ToString() => DisplayName;
}
