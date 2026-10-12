using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlCsvExporter.Services;

/// <summary>
/// Implements the exact CSV formatting matching the JTL data export specification.
/// </summary>
public static class JtlCsvFormatter
{
    public static readonly HashSet<string> MonetaryHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "net",
        "fVKNetto",
        "fEKNetto",
        "fWertBrutto",
        "fWertNetto",
        "sumArticlePrices",
        "sumArticlePricesNetto",
        "sumPayments",
        "fBetrag",
        "fZahlungSumme",
        "fWertBruttoGesamtFixiert",
        "fWertNettoGesamtFixiert",
    };
    public const string CsvDelimiter = ";";
    private static readonly CultureInfo GermanCulture = CultureInfo.GetCultureInfo("de-DE");
    private static readonly Regex SafeExtensionRegex = new(@"^\.[a-z0-9]{1,8}$", RegexOptions.Compiled);

    public static string FormatScalar(object? value)
    {
        if (value is null || value is DBNull)
        {
            return string.Empty;
        }

        if (value is byte[] bytes)
        {
            return Convert.ToBase64String(bytes);
        }

        if (value is DateTime dt)
        {
            // JavaScript Date.toISOString() format: 2026-10-11T17:51:11.000Z
            var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            return utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        if (value is DateTimeOffset dto)
        {
            return dto.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        if (value is DateOnly d)
        {
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (value is TimeOnly t)
        {
            return t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (value is decimal dec)
        {
            return dec.ToString("G29", CultureInfo.InvariantCulture);
        }

        if (value is double dbl)
        {
            return dbl.ToString("G17", CultureInfo.InvariantCulture);
        }

        if (value is float fl)
        {
            return fl.ToString("G9", CultureInfo.InvariantCulture);
        }

        var str = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return str.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    public static string FormatCell(string header, object? value)
    {
        if (header is "active" or "track_strock" or "cAktiv" or "cLagerAktiv")
        {
            if (value is null || value is DBNull)
            {
                return "0";
            }

            if (value is bool b)
            {
                return b ? "1" : "0";
            }

            if (value is int or long or short or byte)
            {
                return Convert.ToInt64(value) != 0 ? "1" : "0";
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            if (string.Equals(text, "Y", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
            {
                return "1";
            }

            return "0";
        }

        if (value is null || value is DBNull)
        {
            return string.Empty;
        }

        if (header is "cArtNr" or "cBarcode" or "sku" or "ean")
        {
            var str = FormatScalar(value);
            if (!string.IsNullOrEmpty(str))
            {
                return $"=\"{str.Replace("\"", "\"\"")}\"";
            }

            return string.Empty;
        }

        if (header == "taxrate")
        {
            if (value is decimal dec)
            {
                return dec.ToString("G29", CultureInfo.InvariantCulture);
            }

            if (value is double or float or int or long or short or byte)
            {
                var num = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return num.ToString("G29", CultureInfo.InvariantCulture);
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(text) && decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed.ToString("G29", CultureInfo.InvariantCulture);
            }

            return text ?? string.Empty;
        }

        if (MonetaryHeaders.Contains(header))
        {
            if (value is decimal or double or float or int or long or short or byte)
            {
                var num = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return num.ToString("0.00", GermanCulture);
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(text) && decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed.ToString("0.00", GermanCulture);
            }

            return text ?? string.Empty;
        }

        return FormatScalar(value);
    }

    public static string CsvLine(IReadOnlyList<string> values, string delimiter = CsvDelimiter)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(delimiter);
            }

            var str = values[i] ?? string.Empty;
            if (str.StartsWith("=\"") && str.EndsWith("\""))
            {
                sb.Append(str);
            }
            else
            {
                sb.Append('"');
                sb.Append(str.Replace("\"", "\"\""));
                sb.Append('"');
            }
        }

        sb.Append("\r\n");
        return sb.ToString();
    }

    public static string GetImageExtension(string? source)
    {
        var ext = Path.GetExtension(source ?? string.Empty).ToLowerInvariant();
        return SafeExtensionRegex.IsMatch(ext) ? ext : ".jpg";
    }
}
