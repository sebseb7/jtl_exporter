namespace SqlCsvExporter.Services;

/// <summary>Export failure with user-safe message (never contains credentials).</summary>
public sealed class ExportException : Exception
{
    public ExportException(string message)
        : base(message)
    {
    }

    public ExportException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
