namespace SqlCsvExporter.Models;

/// <summary>Options controlling CSV output.</summary>
public sealed class ExportOptions
{
    /// <summary>
    /// When true (default), text cells that look like Excel formulas
    /// (=, +, -, @ after optional leading whitespace) are prefixed with
    /// a single quote so Excel does not evaluate them. This changes the
    /// cell text; disable for exact-value data exports.
    /// </summary>
    public bool ProtectAgainstExcelFormulas { get; set; } = true;
}
