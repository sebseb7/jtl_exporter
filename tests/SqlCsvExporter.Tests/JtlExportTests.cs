using Microsoft.Data.SqlClient;
using SqlCsvExporter.Models;
using SqlCsvExporter.Services;

namespace SqlCsvExporter.Tests;

public sealed class JtlExportTests
{
    [Fact]
    public void CsvLineEscapesQuotesAndWrapsInQuotes()
    {
        var line = JtlCsvFormatter.CsvLine(["Hello", "World with \"quotes\"", "simple;delimiter"]);
        Assert.Equal("\"Hello\";\"World with \"\"quotes\"\"\";\"simple;delimiter\"\r\n", line);
    }

    [Fact]
    public void CsvLinePreservesExcelFormulaSyntaxWithoutOuterQuotes()
    {
        var artNr = JtlCsvFormatter.FormatCell("cArtNr", "012345");
        var barcode = JtlCsvFormatter.FormatCell("cBarcode", "4000123456789");
        var sku = JtlCsvFormatter.FormatCell("sku", "098765");
        var ean = JtlCsvFormatter.FormatCell("ean", "4000987654321");
        var normal = JtlCsvFormatter.FormatCell("name", "Testartikel");

        var line = JtlCsvFormatter.CsvLine([artNr, barcode, sku, ean, normal]);
        Assert.Equal("=\"012345\";=\"4000123456789\";=\"098765\";=\"4000987654321\";\"Testartikel\"\r\n", line);
    }

    [Fact]
    public void MonetaryHeadersFormatWithGermanDecimalComma()
    {
        var cell1 = JtlCsvFormatter.FormatCell("net", 19.99m);
        var cell2 = JtlCsvFormatter.FormatCell("fEKNetto", 1234.5);
        var cell3 = JtlCsvFormatter.FormatCell("fWertBrutto", "99.95");
        var cell4 = JtlCsvFormatter.FormatCell("sumPayments", 42.0m);
        var cell5 = JtlCsvFormatter.FormatCell("fWertBruttoGesamtFixiert", 100.123m);

        Assert.Equal("19,99", cell1);
        Assert.Equal("1234,50", cell2);
        Assert.Equal("99,95", cell3);
        Assert.Equal("42,00", cell4);
        Assert.Equal("100,12", cell5);

        foreach (var header in JtlCsvFormatter.MonetaryHeaders)
        {
            Assert.Equal("10,50", JtlCsvFormatter.FormatCell(header, 10.5m));
        }

        var line = JtlCsvFormatter.CsvLine([cell1, cell2]);
        Assert.Equal("\"19,99\";\"1234,50\"\r\n", line);
    }

    [Fact]
    public void TaxRateFormatsWithoutTrailingZeros()
    {
        var rate1 = JtlCsvFormatter.FormatCell("taxrate", 19.00000000000000m);
        var rate2 = JtlCsvFormatter.FormatCell("taxrate", 7.00000000000000m);
        var rate3 = JtlCsvFormatter.FormatCell("taxrate", 0.00000000000000m);
        var rate4 = JtlCsvFormatter.FormatCell("taxrate", "19.00000000000000");

        Assert.Equal("19", rate1);
        Assert.Equal("7", rate2);
        Assert.Equal("0", rate3);
        Assert.Equal("19", rate4);

        var line = JtlCsvFormatter.CsvLine([rate1]);
        Assert.Equal("\"19\"\r\n", line);
    }

    [Fact]
    public void DateTimeFormatsAsIso8601Utc()
    {
        var dt = new DateTime(2026, 10, 11, 15, 30, 45, DateTimeKind.Utc);
        var formatted = JtlCsvFormatter.FormatScalar(dt);
        Assert.StartsWith("2026-10-11T15:30:45", formatted);
        Assert.EndsWith("Z", formatted);
    }

    [Fact]
    public void NewlinesAreNormalizedToSingleLinefeed()
    {
        var formatted = JtlCsvFormatter.FormatScalar("Zeile 1\r\nZeile 2\rZeile 3\nZeile 4");
        Assert.Equal("Zeile 1\nZeile 2\nZeile 3\nZeile 4", formatted);
    }

    [Fact]
    public void ImageExtensionSanitization()
    {
        Assert.Equal(".jpg", JtlCsvFormatter.GetImageExtension("image.jpg"));
        Assert.Equal(".png", JtlCsvFormatter.GetImageExtension("image.PNG"));
        Assert.Equal(".webp", JtlCsvFormatter.GetImageExtension("C:\\path\\photo.webp"));
        Assert.Equal(".jpg", JtlCsvFormatter.GetImageExtension(""));
        Assert.Equal(".jpg", JtlCsvFormatter.GetImageExtension(null));
        Assert.Equal(".jpg", JtlCsvFormatter.GetImageExtension("file.toolongextension"));
        Assert.Equal(".jpg", JtlCsvFormatter.GetImageExtension("file.bad*char"));
    }

    [Fact]
    public void ImageReferenceProcessingBuildsExpectedPaths()
    {
        var rows = new List<(int EntityId, int KBild, int Plattform, int Shop, int? Nr, string? Quelle)>
        {
            (10, 101, 1, 0, null, "pic1.jpg"),
            (10, 102, 1, 0, 5, "pic2.png"),
            (20, 201, 2, 1, null, "pic3.jpeg"),
        };

        var refs = JtlExportService.ProcessImageReferences("article", rows);

        Assert.Equal(3, refs.Count);
        Assert.Equal("pictures/articles/10/1-0-1-bild-101.jpg", refs[0].Path);
        Assert.Equal("pictures/articles/10/1-0-5-bild-102.png", refs[1].Path);
        Assert.Equal("pictures/articles/20/2-1-1-bild-201.jpeg", refs[2].Path);
    }

    [Fact]
    public void GroupImagePathsCollectsByEntity()
    {
        var refs = new List<ImageReference>
        {
            new() { EntityId = 1, KBild = 10, Path = "pictures/articles/1/a.jpg" },
            new() { EntityId = 1, KBild = 11, Path = "pictures/articles/1/b.jpg" },
            new() { EntityId = 2, KBild = 12, Path = "pictures/articles/2/c.jpg" },
        };

        var grouped = JtlExportService.GroupImagePaths(refs);

        Assert.Equal(2, grouped[1].Count);
        Assert.Single(grouped[2]);
        Assert.Contains("pictures/articles/1/a.jpg", grouped[1]);
        Assert.Contains("pictures/articles/1/b.jpg", grouped[1]);
    }

    [Fact]
    public void CategoryScopeCteGeneratesParametersAndRecursion()
    {
        var scope = new CategoryScope
        {
            TopKats = [1, 2],
            ExcludedRoots = [734, 721],
        };

        var cte = JtlExportService.BuildCategoryScopeCte(scope);
        Assert.Contains("@topKat0, @topKat1", cte);
        Assert.Contains("@excludedRoot0, @excludedRoot1", cte);
        Assert.Contains("WITH IncludedCategories AS", cte);
        Assert.Contains("ExcludedCategories AS", cte);
        Assert.Contains("ExportCategories AS", cte);

        using var cmd = new SqlCommand();
        JtlExportService.BindCategoryScope(cmd, scope);
        Assert.Equal(4, cmd.Parameters.Count);
        Assert.Equal(1, cmd.Parameters["@topKat0"].Value);
        Assert.Equal(2, cmd.Parameters["@topKat1"].Value);
        Assert.Equal(734, cmd.Parameters["@excludedRoot0"].Value);
        Assert.Equal(721, cmd.Parameters["@excludedRoot1"].Value);
    }

    [Fact]
    public void ActiveAndTrackStockFormatAsOneAndZero()
    {
        Assert.Equal("1", JtlCsvFormatter.FormatCell("active", "Y"));
        Assert.Equal("0", JtlCsvFormatter.FormatCell("active", "N"));
        Assert.Equal("1", JtlCsvFormatter.FormatCell("active", 1));
        Assert.Equal("0", JtlCsvFormatter.FormatCell("active", 0));
        Assert.Equal("1", JtlCsvFormatter.FormatCell("active", true));
        Assert.Equal("0", JtlCsvFormatter.FormatCell("active", false));
        Assert.Equal("0", JtlCsvFormatter.FormatCell("active", null));

        Assert.Equal("1", JtlCsvFormatter.FormatCell("track_strock", "Y"));
        Assert.Equal("0", JtlCsvFormatter.FormatCell("track_strock", "N"));
        Assert.Equal("1", JtlCsvFormatter.FormatCell("track_strock", 1));
        Assert.Equal("0", JtlCsvFormatter.FormatCell("track_strock", 0));
        Assert.Equal("0", JtlCsvFormatter.FormatCell("track_strock", null));
    }

    [Fact]
    public void QueriesContainPagingParametersAndMaxRecursion()
    {
        var scope = new CategoryScope { TopKats = [1] };

        var catQuery = JtlExportService.GetCategoriesQuery(scope);
        Assert.Contains("@pageSize", catQuery);
        Assert.Contains("@lastId", catQuery);
        Assert.Contains("OPTION (MAXRECURSION 0)", catQuery);
        Assert.Contains("kKategorie AS id", catQuery);
        Assert.Contains("kOberKategorie AS parentid", catQuery);
        Assert.Contains("nSort AS sort", catQuery);

        var artQuery = JtlExportService.GetArticlesQuery(scope);
        Assert.Contains("a.kArtikel AS id", artQuery);
        Assert.Contains("a.cArtNr AS sku", artQuery);
        Assert.Contains("a.fVKNetto AS net", artQuery);
        Assert.Contains("CASE WHEN a.cAktiv = 'Y' THEN 1 ELSE 0 END AS active", artQuery);
        Assert.Contains("CASE WHEN a.cLagerAktiv = 'Y' THEN 1 ELSE 0 END AS track_strock", artQuery);
        Assert.Contains("brand", artQuery);
        Assert.Contains("AS supplieer", artQuery);
        Assert.Contains("dbo.tliefartikel", artQuery);
        Assert.Contains("dbo.tLieferant", artQuery);
        Assert.Contains("AS [desc]", artQuery);
        Assert.True(artQuery.IndexOf("AS supplieer", StringComparison.Ordinal) < artQuery.IndexOf("AS [desc]", StringComparison.Ordinal));
        Assert.True(artQuery.IndexOf("pictureFiles", StringComparison.Ordinal) < artQuery.IndexOf("AS supplieer", StringComparison.Ordinal));
        Assert.True(artQuery.IndexOf("AS [desc]", StringComparison.Ordinal) < artQuery.IndexOf("FROM dbo.tArtikel a", StringComparison.Ordinal));

        var manQuery = JtlExportService.GetManifestsQuery(scope);
        Assert.Contains("o.kAuftrag AS id", manQuery);
        Assert.Contains("o.cAuftragsNr AS nr", manQuery);
        Assert.Contains("paymentType", manQuery);
        Assert.Contains("shippingType", manQuery);
        Assert.Contains("payed", manQuery);
        Assert.Contains("shipped", manQuery);
        Assert.Contains("STRING_AGG", manQuery);

        var itemQuery = JtlExportService.GetManifestItemsQuery(scope);
        Assert.Contains("p.kAuftragPosition AS id", itemQuery);
        Assert.Contains("p.kAuftrag AS manifestid", itemQuery);
        Assert.Contains("articleid", itemQuery);
        Assert.Contains("amount", itemQuery);
        Assert.Contains("amountShipped", itemQuery);
        Assert.Contains("p.fVKNetto AS net", itemQuery);
        Assert.Contains("ISNULL(p.fAnzahl, 0) != 0", itemQuery);
        Assert.DoesNotContain("p.kArtikel > 0", itemQuery);
    }

    [Fact]
    public void SupplieerColumnFormattingHandlesDelimitersAndEmptyValues()
    {
        var rawSupplier = "Grow In AG:109211:0:19:4.31|Naarden International s.r.o.:G201501:0:19:4.19";
        var formatted = JtlCsvFormatter.FormatCell("supplieer", rawSupplier);
        Assert.Equal(rawSupplier, formatted);

        var emptyFormatted = JtlCsvFormatter.FormatCell("supplieer", "");
        Assert.Equal(string.Empty, emptyFormatted);

        var nullFormatted = JtlCsvFormatter.FormatCell("supplieer", null);
        Assert.Equal(string.Empty, nullFormatted);

        var csvLine = JtlCsvFormatter.CsvLine([formatted, "Beschreibung"]);
        Assert.Equal("\"Grow In AG:109211:0:19:4.31|Naarden International s.r.o.:G201501:0:19:4.19\";\"Beschreibung\"\r\n", csvLine);
    }
}
