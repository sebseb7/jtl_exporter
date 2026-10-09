using System.Data.SqlTypes;
using SqlCsvExporter.Models;
using SqlCsvExporter.Services;

namespace SqlCsvExporter.Tests;

public sealed class FormatterTests
{
    [Theory]
    [InlineData("=cmd", true)]
    [InlineData("  +2", true)]
    [InlineData("@x", true)]
    [InlineData("-1", true)]
    [InlineData("hello", false)]
    [InlineData("1-2", false)]
    [InlineData("", false)]
    public void FormulaDetection(string text, bool expected)
    {
        Assert.Equal(expected, CsvWriter.StartsLikeFormula(text));
    }

    [Fact]
    public void NullAndEmptyBecomeEmpty()
    {
        Assert.Null(SqlValueFormatter.FormatValue(null));
        Assert.Null(SqlValueFormatter.FormatValue(DBNull.Value));
        Assert.Equal(string.Empty, SqlValueFormatter.FormatValue(string.Empty));
    }

    [Fact]
    public void BoolFormatsAsOneZero()
    {
        Assert.Equal("1", SqlValueFormatter.FormatValue(true));
        Assert.Equal("0", SqlValueFormatter.FormatValue(false));
    }

    [Fact]
    public void HighPrecisionDecimalIsLossless()
    {
        var big = new SqlDecimal(38, 10, true, 1234567890, 123456789, 123456789, 123456789);
        Assert.Equal(big.ToString(), SqlValueFormatter.FormatValue(big));
    }

    [Fact]
    public void DateTimeOffsetKeepsOffset()
    {
        var dto = new DateTimeOffset(2026, 10, 9, 12, 30, 0, TimeSpan.FromHours(2));
        Assert.Contains("+02:00", SqlValueFormatter.FormatValue(dto));
    }

    [Fact]
    public void BinaryFormatsAsHex()
    {
        Assert.Equal("0x00FF", SqlValueFormatter.FormatValue(new byte[] { 0x00, 0xFF }));
    }

    [Fact]
    public void UnsupportedTypesRejected()
    {
        Assert.False(SqlValueFormatter.IsSupportedType("geography"));
        Assert.Throws<ExportException>(() =>
            SqlValueFormatter.ThrowIfUnsupported("dbo.t", "c", "geometry"));
    }

    [Fact]
    public void IdentifierQuotingEscapesBrackets()
    {
        Assert.Equal("[my]]table]", SqlServerService.QuoteIdentifier("my]table"));
    }

    [Fact]
    public void ArchiveNamesAreSafeAndDeterministic()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var a = new TableInfo { ObjectId = 1, Schema = "dbo", Name = "a/b" };
        var b = new TableInfo { ObjectId = 2, Schema = "dbo", Name = "a?b" };
        var first = ZipExportService.AllocateEntryName(a, used);
        var second = ZipExportService.AllocateEntryName(b, used);
        Assert.Equal("dbo.a_b.csv", first);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("/", second);
        Assert.DoesNotContain("?", second);
    }

    [Fact]
    public void ConnectionBuilderNeverEmbedsPasswordInErrors()
    {
        var service = new SqlServerService();
        var settings = new ConnectionSettings { Server = "  " };
        var ex = Assert.Throws<ExportException>(() => service.BuildConnectionString(settings));
        Assert.DoesNotContain("Password", ex.Message);
    }

    [Fact]
    public void MandantDisplaysName()
    {
        var mandant = new MandantInfo { KMandant = 1, Name = "eB-Standard", Database = "eazybusiness" };
        Assert.Equal("eB-Standard", mandant.ToString());
        Assert.Equal("eazybusiness", mandant.Database);
    }
}
