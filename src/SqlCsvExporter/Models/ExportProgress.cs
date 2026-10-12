namespace SqlCsvExporter.Models;

/// <summary>Progress snapshot reported during export.</summary>
public sealed class ExportProgress
{
    public string Stage { get; set; } = string.Empty;

    public int CompletedSteps { get; set; }

    public int TotalSteps { get; set; }

    public long RecordsWritten { get; set; }

    // Backward-compatibility aliases
    public string CurrentTable
    {
        get => Stage;
        set => Stage = value;
    }

    public int CompletedTables
    {
        get => CompletedSteps;
        set => CompletedSteps = value;
    }

    public int TotalTables
    {
        get => TotalSteps;
        set => TotalSteps = value;
    }

    public long RowsWrittenCurrentTable
    {
        get => RecordsWritten;
        set => RecordsWritten = value;
    }
}
