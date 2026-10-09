using SqlCsvExporter.Models;
using SqlCsvExporter.Services;

namespace SqlCsvExporter.Forms;

/// <summary>Main workflow: database picker, table checklist, export to ZIP.</summary>
public sealed class MainForm : Form
{
    private readonly SqlServerService _sql = new();
    private readonly ZipExportService _export = new();
    private ConnectionSettings? _settings;
    private CancellationTokenSource? _cts;
    private bool _exporting;

    private readonly Button _connectButton = new() { Text = "Verbinden…", AutoSize = true };
    private readonly Button _disconnectButton = new() { Text = "Trennen", AutoSize = true, Enabled = false };
    private readonly ComboBox _mandantBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280, Enabled = false };
    private readonly ComboBox _databaseBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280, Enabled = false };
    private readonly CheckedListBox _tablesBox = new() { CheckOnClick = true, Dock = DockStyle.Fill };
    private readonly Button _selectAllButton = new() { Text = "Alle auswählen", AutoSize = true, Enabled = false };
    private readonly Button _clearButton = new() { Text = "Auswahl löschen", AutoSize = true, Enabled = false };
    private readonly Button _exportButton = new() { Text = "Nach ZIP exportieren…", AutoSize = true, Enabled = false };
    private readonly Button _cancelButton = new() { Text = "Abbrechen", AutoSize = true, Enabled = false };
    private readonly CheckBox _formulaBox = new() { Text = "Textzellen vor Excel-Formeln schützen (fügt '-Präfix hinzu)", Checked = true, AutoSize = true };
    private readonly Label _status = new() { Text = "Nicht verbunden.", AutoSize = true, Dock = DockStyle.Fill };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous };

    private List<TableInfo> _tables = new();
    private List<MandantInfo> _mandants = new();

    public MainForm()
    {
        Text = "JTL Medien Exporter";
        try { Icon = new Icon("app.ico"); } catch { }
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterScreen;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        top.Controls.Add(_connectButton);
        top.Controls.Add(_disconnectButton);
        top.Controls.Add(new Label { Text = "Mandant:", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 8, 0, 0) });
        top.Controls.Add(_mandantBox);
        top.Controls.Add(new Label { Text = "Datenbank:", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 8, 0, 0) });
        top.Controls.Add(_databaseBox);
        top.Controls.Add(_formulaBox);

        var middle = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        middle.Controls.Add(_tablesBox);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        actions.Controls.Add(_selectAllButton);
        actions.Controls.Add(_clearButton);
        actions.Controls.Add(_exportButton);
        actions.Controls.Add(_cancelButton);

        var statusPanel = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 1, AutoSize = true, Padding = new Padding(8) };
        statusPanel.Controls.Add(_status, 0, 0);
        statusPanel.Controls.Add(_progress, 0, 1);

        Controls.Add(middle);
        Controls.Add(actions);
        Controls.Add(statusPanel);
        Controls.Add(top);

        _connectButton.Click += async (_, _) => await ConnectAsync();
        _disconnectButton.Click += (_, _) => Disconnect();
        _mandantBox.SelectedIndexChanged += async (_, _) => await MandantChangedAsync();
        _databaseBox.SelectedIndexChanged += async (_, _) => await LoadTablesAsync();
        _selectAllButton.Click += (_, _) => SetAllChecked(true);
        _clearButton.Click += (_, _) => SetAllChecked(false);
        _exportButton.Click += async (_, _) => await ExportAsync();
        _cancelButton.Click += (_, _) => _cts?.Cancel();

        FormClosing += (_, e) =>
        {
            if (_exporting)
            {
                var result = MessageBox.Show(this,
                    "Ein Export läuft. Abbrechen und schließen?",
                    "Export läuft", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result == DialogResult.No)
                {
                    e.Cancel = true;
                    return;
                }

                _cts?.Cancel();
            }
        };

        Shown += async (_, _) => await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (_exporting)
        {
            return;
        }

        using var dialog = new ConnectionForm();
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _settings = dialog.Settings;
        SetBusy(true, "Verbinde…");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await _sql.TestConnectionAsync(_settings, cts.Token);

            // Load JTL mandants from eazybusiness.dbo.tMandant.
            _mandants = (await _sql.GetMandantsAsync(_settings, cts.Token)).ToList();
            _mandantBox.Items.Clear();
            foreach (var mandant in _mandants)
            {
                _mandantBox.Items.Add(mandant);
            }

            _mandantBox.Enabled = _mandants.Count > 0;
            _disconnectButton.Enabled = true;
            _connectButton.Enabled = false;

            if (_mandants.Count == 0)
            {
                SetStatus("Verbunden. Keine Mandanten in eazybusiness.dbo.tMandant gefunden.");
            }
            else
            {
                SetStatus($"Verbunden mit {_settings.Server}. Bitte Mandant wählen.");
                _mandantBox.SelectedIndex = 0;
            }
        }
        catch (ExportException ex)
        {
            _settings?.ClearCredentials();
            _settings = null;
            SetStatus("Nicht verbunden.");
            MessageBox.Show(this, ex.Message, "Verbindung fehlgeschlagen", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void Disconnect()
    {
        if (_exporting)
        {
            return;
        }

        _settings?.ClearCredentials();
        _settings = null;
        _tables = new();
        _mandants = new();
        _tablesBox.Items.Clear();
        _mandantBox.Items.Clear();
        _mandantBox.Enabled = false;
        _databaseBox.Items.Clear();
        _databaseBox.Enabled = false;
        _disconnectButton.Enabled = false;
        _connectButton.Enabled = true;
        _selectAllButton.Enabled = false;
        _clearButton.Enabled = false;
        _exportButton.Enabled = false;
        SetStatus("Nicht verbunden.");
    }

    /// <summary>
    /// When the mandant changes, resolve its database (cDB) and offer it
    /// in the database dropdown. Falls back to the full database list if
    /// the mandant database is not accessible.
    /// </summary>
    private async Task MandantChangedAsync()
    {
        if (_settings is null || _mandantBox.SelectedItem is not MandantInfo mandant || _exporting)
        {
            return;
        }

        SetBusy(true, $"Ermittle Datenbank für Mandant '{mandant.Name}'…");
        _tablesBox.Items.Clear();
        _tables = new();
        _databaseBox.Items.Clear();
        _databaseBox.Enabled = false;
        _exportButton.Enabled = false;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var databases = await _sql.GetDatabasesAsync(_settings, cts.Token);

            if (databases.Contains(mandant.Database, StringComparer.OrdinalIgnoreCase))
            {
                _databaseBox.Items.Add(mandant.Database);
            }
            else
            {
                // Mandant DB not visible: show everything so the user can pick manually.
                foreach (var db in databases)
                {
                    _databaseBox.Items.Add(db);
                }
            }

            _databaseBox.Enabled = _databaseBox.Items.Count > 0;

            if (_databaseBox.Items.Count == 0)
            {
                SetStatus($"Mandant '{mandant.Name}': Datenbank '{mandant.Database}' ist nicht zugänglich.");
            }
            else
            {
                SetStatus($"Mandant '{mandant.Name}' → Datenbank '{mandant.Database}'. Lade Tabellen…");
                _databaseBox.SelectedIndex = 0;
            }
        }
        catch (ExportException ex)
        {
            SetStatus("Mandanten-Datenbank konnte nicht ermittelt werden.");
            MessageBox.Show(this, ex.Message, "Mandant fehlgeschlagen", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async Task LoadTablesAsync()
    {
        if (_settings is null || _databaseBox.SelectedItem is not string database || _exporting)
        {
            return;
        }

        SetBusy(true, $"Lade Tabellen aus {database}…");
        _tablesBox.Items.Clear();
        _tables = new();
        _exportButton.Enabled = false;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            _tables = (await _sql.GetTablesAsync(_settings, database, cts.Token)).ToList();
            foreach (var table in _tables)
            {
                var label = table.HasSelectAccess ? table.DisplayName : $"{table.DisplayName} (keine SELECT-Berechtigung)";
                _tablesBox.Items.Add(label, false);
            }

            var selectable = _tables.Count(t => t.HasSelectAccess) > 0;
            _selectAllButton.Enabled = selectable;
            _clearButton.Enabled = selectable;
            _exportButton.Enabled = selectable;
            SetStatus(_tables.Count == 0
                ? $"Datenbank '{database}' enthält keine sichtbaren Benutzertabellen."
                : $"Datenbank '{database}': {_tables.Count} Tabelle(n). Bitte zu exportierende Tabellen ankreuzen.");
        }
        catch (ExportException ex)
        {
            SetStatus("Tabellen konnten nicht geladen werden.");
            MessageBox.Show(this, ex.Message, "Tabellen laden fehlgeschlagen", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void SetAllChecked(bool checkedState)
    {
        for (var i = 0; i < _tablesBox.Items.Count; i++)
        {
            // Only check tables we can actually read.
            _tablesBox.SetItemChecked(i, checkedState && _tables[i].HasSelectAccess);
        }
    }

    private async Task ExportAsync()
    {
        if (_settings is null || _databaseBox.SelectedItem is not string database || _exporting)
        {
            return;
        }

        var selected = new List<TableInfo>();
        for (var i = 0; i < _tablesBox.Items.Count; i++)
        {
            if (_tablesBox.GetItemChecked(i) && _tables[i].HasSelectAccess)
            {
                selected.Add(_tables[i]);
            }
        }

        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Bitte mindestens eine Tabelle mit SELECT-Berechtigung auswählen.", "Nichts ausgewählt",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var save = new SaveFileDialog
        {
            Filter = "ZIP-Archiv (*.zip)|*.zip",
            DefaultExt = "zip",
            FileName = $"{database}_export.zip",
            OverwritePrompt = true,
            Title = "Zieldatei für Export wählen",
        };
        if (save.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _exporting = true;
        _cts = new CancellationTokenSource();
        _cancelButton.Enabled = true;
        SetControlsEnabled(false);
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = 0;

        var options = new ExportOptions { ProtectAgainstExcelFormulas = _formulaBox.Checked };
        var progress = new Progress<ExportProgress>(p =>
        {
            if (p.TotalTables > 0)
            {
                _progress.Value = Math.Clamp((int)(p.CompletedTables * 100 / p.TotalTables), 0, 100);
            }

            SetStatus($"Exportiere {p.CurrentTable} ({p.CompletedTables}/{p.TotalTables}) — {p.RowsWrittenCurrentTable} Zeilen…");
        });

        try
        {
            await Task.Run(() => _export.ExportAsync(
                _settings, database, selected, save.FileName, options, progress, _cts.Token));
            _progress.Value = 100;
            SetStatus($"Fertig. {selected.Count} Tabelle(n) nach {save.FileName} geschrieben.");
            MessageBox.Show(this, $"Export abgeschlossen.\n{selected.Count} Tabelle(n) geschrieben nach:\n{save.FileName}",
                "Export abgeschlossen", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (ExportException ex)
        {
            SetStatus("Export wurde nicht abgeschlossen.");
            MessageBox.Show(this, ex.Message, "Export fehlgeschlagen", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _exporting = false;
            _cancelButton.Enabled = false;
            SetControlsEnabled(true);
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        _connectButton.Enabled = enabled && _settings is null;
        _disconnectButton.Enabled = enabled && _settings is not null;
        _mandantBox.Enabled = enabled && _mandantBox.Items.Count > 0;
        _databaseBox.Enabled = enabled && _databaseBox.Items.Count > 0;
        _selectAllButton.Enabled = enabled && _tables.Any(t => t.HasSelectAccess);
        _clearButton.Enabled = enabled && _tables.Any(t => t.HasSelectAccess);
        _exportButton.Enabled = enabled && _tables.Any(t => t.HasSelectAccess);
    }

    private void SetBusy(bool busy, string? message)
    {
        UseWaitCursor = busy;
        if (message is not null)
        {
            SetStatus(message);
        }
    }

    private void SetStatus(string message)
    {
        _status.Text = message;
    }
}
