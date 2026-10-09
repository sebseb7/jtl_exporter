using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace SqlCsvExporter.Services;

/// <summary>Converts SQL values to culture-independent CSV text.</summary>
public static class SqlValueFormatter
{
    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "char", "nchar", "varchar", "nvarchar", "text", "ntext", "xml", "sysname",
    };

    private static readonly HashSet<string> StreamingTextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "varchar", "nvarchar", "text", "ntext", "xml",
    };

    private static readonly HashSet<string> BinaryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "binary", "varbinary", "image", "rowversion", "timestamp",
    };

    private static readonly HashSet<string> UnsupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "geometry", "geography", "hierarchyid", "sql_variant",
    };

    public static bool IsTextType(string dataTypeName) => TextTypes.Contains(dataTypeName);

    public static bool IsStreamingTextType(string dataTypeName, long maxLength)
    {
        // -1 = MAX; long fixed types also benefit from streaming.
        return StreamingTextTypes.Contains(dataTypeName) && (maxLength == -1 || maxLength > 4000);
    }

    public static bool IsBinaryType(string dataTypeName) => BinaryTypes.Contains(dataTypeName);

    /// <summary>
    /// Whether binary content should be streamed via GetStream.
    /// rowversion/timestamp do NOT support GetStream and are read buffered (always 8 bytes).
    /// Small fixed-length binary values are also read buffered.
    /// </summary>
    public static bool IsStreamingBinaryType(string dataTypeName, long maxLength)
    {
        if (dataTypeName.Equals("image", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return dataTypeName.Equals("varbinary", StringComparison.OrdinalIgnoreCase)
            && (maxLength == -1 || maxLength > 8000);
    }

    public static bool IsSupportedType(string dataTypeName)
    {
        return !UnsupportedTypes.Contains(dataTypeName)
            && !dataTypeName.EndsWith("udt", StringComparison.OrdinalIgnoreCase);
    }

    public static void ThrowIfUnsupported(string table, string column, string dataTypeName)
    {
        if (!IsSupportedType(dataTypeName))
        {
            throw new ExportException(
                $"Tabelle '{table}', Spalte '{column}': Typ '{dataTypeName}' wird vom CSV-Export nicht unterstützt.");
        }
    }

    /// <summary>Formats an already-read scalar value. Returns null for SQL NULL.</summary>
    public static string? FormatValue(object? value)
    {
        if (value is null || value is DBNull)
        {
            return null;
        }

        return value switch
        {
            bool b => b ? "1" : "0",
            byte v => v.ToString(CultureInfo.InvariantCulture),
            short v => v.ToString(CultureInfo.InvariantCulture),
            int v => v.ToString(CultureInfo.InvariantCulture),
            long v => v.ToString(CultureInfo.InvariantCulture),
            float v => v.ToString("R", CultureInfo.InvariantCulture),
            double v => v.ToString("R", CultureInfo.InvariantCulture),
            decimal v => v.ToString(CultureInfo.InvariantCulture),
            SqlDecimal v => v.ToString(),
            SqlMoney v => v.Value.ToString(CultureInfo.InvariantCulture),
            DateTime v => v.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            DateOnly v => v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly v => v.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            DateTimeOffset v => v.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture),
            TimeSpan v => v.ToString("c", CultureInfo.InvariantCulture),
            Guid v => v.ToString(),
            byte[] v => "0x" + Convert.ToHexString(v),
            SqlBinary v => "0x" + Convert.ToHexString(v.Value),
            SqlGuid v => v.Value.ToString(),
            SqlBoolean v => v.Value ? "1" : "0",
            SqlDateTime v => v.Value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            SqlString v => v.IsNull ? null : v.Value,
            SqlXml v => v.IsNull ? null : v.Value,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    public static bool IsTextColumnType(Type fieldType, string dataTypeName)
    {
        return fieldType == typeof(string) || IsTextType(dataTypeName);
    }
}
