using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SONO.App;

/// <summary>Small dark About dialog: logo centered on top, app name + version, links below.</summary>
public sealed class AboutDialog : Form
{
    public AboutDialog()
    {
        var theme = Theme.Current;

        Text = "About SONO Mixer";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(360, 300);
        BackColor = theme.Card;
        TitleBarTheme.Apply(this);

        // logo (the same asset used as the panel mascot), centered, max width
        string logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
        if (File.Exists(logoPath))
        {
            try
            {
                using var src = Image.FromFile(logoPath);
                int maxW = ClientSize.Width - 80, maxH = 96;
                float scale = Math.Min(maxW / (float)src.Width, maxH / (float)src.Height);
                int w = Math.Max(1, (int)(src.Width * scale));
                int h = Math.Max(1, (int)(src.Height * scale));
                var logo = new PictureBox
                {
                    Image = Image.FromFile(logoPath),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Size = new Size(w, h),
                    Location = new Point((ClientSize.Width - w) / 2, 18),
                    BackColor = Color.Transparent,
                };
                Controls.Add(logo);
            }
            catch { /* logo missing/corrupt → skip, text carries the dialog */ }
        }

        string version = "1.0";
        try { version = Application.ProductVersion.Split('+')[0]; } catch { }

        var name = new Label
        {
            Text = "SONO Mixer",
            Font = new Font("Segoe UI Semibold", 14f),
            ForeColor = theme.Text,
            AutoSize = true,
            Location = new Point(0, 122),
        };
        var ver = new Label
        {
            Text = $"Version {version}",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.Muted,
            AutoSize = true,
            Location = new Point(0, 150),
        };
        var info = new Label
        {
            Text = "Per-app audio routing for Windows — four channels\n(Game / Chat / Media / Aux), global hotkeys, and a mix\noutput. Powered by Virtual Audio Cable.",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.Muted,
            AutoSize = false,
            Size = new Size(330, 54),
            Location = new Point(15, 176),
            TextAlign = ContentAlignment.TopCenter,
        };

        var repo = new LinkLabel
        {
            Text = "github.com/komabear/SONO-mixer",
            Font = new Font("Segoe UI", 9.5f),
            LinkColor = theme.Accent,
            ActiveLinkColor = theme.Accent,
            AutoSize = true,
            Location = new Point(0, 234),
            BackColor = Color.Transparent,
        };
        repo.LinkClicked += (_, _) => OpenRepo();
        repo.Left = (ClientSize.Width - repo.PreferredWidth) / 2;

        // make the whole repo line clickable
        repo.LinkArea = new LinkArea(0, repo.Text.Length);

        var close = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Field,
            ForeColor = theme.Text,
            Size = new Size(88, 28),
            Location = new Point((ClientSize.Width - 88) / 2, 260),
        };
        close.FlatAppearance.BorderSize = 0;
        CancelButton = close;

        Controls.AddRange(new Control[] { name, ver, info, repo, close });

        // center the autosized labels after they measure themselves
        name.Left = (ClientSize.Width - name.PreferredWidth) / 2;
        ver.Left = (ClientSize.Width - ver.PreferredWidth) / 2;

        AcceptButton = close;
    }

    private static void OpenRepo()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://github.com/komabear/SONO-mixer",
            UseShellExecute = true,
        }); } catch { }
    }
}
