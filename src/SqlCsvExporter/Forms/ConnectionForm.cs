using SqlCsvExporter.Models;

namespace SqlCsvExporter.Forms;

/// <summary>Modal dialog collecting SQL Server connection credentials.</summary>
public sealed class ConnectionForm : Form
{
    private readonly TextBox _serverBox = new() { Text = "127.0.0.1", PlaceholderText = "z. B. meinserver oder meinserver\\SQLEXPRESS oder meinserver,1433" };
    private readonly RadioButton _winAuth = new() { Text = "Windows-Authentifizierung", AutoSize = true };
    private readonly RadioButton _sqlAuth = new() { Text = "SQL Server-Authentifizierung", Checked = true, AutoSize = true };
    private readonly TextBox _userBox = new() { Text = "sa" };
    private readonly TextBox _passwordBox = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _trustCert = new() { Text = "Serverzertifikat vertrauen (selbstsigniert, nur interne Server)", AutoSize = true };
    private readonly Label _warning = new()
    {
        Text = "Verbindungen sind verschlüsselt. Nur für interne Server mit selbstsignierten Zertifikaten aktivieren.",
        AutoSize = true,
        ForeColor = Color.DarkRed,
    };

    public ConnectionSettings Settings { get; } = new();

    public ConnectionForm()
    {
        Text = "Mit SQL Server verbinden";
        try { Icon = new Icon("app.ico"); } catch { }
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Padding = new Padding(4),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _serverBox.Width = 320;
        _userBox.Width = 320;
        _passwordBox.Width = 320;

        layout.Controls.Add(new Label { Text = "Server:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0); // Server (Hostname/IP)
        layout.Controls.Add(_serverBox, 1, 0);
        layout.Controls.Add(_winAuth, 1, 1);
        layout.Controls.Add(_sqlAuth, 1, 2);
        layout.Controls.Add(new Label { Text = "Benutzer:", AutoSize = true }, 0, 3);
        layout.Controls.Add(_userBox, 1, 3);
        layout.Controls.Add(new Label { Text = "Kennwort:", AutoSize = true }, 0, 4);
        layout.Controls.Add(_passwordBox, 1, 4);
        layout.Controls.Add(_trustCert, 1, 5);
        layout.Controls.Add(_warning, 1, 6);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        var ok = new Button { Text = "Verbinden", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 1, 7);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;

        _sqlAuth.CheckedChanged += (_, _) =>
        {
            var sql = _sqlAuth.Checked;
            _userBox.Enabled = sql;
            _passwordBox.Enabled = sql;
        };

        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_serverBox.Text))
            {
                MessageBox.Show(this, "Bitte einen Servernamen eingeben.", "Server fehlt",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            Settings.Server = _serverBox.Text.Trim();
            Settings.UseWindowsAuth = _winAuth.Checked;
            Settings.Username = _sqlAuth.Checked ? _userBox.Text.Trim() : null;
            Settings.Password = _sqlAuth.Checked ? _passwordBox.Text : null;
            Settings.TrustServerCertificate = _trustCert.Checked;

            if (_sqlAuth.Checked && string.IsNullOrWhiteSpace(Settings.Username))
            {
                MessageBox.Show(this, "Bitte einen SQL-Benutzernamen eingeben.", "Benutzer fehlt",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        };

        FormClosed += (_, _) =>
        {
            // Never keep the password longer than needed in the dialog.
            _passwordBox.Text = string.Empty;
        };
    }
}
