namespace SqlCsvExporter.Models;

/// <summary>Progress snapshot reported during export.</summary>
public sealed class ExportProgress
{
    public string CurrentTable { get; set; } = string.Empty;

    public int CompletedTables { get; set; }

    public int TotalTables { get; set; }

    public long RowsWrittenCurrentTable { get; set; }
}
