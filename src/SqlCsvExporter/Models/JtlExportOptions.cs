using System.IO.Compression;

namespace SqlCsvExporter.Models;

/// <summary>
/// Options for the JTL chunked data export.
/// </summary>
public sealed class JtlExportOptions
{
    public CategoryScope CategoryScope { get; set; } = new();

    /// <summary>
    /// Whether to include image blobs in pictures/ within the ZIP archive.
    /// </summary>
    public bool IncludeImages { get; set; } = true;

    /// <summary>
    /// Compression level for the ZIP archive entries.
    /// </summary>
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Optimal;

    /// <summary>
    /// Database query batch size per page.
    /// </summary>
    public int RowPageSize { get; set; } = 200;

    /// <summary>
    /// Image fetching batch size.
    /// </summary>
    public int ImageBatchSize { get; set; } = 12;
}
