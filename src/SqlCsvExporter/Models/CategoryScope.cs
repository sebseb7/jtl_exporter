namespace SqlCsvExporter.Models;

/// <summary>
/// Defines the category scope for the JTL data export.
/// </summary>
public sealed class CategoryScope
{
    public static readonly int[] DefaultRebateCategoryRoots = [734, 721, 723, 746];

    public IReadOnlyList<int> TopKats { get; set; } = [];

    public IReadOnlyList<int> ExcludedRoots { get; set; } = DefaultRebateCategoryRoots;
}
