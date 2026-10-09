namespace SqlCsvExporter.Models;

/// <summary>A JTL-eazybusiness mandant (client) from dbo.tMandant.</summary>
public sealed class MandantInfo
{
    public int KMandant { get; set; }

    /// <summary>Display name (cName column).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Database name (cDB column) used for the real export.</summary>
    public string Database { get; set; } = string.Empty;

    public override string ToString() => Name;
}
