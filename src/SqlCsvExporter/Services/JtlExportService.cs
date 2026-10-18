using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlCsvExporter.Models;

namespace SqlCsvExporter.Services;

/// <summary>
/// Streams JTL categories, articles, manifests, manifest items and images
/// directly into a ZIP archive, matching the Node.js data export specification.
/// </summary>
public sealed class JtlExportService
{
    private readonly SqlServerService _sql = new();

    public async Task ExportAsync(
        ConnectionSettings settings,
        string database,
        string destinationZipPath,
        JtlExportOptions options,
        IProgress<ExportProgress>? progress,
        CancellationToken ct)
    {
        if (options.CategoryScope.TopKats.Count == 0)
        {
            throw new ExportException("Keine gültigen topKats für den Datenexport konfiguriert.");
        }

        if (string.IsNullOrWhiteSpace(destinationZipPath))
        {
            throw new ExportException("Keine Zieldatei ausgewählt.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationZipPath));
        if (string.IsNullOrEmpty(directory))
        {
            throw new ExportException("Ungültiger Zielpfad.");
        }

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(destinationZipPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var connectionString = _sql.BuildConnectionString(settings, database);
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            using (var zipStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
                {
                    var completedSteps = 0;
                    const int totalSteps = 8;

                    void ReportStage(string stage)
                    {
                        progress?.Report(new ExportProgress
                        {
                            Stage = stage,
                            CompletedSteps = completedSteps,
                            TotalSteps = totalSteps,
                        });
                    }

                    // 1. Load image references
                    ReportStage("Bildreferenzen: category");
                    var categoryImages = await LoadImageReferencesAsync(connection, "category", options.CategoryScope, ReportStage, ct).ConfigureAwait(false);
                    completedSteps++;

                    ReportStage("Bildreferenzen: article");
                    var articleImages = await LoadImageReferencesAsync(connection, "article", options.CategoryScope, ReportStage, ct).ConfigureAwait(false);
                    completedSteps++;

                    ReportStage("Bildreferenzen: manufacturer");
                    var manufacturerImages = await LoadImageReferencesAsync(connection, "manufacturer", options.CategoryScope, ReportStage, ct).ConfigureAwait(false);
                    completedSteps++;

                    var categoryImageDict = GroupImagePaths(categoryImages);
                    var articleImageDict = GroupImagePaths(articleImages);
                    var manufacturerImageDict = GroupImagePaths(manufacturerImages);

                    // 2. Export categories.csv
                    ReportStage("ZIP-Eintrag: categories.csv");
                    await ExportCsvTableAsync(
                        connection,
                        archive,
                        "categories.csv",
                        GetCategoriesQuery(options.CategoryScope),
                        options.CategoryScope,
                        "id",
                        row => categoryImageDict.TryGetValue(ToIntSafe(row["id"]), out var paths) ? paths : [],
                        isArticleTable: false,
                        manufacturerImageDict: null,
                        options.RowPageSize,
                        ReportStage,
                        ct).ConfigureAwait(false);
                    completedSteps++;

                    // 3. Export articles.csv
                    ReportStage("ZIP-Eintrag: articles.csv");
                    await ExportCsvTableAsync(
                        connection,
                        archive,
                        "articles.csv",
                        GetArticlesQuery(options.CategoryScope),
                        options.CategoryScope,
                        "id",
                        row => articleImageDict.TryGetValue(ToIntSafe(row["id"]), out var paths) ? paths : [],
                        isArticleTable: true,
                        manufacturerImageDict,
                        options.RowPageSize,
                        ReportStage,
                        ct).ConfigureAwait(false);
                    completedSteps++;

                    // 4. Export manifests.csv
                    ReportStage("ZIP-Eintrag: manifests.csv");
                    await ExportCsvTableAsync(
                        connection,
                        archive,
                        "manifests.csv",
                        GetManifestsQuery(options.CategoryScope),
                        options.CategoryScope,
                        "id",
                        _ => [],
                        isArticleTable: false,
                        manufacturerImageDict: null,
                        options.RowPageSize,
                        ReportStage,
                        ct).ConfigureAwait(false);
                    completedSteps++;

                    // 5. Export manifestitems.csv
                    ReportStage("ZIP-Eintrag: manifestitems.csv");
                    await ExportCsvTableAsync(
                        connection,
                        archive,
                        "manifestitems.csv",
                        GetManifestItemsQuery(options.CategoryScope),
                        options.CategoryScope,
                        "id",
                        _ => [],
                        isArticleTable: false,
                        manufacturerImageDict: null,
                        options.RowPageSize,
                        ReportStage,
                        ct).ConfigureAwait(false);
                    completedSteps++;

                    // 6. Export images
                    if (options.IncludeImages)
                    {
                        var allReferences = categoryImages.Concat(articleImages).Concat(manufacturerImages).ToList();
                        await AppendImagesAsync(connection, archive, allReferences, options.ImageBatchSize, ReportStage, ct).ConfigureAwait(false);
                    }

                    ReportStage("ZIP-Archiv abschliessen");
                    completedSteps++;
                    ReportStage("Fertig");
                }
            }

            connection.Close();

            // Atomic publish
            File.Move(tempPath, destinationZipPath, overwrite: true);
        }
        catch (OperationCanceledException ex)
        {
            DeleteTemp(tempPath);
            throw new ExportException("Export wurde abgebrochen. Es wurde keine ZIP-Datei erstellt.", ex);
        }
        catch (ExportException)
        {
            DeleteTemp(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            DeleteTemp(tempPath);
            throw SqlServerService.ToSafeException("Export fehlgeschlagen", ex);
        }
    }

    public static string BuildCategoryScopeCte(CategoryScope scope)
    {
        var topKatParams = scope.TopKats.Count > 0
            ? string.Join(", ", scope.TopKats.Select((_, i) => $"@topKat{i}"))
            : "NULL";

        var excludedRootParams = scope.ExcludedRoots.Count > 0
            ? string.Join(", ", scope.ExcludedRoots.Select((_, i) => $"@excludedRoot{i}"))
            : "NULL";

        return $"""
        WITH IncludedCategories AS (
            SELECT k.kKategorie FROM dbo.tKategorie k WHERE k.kKategorie IN ({topKatParams})
            UNION ALL
            SELECT child.kKategorie
            FROM dbo.tKategorie child
            INNER JOIN IncludedCategories parent ON child.kOberKategorie = parent.kKategorie
        ),
        ExcludedCategories AS (
            SELECT k.kKategorie FROM dbo.tKategorie k WHERE k.kKategorie IN ({excludedRootParams})
            UNION ALL
            SELECT child.kKategorie
            FROM dbo.tKategorie child
            INNER JOIN ExcludedCategories parent ON child.kOberKategorie = parent.kKategorie
        ),
        ExportCategories AS (
            SELECT DISTINCT included.kKategorie
            FROM IncludedCategories included
            WHERE NOT EXISTS (
                SELECT 1 FROM ExcludedCategories excluded
                WHERE excluded.kKategorie = included.kKategorie
            )
        )
        """;
    }

    public static void BindCategoryScope(SqlCommand command, CategoryScope scope)
    {
        for (var i = 0; i < scope.TopKats.Count; i++)
        {
            command.Parameters.Add($"@topKat{i}", SqlDbType.Int).Value = scope.TopKats[i];
        }

        for (var i = 0; i < scope.ExcludedRoots.Count; i++)
        {
            command.Parameters.Add($"@excludedRoot{i}", SqlDbType.Int).Value = scope.ExcludedRoots[i];
        }
    }

    public static string GetCategoriesQuery(CategoryScope scope) => $"""
        {BuildCategoryScopeCte(scope)}
        SELECT TOP (@pageSize)
            k.kKategorie AS id,
            k.kOberKategorie AS parentid,
            k.nSort AS sort,
            COALESCE((
                SELECT TOP 1 ks.cName
                FROM dbo.tKategorieSprache ks
                WHERE ks.kKategorie = k.kKategorie
                ORDER BY ks.kSprache, ks.kPlattform, ks.kShop
            ), '') AS name,
            '' AS pictureFiles
        FROM dbo.tKategorie k
        WHERE k.kKategorie > @lastId
            AND EXISTS (SELECT 1 FROM ExportCategories ec WHERE ec.kKategorie = k.kKategorie)
        ORDER BY k.kKategorie
        OPTION (MAXRECURSION 0)
        """;

    public static string GetArticlesQuery(CategoryScope scope) => $"""
        {BuildCategoryScopeCte(scope)}
        SELECT TOP (@pageSize)
            a.kArtikel AS id,
            a.cArtNr AS sku,
            COALESCE((
                SELECT TOP 1 d.cName
                FROM dbo.tArtikelBeschreibung d
                WHERE d.kArtikel = a.kArtikel
                ORDER BY d.kSprache, d.kPlattform, d.kShop
            ), '') AS name,
            a.fVKNetto AS net,
            COALESCE((
                SELECT TOP 1 ss.fSteuersatz
                FROM dbo.tSteuersatz ss
                WHERE ss.kSteuerklasse = a.kSteuerklasse AND ss.kSteuerzone = 1
            ), CASE WHEN a.kSteuerklasse = 1 THEN 19 WHEN a.kSteuerklasse = 2 THEN 7 ELSE 0 END) AS taxrate,
            CASE WHEN a.cAktiv = 'Y' THEN 1 ELSE 0 END AS active,
            a.nLagerbestand AS stock,
            a.cBarcode AS ean,
            a.fGewicht AS weight,
            CASE WHEN a.cLagerAktiv = 'Y' THEN 1 ELSE 0 END AS track_strock,
            a.dErstelldatum AS created_at,
            a.nSort AS sort,
            CASE
                WHEN a.nIstVater = 1 THEN COALESCE((
                    SELECT STRING_AGG(es.cName, ', ')
                    FROM dbo.teigenschaft e
                    LEFT JOIN dbo.tEigenschaftSprache es
                        ON es.kEigenschaft = e.kEigenschaft AND es.kSprache = 1
                    WHERE e.kArtikel = a.kArtikel
                ), '')
                ELSE COALESCE((
                    SELECT STRING_AGG(ws.cName, ', ')
                    FROM dbo.tEigenschaftKombiWert kw
                    INNER JOIN dbo.tEigenschaftWertSprache ws
                        ON ws.kEigenschaftWert = kw.kEigenschaftWert AND ws.kSprache = 1
                    WHERE kw.kEigenschaftKombi = a.kEigenschaftKombi
                ), '')
            END AS variation,
            a.kVaterArtikel AS parentid,
            a.nIstVater AS is_parent,
            a.kHersteller,
            COALESCE(h.cName, '') AS brand,
            COALESCE((
                SELECT STRING_AGG(CAST(ka.kKategorie AS varchar), ',')
                FROM dbo.tKategorieArtikel ka
                WHERE ka.kArtikel = a.kArtikel
                    AND EXISTS (SELECT 1 FROM ExportCategories ec WHERE ec.kKategorie = ka.kKategorie)
            ), '') AS categories,
            COALESCE((
                SELECT TOP 1 CONCAT('pfand:', CAST(CAST(aas.fWertDecimal AS decimal(10, 2)) AS varchar))
                FROM dbo.tArtikelAttribut aa
                INNER JOIN dbo.tArtikelAttributSprache aas ON aas.kArtikelAttribut = aa.kArtikelAttribut
                WHERE aa.kArtikel = a.kArtikel AND aa.kAttribut = 160 AND aas.fWertDecimal IS NOT NULL
            ), '') AS attributes,
            '' AS brandPictureFiles,
            '' AS pictureFiles,
            COALESCE((
                SELECT STRING_AGG(
                    CONCAT(
                        REPLACE(REPLACE(COALESCE(l.cFirma, ''), ':', ' '), '|', ' '),
                        ':',
                        REPLACE(REPLACE(COALESCE(la.cLiefArtNr, ''), ':', ' '), '|', ' '),
                        ':',
                        CAST(CAST(CAST(COALESCE(la.nVPEMenge, 0) AS decimal(10, 2)) AS float) AS varchar),
                        ':',
                        CAST(CAST(CAST(COALESCE(la.fMwSt, 0) AS decimal(10, 2)) AS float) AS varchar),
                        ':',
                        CAST(CAST(COALESCE(la.fEKNetto, 0) AS decimal(10, 2)) AS varchar)
                    ), '|'
                ) WITHIN GROUP (ORDER BY COALESCE(la.nStandard, 0) DESC, la.kLiefArtikel ASC)
                FROM dbo.tliefartikel la
                LEFT JOIN dbo.tLieferant l ON l.kLieferant = la.tLieferant_kLieferant
                WHERE la.tArtikel_kArtikel = a.kArtikel
            ), '') AS supplieer,
            COALESCE((
                SELECT TOP 1 d.cBeschreibung
                FROM dbo.tArtikelBeschreibung d
                WHERE d.kArtikel = a.kArtikel
                ORDER BY d.kSprache, d.kPlattform, d.kShop
            ), '') AS [desc]
        FROM dbo.tArtikel a
        LEFT JOIN dbo.tHersteller h ON h.kHersteller = a.kHersteller
        WHERE a.kArtikel > @lastId
            AND EXISTS (
                SELECT 1 FROM dbo.tKategorieArtikel ka
                WHERE ka.kArtikel = a.kArtikel
                    AND EXISTS (SELECT 1 FROM ExportCategories ec WHERE ec.kKategorie = ka.kKategorie)
            )
        ORDER BY a.kArtikel
        OPTION (MAXRECURSION 0)
        """;

    public static string GetManifestsQuery(CategoryScope scope) => $"""
        {BuildCategoryScopeCte(scope)}
        SELECT TOP (@pageSize)
            o.kAuftrag AS id,
            o.cAuftragsNr AS nr,
            o.cExterneAuftragsnummer AS extnr,
            o.dErstellt AS created_at,
            COALESCE((
                SELECT STRING_AGG(t.cName, ', ')
                FROM (
                    SELECT DISTINCT COALESCE(za.cName, pay.cName) AS cName, pay.kBestellung
                    FROM dbo.tZahlung pay
                    LEFT JOIN dbo.tZahlungsart za ON za.kZahlungsart = pay.kZahlungsart
                ) t
                WHERE t.kBestellung = o.kAuftrag
            ), paymentType.cName, '') AS paymentType,
            shippingType.cName AS shippingType,
            CASE 
                WHEN ae.nZahlungStatus = 2 OR ae.dBezahlt IS NOT NULL OR ae.fZahlung >= ae.fWertBrutto OR EXISTS (SELECT 1 FROM dbo.tZahlung pay WHERE pay.kBestellung = o.kAuftrag) 
                THEN 1 ELSE 0 
            END AS payed,
            CASE 
                WHEN o.nKomplettAusgeliefert = 1 OR ae.dLetzterVersand IS NOT NULL OR EXISTS (SELECT 1 FROM dbo.tLieferschein l WHERE l.kBestellung = o.kAuftrag) 
                THEN 1 ELSE 0 
            END AS shipped
        FROM Verkauf.tAuftrag o
        INNER JOIN Verkauf.tAuftragEckdaten ae ON ae.kAuftrag = o.kAuftrag
        LEFT JOIN dbo.tZahlungsart paymentType ON paymentType.kZahlungsart = o.kZahlungsart
        LEFT JOIN dbo.tVersandArt shippingType ON shippingType.kVersandArt = o.kVersandArt
        WHERE o.kAuftrag > @lastId
            AND o.nType = 1
            AND EXISTS (
                SELECT 1
                FROM Verkauf.tAuftragPosition p
                WHERE p.kAuftrag = o.kAuftrag AND p.kArtikel IS NOT NULL
                    AND EXISTS (
                        SELECT 1 FROM dbo.tKategorieArtikel ka
                        WHERE ka.kArtikel = p.kArtikel
                            AND EXISTS (SELECT 1 FROM ExportCategories ec WHERE ec.kKategorie = ka.kKategorie)
                    )
            )
        ORDER BY o.kAuftrag
        OPTION (MAXRECURSION 0)
        """;

    public static string GetManifestItemsQuery(CategoryScope scope) => $"""
        {BuildCategoryScopeCte(scope)}
        SELECT TOP (@pageSize)
            p.kAuftragPosition AS id,
            p.kAuftrag AS manifestid,
            COALESCE(p.kArtikel, 0) AS articleid,
            p.cName AS name,
            p.fAnzahl AS amount,
            COALESCE(detail.fAnzahlGeliefert, 0) AS amountShipped,
            p.fVKNetto AS net,
            p.fMwSt AS taxrate,
            p.nSort AS sort
        FROM Verkauf.tAuftragPosition p
        INNER JOIN Verkauf.tAuftrag o ON o.kAuftrag = p.kAuftrag AND o.nType = 1
        LEFT JOIN Verkauf.tAuftragPositionEckdaten detail
            ON detail.kAuftragPosition = p.kAuftragPosition
        WHERE p.kAuftragPosition > @lastId
            AND ISNULL(p.fAnzahl, 0) != 0
            AND EXISTS (
                SELECT 1
                FROM Verkauf.tAuftragPosition item
                WHERE item.kAuftrag = o.kAuftrag AND item.kArtikel IS NOT NULL
                    AND EXISTS (
                        SELECT 1 FROM dbo.tKategorieArtikel ka
                        WHERE ka.kArtikel = item.kArtikel
                            AND EXISTS (SELECT 1 FROM ExportCategories ec WHERE ec.kKategorie = ka.kKategorie)
                    )
            )
        ORDER BY p.kAuftragPosition
        OPTION (MAXRECURSION 0)
        """;

    public static async Task<List<ImageReference>> LoadImageReferencesAsync(
        SqlConnection connection,
        string entityType,
        CategoryScope scope,
        Action<string> onStage,
        CancellationToken ct)
    {
        var query = entityType switch
        {
            "category" => $"""
                {BuildCategoryScopeCte(scope)}
                SELECT kp.kKategorie AS entityId, kp.kBild, kp.kPlattform, kp.kShop, kp.nNr, b.cQuelle
                FROM dbo.tKategoriebildPlattform kp
                LEFT JOIN dbo.tBild b ON b.kBild = kp.kBild
                WHERE EXISTS (SELECT 1 FROM ExportCategories ec WHERE ec.kKategorie = kp.kKategorie)
                ORDER BY kp.kKategorie, kp.kPlattform, kp.kShop, kp.nNr, kp.kBild
                OPTION (MAXRECURSION 0)
                """,
            "article" => $"""
                {BuildCategoryScopeCte(scope)}
                SELECT ap.kArtikel AS entityId, ap.kBild, ap.kPlattform, ap.kShop, ap.nNr, b.cQuelle
                FROM dbo.tArtikelbildPlattform ap
                LEFT JOIN dbo.tBild b ON b.kBild = ap.kBild
                WHERE ap.kShop = 0
                    AND EXISTS (
                    SELECT 1 FROM dbo.tKategorieArtikel ka
                    WHERE ka.kArtikel = ap.kArtikel
                        AND EXISTS (SELECT 1 FROM ExportCategories ec WHERE ec.kKategorie = ka.kKategorie)
                )
                ORDER BY ap.kArtikel, ap.kPlattform, ap.kShop, ap.nNr, ap.kBild
                OPTION (MAXRECURSION 0)
                """,
            _ => $"""
                {BuildCategoryScopeCte(scope)}
                SELECT DISTINCT hbp.kHersteller AS entityId, hbp.kBild, hbp.kPlattform, hbp.kShop, CAST(NULL AS int) AS nNr, b.cQuelle
                FROM dbo.tHerstellerBildPlattform hbp
                INNER JOIN dbo.tBild b ON b.kBild = hbp.kBild
                WHERE hbp.kPlattform = 1
                    AND hbp.kShop = 0
                    AND EXISTS (
                        SELECT 1
                        FROM dbo.tArtikel a
                        INNER JOIN dbo.tKategorieArtikel ka ON ka.kArtikel = a.kArtikel
                        INNER JOIN ExportCategories ec ON ec.kKategorie = ka.kKategorie
                        WHERE a.kHersteller = hbp.kHersteller
                    )
                ORDER BY hbp.kHersteller, hbp.kBild
                OPTION (MAXRECURSION 0)
                """,
        };

        using var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = 0;
        BindCategoryScope(command, scope);

        var rows = new List<(int EntityId, int KBild, int Plattform, int Shop, int? Nr, string? Quelle)>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var entityIdOrdinal = reader.GetOrdinal("entityId");
        var kBildOrdinal = reader.GetOrdinal("kBild");
        var plattformOrdinal = reader.GetOrdinal("kPlattform");
        var shopOrdinal = reader.GetOrdinal("kShop");
        int? nNrOrdinal = null;
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), "nNr", StringComparison.OrdinalIgnoreCase))
            {
                nNrOrdinal = i;
                break;
            }
        }
        var cQuelleOrdinal = reader.GetOrdinal("cQuelle");

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var entityId = ToIntSafe(reader.GetValue(entityIdOrdinal));
            var kBild = ToIntSafe(reader.GetValue(kBildOrdinal));
            var plattform = ToIntSafe(reader.GetValue(plattformOrdinal));
            var shop = ToIntSafe(reader.GetValue(shopOrdinal));
            int? nr = null;
            if (nNrOrdinal.HasValue && !reader.IsDBNull(nNrOrdinal.Value))
            {
                var val = reader.GetValue(nNrOrdinal.Value);
                if (val is int intVal)
                {
                    nr = intVal;
                }
                else if (int.TryParse(val?.ToString(), out var parsedInt))
                {
                    nr = parsedInt;
                }
            }
            var quelle = reader.IsDBNull(cQuelleOrdinal) ? null : reader.GetString(cQuelleOrdinal);
            rows.Add((entityId, kBild, plattform, shop, nr, quelle));
        }

        return ProcessImageReferences(entityType, rows);
    }

    public static List<ImageReference> ProcessImageReferences(
        string entityType,
        IEnumerable<(int EntityId, int KBild, int Plattform, int Shop, int? Nr, string? Quelle)> rows)
    {
        var folder = entityType switch
        {
            "category" => "pictures/categories",
            "manufacturer" => "pictures/hersteller",
            _ => "pictures/articles",
        };

        var entitySequence = new Dictionary<int, int>();
        var results = new List<ImageReference>();

        foreach (var row in rows)
        {
            var sequence = entitySequence.TryGetValue(row.EntityId, out var seq) ? seq + 1 : 1;
            entitySequence[row.EntityId] = sequence;

            var imageSequence = row.Nr ?? sequence;
            var ext = JtlCsvFormatter.GetImageExtension(row.Quelle);
            var fileName = $"{row.Plattform}-{row.Shop}-{imageSequence}-bild-{row.KBild}{ext}";

            results.Add(new ImageReference
            {
                KBild = row.KBild,
                EntityId = row.EntityId,
                Path = $"{folder}/{row.EntityId}/{fileName}",
            });
        }

        return results;
    }

    public static Dictionary<int, List<string>> GroupImagePaths(IEnumerable<ImageReference> references)
    {
        var dict = new Dictionary<int, List<string>>();
        foreach (var r in references)
        {
            if (!dict.TryGetValue(r.EntityId, out var list))
            {
                list = [];
                dict[r.EntityId] = list;
            }

            list.Add(r.Path);
        }

        return dict;
    }

    private static async Task ExportCsvTableAsync(
        SqlConnection connection,
        ZipArchive archive,
        string exportName,
        string query,
        CategoryScope scope,
        string lastIdField,
        Func<Dictionary<string, object?>, IReadOnlyList<string>> getPictureFiles,
        bool isArticleTable,
        Dictionary<int, List<string>>? manufacturerImageDict,
        int pageSize,
        Action<string> onStage,
        CancellationToken ct)
    {
        var entry = archive.CreateEntry(exportName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), bufferSize: 65536, leaveOpen: true)
        {
            NewLine = "\r\n",
        };

        var lastId = 0;
        var hasMore = true;
        var page = 0;
        List<string>? headers = null;

        while (hasMore)
        {
            ct.ThrowIfCancellationRequested();
            page++;
            onStage($"{exportName}: Datenbankabfrage Seite {page}");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = query;
            cmd.CommandTimeout = 0;
            cmd.Parameters.Add("@lastId", SqlDbType.Int).Value = lastId;
            cmd.Parameters.Add("@pageSize", SqlDbType.Int).Value = pageSize;
            BindCategoryScope(cmd, scope);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (headers is null)
            {
                var columnNames = new List<string>();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var col = reader.GetName(i);
                    if (isArticleTable && col == "kHersteller")
                    {
                        continue;
                    }

                    columnNames.Add(col);
                }

                if (columnNames.Count == 0)
                {
                    throw new ExportException($"CSV-Spalten für {exportName} konnten nicht ermittelt werden.");
                }

                headers = columnNames;
                await writer.WriteAsync(JtlCsvFormatter.CsvLine(headers)).ConfigureAwait(false);
            }

            var rowCount = 0;
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rowCount++;
                var rowData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    rowData[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                if (exportName == "manifestitems.csv")
                {
                    var amount = ToDecimalSafe(rowData.TryGetValue("amount", out var a) ? a : null);
                    if (amount == 0m)
                    {
                        continue;
                    }
                }

                var lineValues = new List<string>(headers.Count);
                foreach (var header in headers)
                {
                    if (header == "pictureFiles")
                    {
                        var paths = getPictureFiles(rowData);
                        lineValues.Add(string.Join(",", paths));
                    }
                    else if (header is "brandPictureFiles" or "manufacturerPictureFiles")
                    {
                        var kHerstellerObj = rowData.TryGetValue("kHersteller", out var h) ? h : null;
                        var kHersteller = ToIntSafe(kHerstellerObj);
                        var paths = manufacturerImageDict is not null && manufacturerImageDict.TryGetValue(kHersteller, out var mPaths) ? mPaths : [];
                        lineValues.Add(string.Join(",", paths));
                    }
                    else
                    {
                        var cellValue = rowData.TryGetValue(header, out var v) ? v : null;
                        lineValues.Add(JtlCsvFormatter.FormatCell(header, cellValue));
                    }
                }

                await writer.WriteAsync(JtlCsvFormatter.CsvLine(lineValues)).ConfigureAwait(false);

                if (!rowData.TryGetValue(lastIdField, out var nextLastIdObj) || nextLastIdObj is null)
                {
                    throw new ExportException($"Ungültiger Paging-Schlüssel {lastIdField} in {exportName}");
                }

                var nextLastId = ToIntSafe(nextLastIdObj);
                if (nextLastId <= lastId)
                {
                    throw new ExportException($"Ungültiger Paging-Schlüssel {lastIdField} in {exportName}: {nextLastId}");
                }

                lastId = nextLastId;
            }

            hasMore = rowCount == pageSize;
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task AppendImagesAsync(
        SqlConnection connection,
        ZipArchive archive,
        IReadOnlyList<ImageReference> references,
        int batchSize,
        Action<string> onStage,
        CancellationToken ct)
    {
        var referencesByImageId = new Dictionary<int, List<ImageReference>>();
        foreach (var r in references)
        {
            if (!referencesByImageId.TryGetValue(r.KBild, out var list))
            {
                list = [];
                referencesByImageId[r.KBild] = list;
            }

            list.Add(r);
        }

        var uniqueImageIds = referencesByImageId.Keys.ToList();
        var totalBatches = (int)Math.Ceiling((double)uniqueImageIds.Count / batchSize);

        for (var i = 0; i < uniqueImageIds.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batchIndex = (i / batchSize) + 1;
            onStage($"Bilder: Stapel {batchIndex}/{Math.Max(1, totalBatches)}");

            var batch = uniqueImageIds.Skip(i).Take(batchSize).ToList();
            using var cmd = connection.CreateCommand();
            cmd.CommandTimeout = 0;
            var paramNames = new List<string>();
            for (var b = 0; b < batch.Count; b++)
            {
                var paramName = $"@kBild{b}";
                paramNames.Add(paramName);
                cmd.Parameters.Add(paramName, SqlDbType.Int).Value = batch[b];
            }

            cmd.CommandText = $"SELECT kBild, bBild FROM dbo.tBild WHERE kBild IN ({string.Join(",", paramNames)})";
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var imageMap = new Dictionary<int, byte[]>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var kBild = reader.GetInt32(0);
                var bBild = reader.IsDBNull(1) ? [] : (byte[])reader.GetValue(1);
                imageMap[kBild] = bBild;
            }

            foreach (var kBild in batch)
            {
                if (!imageMap.TryGetValue(kBild, out var imageBytes))
                {
                    throw new ExportException($"Bilddaten fehlen für kBild {kBild}");
                }

                foreach (var reference in referencesByImageId[kBild])
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(reference.Path, CompressionLevel.NoCompression);
                    using var entryStream = entry.Open();
                    await entryStream.WriteAsync(imageBytes, ct).ConfigureAwait(false);
                }
            }
        }
    }

    private static int ToIntSafe(object? value)
    {
        if (value is null or DBNull)
        {
            return 0;
        }

        if (value is int i)
        {
            return i;
        }

        if (value is long l)
        {
            return (int)l;
        }

        if (value is short s)
        {
            return s;
        }

        if (value is byte b)
        {
            return b;
        }

        var str = value.ToString();
        if (int.TryParse(str, out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static decimal ToDecimalSafe(object? value)
    {
        if (value is null or DBNull)
        {
            return 0m;
        }

        if (value is decimal dec)
        {
            return dec;
        }

        if (value is double or float or int or long or short or byte)
        {
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }

        var str = value.ToString();
        if (decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0m;
    }

    private static void DeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex)
        {
            throw new ExportException($"Export wurde nicht abgeschlossen und die Temporärdatei '{tempPath}' konnte nicht gelöscht werden: {ex.Message}", ex);
        }
    }
}
