using Microsoft.Data.SqlClient;
using SqlCsvExporter.Models;

namespace SqlCsvExporter.Services;

/// <summary>SQL Server connection, database and table discovery.</summary>
public sealed class SqlServerService
{
    public string BuildConnectionString(ConnectionSettings settings, string? database = null)
    {
        if (string.IsNullOrWhiteSpace(settings.Server))
        {
            throw new ExportException("Der Servername darf nicht leer sein.");
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = settings.Server.Trim(),
            Encrypt = true,
            TrustServerCertificate = settings.TrustServerCertificate,
            ConnectTimeout = Math.Clamp(settings.ConnectTimeoutSeconds, 1, 120),
            ApplicationName = "SqlCsvExporter",
            PersistSecurityInfo = false,
        };

        if (!string.IsNullOrWhiteSpace(database))
        {
            builder.InitialCatalog = database.Trim();
        }

        if (settings.UseWindowsAuth)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.Username))
            {
                throw new ExportException("Der Benutzername darf bei SQL Server-Authentifizierung nicht leer sein.");
            }

            builder.IntegratedSecurity = false;
            builder.UserID = settings.Username!.Trim();
            builder.Password = settings.Password ?? string.Empty;
        }

        return builder.ConnectionString;
    }

    public static string QuoteIdentifier(string identifier)
    {
        // Bracket-quote one identifier part (schema or table separately).
        return "[" + identifier.Replace("]", "]]") + "]";
    }

    public async Task TestConnectionAsync(ConnectionSettings settings, CancellationToken ct)
    {
        var connectionString = BuildConnectionString(settings);
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or OperationCanceledException)
        {
            throw ToSafeException("Verbindung fehlgeschlagen", ex);
        }
    }

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(ConnectionSettings settings, CancellationToken ct)
    {
        const string sql = """
            SELECT name
            FROM sys.databases
            WHERE state_desc = 'ONLINE'
              AND HAS_DBACCESS(name) = 1
              AND database_id > 4
              AND name NOT IN ('master', 'tempdb', 'model', 'msdb')
            ORDER BY name;
            """;

        var connectionString = BuildConnectionString(settings);
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var command = new SqlCommand(sql, connection)
            {
                CommandTimeout = Math.Clamp(settings.ConnectTimeoutSeconds, 1, 120),
            };
            using var cancel = ct.Register(() => { try { command.Cancel(); } catch { } });
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<string>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or OperationCanceledException)
        {
            throw ToSafeException("Datenbanken konnten nicht aufgelistet werden", ex);
        }
    }

    /// <summary>
    /// Loads JTL mandants from dbo.tMandant in the eazybusiness database.
    /// </summary>
    public async Task<IReadOnlyList<MandantInfo>> GetMandantsAsync(
        ConnectionSettings settings, CancellationToken ct)
    {
        const string sql = """
            SELECT kMandant, cName, cDB
            FROM eazybusiness.dbo.tMandant
            ORDER BY cName;
            """;

        var connectionString = BuildConnectionString(settings);
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var command = new SqlCommand(sql, connection)
            {
                CommandTimeout = Math.Clamp(settings.ConnectTimeoutSeconds, 1, 120),
            };
            using var cancel = ct.Register(() => { try { command.Cancel(); } catch { } });
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<MandantInfo>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var db = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                if (string.IsNullOrWhiteSpace(db))
                {
                    continue;
                }

                result.Add(new MandantInfo
                {
                    KMandant = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0)),
                    Name = string.IsNullOrWhiteSpace(name) ? db.Trim() : name.Trim(),
                    Database = db.Trim(),
                });
            }

            return result;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or OperationCanceledException)
        {
            throw ToSafeException("Mandanten konnten nicht aus eazybusiness.dbo.tMandant geladen werden", ex);
        }
    }

    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync(
        ConnectionSettings settings, string database, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(database))
        {
            throw new ExportException("Bitte zuerst eine Datenbank wählen.");
        }

        const string sql = """
            SELECT
                t.object_id,
                SCHEMA_NAME(t.schema_id) AS schema_name,
                t.name AS table_name,
                HAS_PERMS_BY_NAME(
                    QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name),
                    'OBJECT',
                    'SELECT') AS has_select
            FROM sys.tables AS t
            WHERE t.is_ms_shipped = 0
            ORDER BY schema_name, table_name;
            """;

        var connectionString = BuildConnectionString(settings, database);
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var command = new SqlCommand(sql, connection)
            {
                CommandTimeout = Math.Clamp(settings.ConnectTimeoutSeconds, 1, 120),
            };
            using var cancel = ct.Register(() => { try { command.Cancel(); } catch { } });
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<TableInfo>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result.Add(new TableInfo
                {
                    ObjectId = reader.GetInt32(0),
                    Schema = reader.GetString(1),
                    Name = reader.GetString(2),
                    HasSelectAccess = !reader.IsDBNull(3) && Convert.ToInt32(reader.GetValue(3)) == 1,
                });
            }

            return result;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or OperationCanceledException)
        {
            throw ToSafeException($"Tabellen in Datenbank '{database}' konnten nicht aufgelistet werden", ex);
        }
    }

    /// <summary>
    /// Loads top-level categories (kOberKategorie = 0 or NULL) from dbo.tKategorie.
    /// </summary>
    public async Task<IReadOnlyList<CategoryInfo>> GetTopCategoriesAsync(
        ConnectionSettings settings, string database, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(database))
        {
            throw new ExportException("Bitte zuerst eine Datenbank wählen.");
        }

        const string sql = """
            SELECT
                k.kKategorie,
                k.kOberKategorie,
                k.nSort,
                COALESCE((
                    SELECT TOP 1 ks.cName
                    FROM dbo.tKategorieSprache ks
                    WHERE ks.kKategorie = k.kKategorie
                    ORDER BY ks.kSprache, ks.kPlattform, ks.kShop
                ), '') AS cName
            FROM dbo.tKategorie k
            WHERE k.kOberKategorie = 0 OR k.kOberKategorie IS NULL
            ORDER BY k.nSort, k.kKategorie;
            """;

        var connectionString = BuildConnectionString(settings, database);
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var command = new SqlCommand(sql, connection)
            {
                CommandTimeout = Math.Clamp(settings.ConnectTimeoutSeconds, 1, 120),
            };
            using var cancel = ct.Register(() => { try { command.Cancel(); } catch { } });
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var result = new List<CategoryInfo>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result.Add(new CategoryInfo
                {
                    KKategorie = reader.GetInt32(0),
                    KOberKategorie = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    NSort = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    Name = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                });
            }

            return result;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or OperationCanceledException)
        {
            throw ToSafeException($"Kategorien in Datenbank '{database}' konnten nicht aufgelistet werden", ex);
        }
    }

    internal static ExportException ToSafeException(string context, Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return new ExportException($"{context}: Vorgang wurde abgebrochen.", ex);
        }

        if (ex is SqlException sql)
        {
            var detail = sql.Errors.Count > 0 ? sql.Errors[0].Message : sql.Message;
            return new ExportException($"{context}: {detail}", ex);
        }

        return new ExportException($"{context}: {ex.Message}", ex);
    }
}
