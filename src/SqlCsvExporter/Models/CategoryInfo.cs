namespace SqlCsvExporter.Models;

/// <summary>
/// A category from dbo.tKategorie.
/// </summary>
public sealed class CategoryInfo
{
    public int KKategorie { get; set; }

    public int? KOberKategorie { get; set; }

    public int NSort { get; set; }

    public string Name { get; set; } = string.Empty;

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Name)
            ? $"Kategorie {KKategorie}"
            : $"{Name} (ID: {KKategorie})";
    }
}
