namespace SqlCsvExporter.Models;

/// <summary>
/// Reference to an image entry in dbo.tBild linked to a category, article or manufacturer.
/// </summary>
public sealed class ImageReference
{
    public int KBild { get; set; }

    public int EntityId { get; set; }

    public string Path { get; set; } = string.Empty;
}
