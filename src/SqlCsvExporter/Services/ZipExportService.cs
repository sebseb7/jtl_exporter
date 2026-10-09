using System.Data;
using System.IO.Compression;
using Microsoft.Data.SqlClient;
using SqlCsvExporter.Models;

namespace SqlCsvExporter.Services;

/// <summary>
/// Streams selected tables as CSV entries into a ZIP archive.
/// Writes to a temp file first; the final ZIP appears only on full success.
/// </summary>
public sealed class ZipExportService
{
    private readonly SqlServerService _sql = new();

    public async Task ExportAsync(
        ConnectionSettings settings,
        string database,
        IReadOnlyList<TableInfo> tables,
        string destinationZipPath,
        ExportOptions options,
        IProgress<ExportProgress>? progress,
        CancellationToken ct)
    {
        if (tables.Count == 0)
        {
            throw new ExportException("Keine Tabellen ausgewählt.");
        }

        if (string.IsNullOrWhiteSpace(destinationZipPath))
        {
            throw new ExportException("Keine Zieldatei ausgewählt.");
        }

        // Freeze the selection.
        var frozen = tables.Select(t => new TableInfo
        {
            ObjectId = t.ObjectId,
            Schema = t.Schema,
            Name = t.Name,
            HasSelectAccess = t.HasSelectAccess,
        }).ToList();

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

            // NOTE: archive must be fully disposed (finalized) before File.Move.
            // ZipArchive with leaveOpen:false closes zipStream on Dispose,
            // so do NOT touch zipStream afterwards.
            using (var zipStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
                {
                    var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var completed = 0;

                    foreach (var table in frozen)
                    {
                        ct.ThrowIfCancellationRequested();
                        var entryName = AllocateEntryName(table, usedNames);
                        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                        long rows;
                        using (var entryStream = entry.Open())
                        {
                            rows = await ExportTableAsync(connection, table, entryStream, options, name =>
                            {
                                progress?.Report(new ExportProgress
                                {
                                    CurrentTable = name,
                                    CompletedTables = completed,
                                    TotalTables = frozen.Count,
                                    RowsWrittenCurrentTable = 0,
                                });
                            }, (name, rowCount) =>
                            {
                                // Throttle: report every 1000 rows.
                                if (rowCount % 1000 == 0)
                                {
                                    progress?.Report(new ExportProgress
                                    {
                                        CurrentTable = name,
                                        CompletedTables = completed,
                                        TotalTables = frozen.Count,
                                        RowsWrittenCurrentTable = rowCount,
                                    });
                                }
                            }, ct).ConfigureAwait(false);
                        }

                        completed++;
                        progress?.Report(new ExportProgress
                        {
                            CurrentTable = table.DisplayName,
                            CompletedTables = completed,
                            TotalTables = frozen.Count,
                            RowsWrittenCurrentTable = rows,
                        });
                    }
                }
            }

            connection.Close();

            // Atomic publish: replace destination only after complete success.
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

    private async Task<long> ExportTableAsync(
        SqlConnection connection,
        TableInfo table,
        Stream output,
        ExportOptions options,
        Action<string> onStart,
        Action<string, long> onRows,
        CancellationToken ct)
    {
        var quoted = $"{SqlServerService.QuoteIdentifier(table.Schema)}.{SqlServerService.QuoteIdentifier(table.Name)}";
        onStart(table.DisplayName);

        // Preflight: column metadata + supported types + SELECT permission.
        List<(string Name, string TypeName, long MaxLength)> columns;
        using (var metaCommand = new SqlCommand($"SELECT TOP (0) * FROM {quoted};", connection)
        {
            CommandTimeout = 0,
        })
        {
            using var cancel = ct.Register(() => { try { metaCommand.Cancel(); } catch { } });
            using var metaReader = await metaCommand.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct).ConfigureAwait(false);
            var schema = metaReader.GetSchemaTable();
            columns = new List<(string, string, long)>();
            if (schema is not null)
            {
                foreach (System.Data.DataRow row in schema.Rows)
                {
                    var columnName = (string)row["ColumnName"];
                    var typeName = ((string)row["DataTypeName"]).Trim().ToLowerInvariant();
                    long maxLength = row["ColumnSize"] is int size ? size : -1;
                    SqlValueFormatter.ThrowIfUnsupported(table.DisplayName, columnName, typeName);
                    columns.Add((columnName, typeName, maxLength));
                }
            }

            metaReader.Close();
        }

        if (columns.Count == 0)
        {
            throw new ExportException($"Tabelle '{table.DisplayName}' hat keine exportierbaren Spalten.");
        }

        using var csv = new CsvWriter(output, options.ProtectAgainstExcelFormulas);
        await csv.WriteHeaderAsync(columns.Select(c => c.Name).ToList(), ct).ConfigureAwait(false);

        using var command = new SqlCommand($"SELECT * FROM {quoted};", connection)
        {
            CommandTimeout = 0, // unlimited; user can cancel.
        };
        using var registration = ct.Register(() => { try { command.Cancel(); } catch { } });

        long rows = 0;
        try
        {
            using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            var fieldCount = reader.FieldCount;

            // Cache per-column streaming decisions once (also avoids per-row GetColumnSchema cost).
            var columnSchemas = reader.GetColumnSchema();
            var dataTypeNames = new string[fieldCount];
            var fieldTypes = new Type[fieldCount];
            var streamBinary = new bool[fieldCount];
            var streamText = new bool[fieldCount];
            for (var i = 0; i < fieldCount; i++)
            {
                var typeName = reader.GetDataTypeName(i).Trim().ToLowerInvariant();
                dataTypeNames[i] = typeName;
                fieldTypes[i] = reader.GetFieldType(i);
                var size = columnSchemas[i].ColumnSize ?? -1;
                streamBinary[i] = SqlValueFormatter.IsStreamingBinaryType(typeName, size);
                streamText[i] = !streamBinary[i] && SqlValueFormatter.IsStreamingTextType(typeName, size);
            }

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                for (var i = 0; i < fieldCount; i++)
                {
                    if (i > 0)
                    {
                        await csv.WriteFieldSeparatorAsync().ConfigureAwait(false);
                    }

                    if (await reader.IsDBNullAsync(i, ct).ConfigureAwait(false))
                    {
                        continue; // NULL -> empty cell.
                    }

                    var dataTypeName = dataTypeNames[i];
                    var fieldType = fieldTypes[i];

                    if (streamBinary[i])
                    {
                        try
                        {
                            using var binary = reader.GetStream(i);
                            await csv.WriteStreamingBinaryFieldAsync(binary, ct).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException)
                        {
                            // Provider refused streaming for this type: fall back to buffered read.
                            var buffered = reader.GetValue(i);
                            var bufferedText = SqlValueFormatter.FormatValue(buffered);
                            await csv.WriteFieldAsync(bufferedText, false).ConfigureAwait(false);
                        }
                    }
                    else if (streamText[i])
                    {
                        try
                        {
                            using var textReader = reader.GetTextReader(i);
                            await csv.WriteStreamingTextFieldAsync(textReader, ct).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException)
                        {
                            // Provider refused streaming for this type: fall back to buffered read.
                            var buffered = reader.GetValue(i);
                            var bufferedText = SqlValueFormatter.FormatValue(buffered);
                            await csv.WriteFieldAsync(bufferedText, true).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        var value = reader.GetValue(i);
                        var text = SqlValueFormatter.FormatValue(value);
                        await csv.WriteFieldAsync(text, SqlValueFormatter.IsTextColumnType(fieldType, dataTypeName)).ConfigureAwait(false);
                    }
                }

                await csv.EndRecordAsync(ct).ConfigureAwait(false);
                rows++;
                onRows(table.DisplayName, rows);
            }
        }
        catch (SqlException ex)
        {
            var detail = ex.Errors.Count > 0 ? ex.Errors[0].Message : ex.Message;
            throw new ExportException($"Tabelle '{table.DisplayName}': {detail}", ex);
        }

        await csv.FlushAsync(ct).ConfigureAwait(false);
        return rows;
    }

    public static string AllocateEntryName(TableInfo table, HashSet<string> usedNames)
    {
        var baseName = SanitizeFileName($"{table.Schema}.{table.Name}");
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = $"table_{table.ObjectId}";
        }

        if (baseName.Length > 120)
        {
            baseName = baseName[..120];
        }

        var candidate = baseName + ".csv";
        if (usedNames.Add(candidate))
        {
            return candidate;
        }

        // Deterministic collision resolution using the database-scoped object id.
        candidate = $"{baseName}_{table.ObjectId}.csv";
        if (usedNames.Add(candidate))
        {
            return candidate;
        }

        var n = 2;
        while (!usedNames.Add($"{baseName}_{table.ObjectId}_{n}.csv"))
        {
            n++;
        }

        return $"{baseName}_{table.ObjectId}_{n}.csv";
    }

    internal static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        }

        var result = sb.ToString().Trim().TrimEnd('.');
        // Avoid Windows reserved device names.
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };
        if (reserved.Contains(result))
        {
            result = "_" + result;
        }

        return result;
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
