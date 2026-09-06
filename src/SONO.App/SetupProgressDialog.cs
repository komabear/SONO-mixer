using System;
using System.Drawing;
using System.Windows.Forms;

namespace SONO.App;

/// <summary>Dark non-closable progress dialog shown while the elevated device-setup runs.
/// Marquee bar + step text so the user always knows setup is alive (UAC may pop over it).</summary>
public sealed class SetupProgressDialog : Form
{
    private readonly Label _status;

    public SetupProgressDialog()
    {
        var theme = Theme.Current;

        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(470, 168);
        BackColor = theme.Card;

        var title = new Label
        {
            Text = "Setting up SONO devices",
            Font = new Font("Segoe UI Semibold", 12.5f),
            ForeColor = theme.Text,
            AutoSize = true,
            Location = new Point(22, 16),
        };

        _status = new Label
        {
            Text = "Preparing…",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = theme.Muted,
            AutoSize = false,
            Size = new Size(426, 40),
            Location = new Point(22, 48),
        };

        var bar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 25,
            Location = new Point(22, 94),
            Size = new Size(426, 10),
        };

        var note = new Label
        {
            Text = "Approve the administrator prompt if it appears.\nThis can take up to a minute — audio may briefly stop.",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = theme.Muted,
            AutoSize = false,
            Size = new Size(426, 36),
            Location = new Point(22, 116),
        };

        Controls.AddRange(new Control[] { title, _status, bar, note });
    }

    public void SetStatus(string text)
    {
        _status.Text = text;
        _status.Invalidate();
        Update();
    }
}
