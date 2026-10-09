using System.Text;

namespace SqlCsvExporter.Services;

/// <summary>
/// Streaming CSV writer: semicolon delimiter, CRLF, double-quote escaping.
/// </summary>
public sealed class CsvWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly bool _protectFormulas;
    private bool _disposed;

    public CsvWriter(Stream output, bool protectFormulas)
    {
        _protectFormulas = protectFormulas;
        // UTF-8 with BOM, Excel-friendly.
        _writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = false,
        };
    }

    public async Task WriteHeaderAsync(IReadOnlyList<string> columnNames, CancellationToken ct)
    {
        for (var i = 0; i < columnNames.Count; i++)
        {
            if (i > 0)
            {
                await _writer.WriteAsync(';').ConfigureAwait(false);
            }

            await _writer.WriteAsync(Escape(columnNames[i], isText: true)).ConfigureAwait(false);
        }

        await _writer.WriteLineAsync().ConfigureAwait(false);
        await _writer.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task WriteFieldSeparatorAsync()
    {
        await _writer.WriteAsync(';').ConfigureAwait(false);
    }

    public async Task EndRecordAsync(CancellationToken ct)
    {
        await _writer.WriteLineAsync().ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        await _writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Writes one fully-materialized field (NULL becomes empty).</summary>
    public async Task WriteFieldAsync(string? text, bool isText)
    {
        await _writer.WriteAsync(Escape(text, isText)).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams a large text field as a quoted CSV cell, escaping quotes chunk-wise.
    /// Always quoted to avoid buffering for delimiter detection.
    /// </summary>
    public async Task WriteStreamingTextFieldAsync(TextReader reader, CancellationToken ct)
    {
        await _writer.WriteAsync('"').ConfigureAwait(false);
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '"')
                {
                    await _writer.WriteAsync("\"\"").ConfigureAwait(false);
                }
                else
                {
                    await _writer.WriteAsync(buffer[i]).ConfigureAwait(false);
                }
            }
        }

        await _writer.WriteAsync('"').ConfigureAwait(false);
    }

    /// <summary>Streams binary content as 0x-prefixed uppercase hex (never needs quoting).</summary>
    public async Task WriteStreamingBinaryFieldAsync(Stream binary, CancellationToken ct)
    {
        await _writer.WriteAsync("0x").ConfigureAwait(false);
        var buffer = new byte[8192];
        int read;
        while ((read = await binary.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            var sb = new StringBuilder(read * 2);
            for (var i = 0; i < read; i++)
            {
                sb.Append(buffer[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }

            await _writer.WriteAsync(sb.ToString()).ConfigureAwait(false);
        }
    }

    private string Escape(string? value, bool isText)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value;
        if (isText && _protectFormulas && StartsLikeFormula(text))
        {
            text = "'" + text;
        }

        if (text.Contains(';') || text.Contains('"') || text.Contains('\r') || text.Contains('\n'))
        {
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        // Quote formula-protected cells that now start with a quote marker? No:
        // a leading single quote is harmless unquoted.
        return text;
    }

    public static bool StartsLikeFormula(string text)
    {
        // Find first char that is not whitespace/control (CSV injection can hide behind them).
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                continue;
            }

            return c is '=' or '+' or '-' or '@';
        }

        return false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _writer.Dispose();
            _disposed = true;
        }
    }
}
