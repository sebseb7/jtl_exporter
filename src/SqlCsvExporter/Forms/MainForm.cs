using SqlCsvExporter.Models;
using SqlCsvExporter.Services;

namespace SqlCsvExporter.Forms;

/// <summary>Main workflow: database picker, category scope, JTL data export to ZIP.</summary>
public sealed class MainForm : Form
{
    private readonly SqlServerService _sql = new();
    private readonly JtlExportService _export = new();
    private ConnectionSettings? _settings;
    private CancellationTokenSource? _cts;
    private bool _exporting;

    private readonly Button _connectButton = new() { Text = "Verbinden…", AutoSize = true };
    private readonly Button _disconnectButton = new() { Text = "Trennen", AutoSize = true, Enabled = false };
    private readonly ComboBox _mandantBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Enabled = false };
    private readonly ComboBox _databaseBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Enabled = false };
    private readonly CheckedListBox _categoriesBox = new() { CheckOnClick = true, Dock = DockStyle.Fill, Enabled = false };
    private readonly Button _selectAllButton = new() { Text = "Alle auswählen", AutoSize = true, Enabled = false };
    private readonly Button _clearButton = new() { Text = "Auswahl aufheben", AutoSize = true, Enabled = false };
    private readonly CheckBox _includeImagesBox = new() { Text = "Bilder (pictures/) in ZIP archivieren", Checked = true, AutoSize = true };
    private readonly Button _exportButton = new() { Text = "Datenexport (ZIP) erstellen…", AutoSize = true, Enabled = false };
    private readonly Button _cancelButton = new() { Text = "Abbrechen", AutoSize = true, Enabled = false };
    private readonly Label _status = new() { Text = "Nicht verbunden.", AutoSize = true, Dock = DockStyle.Fill };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous };

    private List<CategoryInfo> _categories = [];
    private List<MandantInfo> _mandants = [];

    public MainForm()
    {
        Text = "JTL Medien Exporter – Datenexport";
        try { Icon = new Icon("app.ico"); } catch { }
        MinimumSize = new Size(720, 560);
        StartPosition = FormStartPosition.CenterScreen;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        top.Controls.Add(_connectButton);
        top.Controls.Add(_disconnectButton);
        top.Controls.Add(new Label { Text = "Mandant:", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 8, 0, 0) });
        top.Controls.Add(_mandantBox);
        top.Controls.Add(new Label { Text = "Datenbank:", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 8, 0, 0) });
        top.Controls.Add(_databaseBox);

        var middle = new GroupBox
        {
            Text = "Kategorien für Datenexport",
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
        };
        middle.Controls.Add(_categoriesBox);

        var optionsPanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        optionsPanel.Controls.Add(_selectAllButton);
        optionsPanel.Controls.Add(_clearButton);
        optionsPanel.Controls.Add(_includeImagesBox);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        actions.Controls.Add(_exportButton);
        actions.Controls.Add(_cancelButton);

        var statusPanel = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 1, AutoSize = true, Padding = new Padding(8) };
        statusPanel.Controls.Add(_status, 0, 0);
        statusPanel.Controls.Add(_progress, 0, 1);

        Controls.Add(middle);
        Controls.Add(optionsPanel);
        Controls.Add(actions);
        Controls.Add(statusPanel);
        Controls.Add(top);

        _connectButton.Click += async (_, _) => await ConnectAsync();
        _disconnectButton.Click += (_, _) => Disconnect();
        _mandantBox.SelectedIndexChanged += async (_, _) => await MandantChangedAsync();
        _databaseBox.SelectedIndexChanged += async (_, _) => await LoadCategoriesAsync();
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
        _categories = [];
        _mandants = [];
        _categoriesBox.Items.Clear();
        _categoriesBox.Enabled = false;
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

    private async Task MandantChangedAsync()
    {
        if (_settings is null || _mandantBox.SelectedItem is not MandantInfo mandant || _exporting)
        {
            return;
        }

        SetBusy(true, $"Ermittle Datenbank für Mandant '{mandant.Name}'…");
        _categoriesBox.Items.Clear();
        _categories = [];
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
                SetStatus($"Mandant '{mandant.Name}' → Datenbank '{mandant.Database}'. Lade Kategorien…");
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

    private async Task LoadCategoriesAsync()
    {
        if (_settings is null || _databaseBox.SelectedItem is not string database || _exporting)
        {
            return;
        }

        SetBusy(true, $"Lade Kategorien aus {database}…");
        _categoriesBox.Items.Clear();
        _categories = [];
        _exportButton.Enabled = false;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            _categories = (await _sql.GetTopCategoriesAsync(_settings, database, cts.Token)).ToList();

            foreach (var category in _categories)
            {
                _categoriesBox.Items.Add(category, true);
            }

            var hasCategories = _categories.Count > 0;
            _categoriesBox.Enabled = hasCategories;
            _selectAllButton.Enabled = hasCategories;
            _clearButton.Enabled = hasCategories;
            _exportButton.Enabled = hasCategories;
            SetStatus(_categories.Count == 0
                ? $"Datenbank '{database}' enthält keine Haupt Kategorien (kOberKategorie = 0)."
                : $"Datenbank '{database}': {_categories.Count} Kategorie(n) geladen.");
        }
        catch (ExportException ex)
        {
            SetStatus("Kategorien konnten nicht geladen werden.");
            MessageBox.Show(this, ex.Message, "Kategorien laden fehlgeschlagen", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void SetAllChecked(bool checkedState)
    {
        for (var i = 0; i < _categoriesBox.Items.Count; i++)
        {
            _categoriesBox.SetItemChecked(i, checkedState);
        }
    }

    private async Task ExportAsync()
    {
        if (_settings is null || _databaseBox.SelectedItem is not string database || _exporting)
        {
            return;
        }

        var topKats = new List<int>();
        for (var i = 0; i < _categoriesBox.Items.Count; i++)
        {
            if (_categoriesBox.GetItemChecked(i))
            {
                topKats.Add(_categories[i].KKategorie);
            }
        }

        if (topKats.Count == 0)
        {
            MessageBox.Show(this, "Bitte mindestens eine Kategorie für den Export auswählen.", "Keine Kategorien ausgewählt",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var stamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");

        using var save = new SaveFileDialog
        {
            Filter = "ZIP-Archiv (*.zip)|*.zip",
            DefaultExt = "zip",
            FileName = $"datenexport-{stamp}.zip",
            OverwritePrompt = true,
            Title = "Zieldatei für Datenexport wählen",
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

        var options = new JtlExportOptions
        {
            CategoryScope = new CategoryScope
            {
                TopKats = topKats,
            },
            IncludeImages = _includeImagesBox.Checked,
        };

        var progress = new Progress<ExportProgress>(p =>
        {
            if (p.TotalSteps > 0)
            {
                _progress.Value = Math.Clamp((int)(p.CompletedSteps * 100 / p.TotalSteps), 0, 100);
            }

            SetStatus($"{p.Stage} ({p.CompletedSteps}/{p.TotalSteps})…");
        });

        try
        {
            await Task.Run(() => _export.ExportAsync(
                _settings, database, save.FileName, options, progress, _cts.Token));
            _progress.Value = 100;
            SetStatus($"Fertig. Datenexport erfolgreich nach {save.FileName} geschrieben.");
            MessageBox.Show(this, $"Datenexport erfolgreich abgeschlossen!\n\nDatei:\n{save.FileName}",
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
        _categoriesBox.Enabled = enabled && _categories.Count > 0;
        _selectAllButton.Enabled = enabled && _categories.Count > 0;
        _clearButton.Enabled = enabled && _categories.Count > 0;
        _exportButton.Enabled = enabled && _categories.Count > 0;
        _includeImagesBox.Enabled = enabled;
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
