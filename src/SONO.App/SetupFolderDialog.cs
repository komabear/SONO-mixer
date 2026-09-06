using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SONO.Core.Diagnostics;

namespace SONO.App;

/// <summary>Fixed dark modal: asks for the user's own VAC distribution folder, validates it
/// live (vrtaucbl.inf + x64\vrtaucbl.sys + catalog), and only enables OK when valid.
/// SONO never bundles VAC — the user points at their own lawfully obtained package.</summary>
public sealed class SetupFolderDialog : Form
{
    /// <summary>Full path to the validated vrtaucbl.inf (what pnputil needs).</summary>
    public string InfPath { get; private set; } = "";

    private readonly TextBox _path;
    private readonly Button _browse;
    private readonly Button _ok;
    private readonly Label _status;

    public SetupFolderDialog()
    {
        var theme = Theme.Current;

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 208);
        BackColor = theme.Card;
        TitleBarTheme.Apply(this);

        var title = new Label
        {
            Text = "Virtual Audio Cable package",
            Font = new Font("Segoe UI Semibold", 12.5f),
            ForeColor = theme.Text,
            AutoSize = true,
            Location = new Point(20, 14),
        };

        var help = new Label
        {
            Text = "SONO needs the Virtual Audio Cable driver (not included).\nPoint at the unpacked VAC 4.x folder you downloaded —\nit must contain vrtaucbl.inf and x64\\vrtaucbl.sys.",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.Muted,
            AutoSize = true,
            Location = new Point(20, 44),
        };

        _path = new TextBox
        {
            Location = new Point(20, 102),
            Size = new Size(430, 26),
            BackColor = theme.Field,
            ForeColor = theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _path.TextChanged += (_, _) => ValidateNow();

        _browse = new Button
        {
            Text = "Browse…",
            Location = new Point(458, 100),
            Size = new Size(84, 29),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Field,
            ForeColor = theme.Text,
        };
        _browse.FlatAppearance.BorderSize = 0;
        _browse.Click += (_, _) =>
        {
            using var fb = new FolderBrowserDialog { Description = "Select the unpacked Virtual Audio Cable folder" };
            if (fb.ShowDialog(this) == DialogResult.OK) _path.Text = fb.SelectedPath;
        };

        _status = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = theme.Muted,
            AutoSize = false,
            Size = new Size(522, 20),
            Location = new Point(20, 134),
        };

        _ok = new Button
        {
            Text = "Install",
            DialogResult = DialogResult.OK,
            Enabled = false,
            Location = new Point(364, 162), // placed by relayout; see below
            Size = new Size(88, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Accent,
            ForeColor = theme.Card,
        };
        _ok.FlatAppearance.BorderSize = 0;

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(460, 162),
            Size = new Size(82, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Field,
            ForeColor = theme.Text,
        };
        cancel.FlatAppearance.BorderSize = 0;

        // buttons sit on their own bottom strip
        ClientSize = new Size(560, 208);
        _ok.Location = new Point(364, 166);
        cancel.Location = new Point(460, 166);
        AcceptButton = _ok;
        CancelButton = cancel;

        Controls.AddRange(new Control[] { title, help, _path, _browse, _status, _ok, cancel });
    }

    private void ValidateNow()
    {
        var theme = Theme.Current;
        var dir = _path.Text.Trim();
        if (dir.Length == 0)
        {
            InfPath = ""; _ok.Enabled = false; _status.Text = ""; return;
        }

        var v = Core.SystemIntegrations.DeviceSetup.ValidateVacPackage(dir);
        _status.Text = v.Details.Replace("\n", "  ");
        _status.ForeColor = v.Valid ? theme.Accent : theme.Danger;
        InfPath = v.InfPath;
        _ok.Enabled = v.Valid;
        if (v.Valid) Log.Write($"setup: VAC package validated: {v.InfPath}");
    }
}
