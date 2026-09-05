using System.Drawing.Drawing2D;
using SONO.Core.Audio;

namespace SONO.App;

/// <summary>One channel track: header, volume slider, mute, app drop-area, hotkey binds.</summary>
public class ChannelCard : Control
{
    private static readonly HotkeySlot[] Slots = { HotkeySlot.VolDown, HotkeySlot.VolUp, HotkeySlot.Mute };
    private static readonly Dictionary<HotkeySlot, string> SlotNames = new()
    {
        [HotkeySlot.VolDown] = "Vol −",
        [HotkeySlot.VolUp] = "Vol +",
        [HotkeySlot.Mute] = "Mute",
    };
    private static readonly ToolTip Tips = new();

    private readonly ChannelDefinition _def;
    private readonly FlowLayoutPanel _apps;
    private readonly Label _name;
    private readonly Label _db;
    private readonly Button _mute;
    private readonly Button _gear;
    private readonly SliderBar _slider;
    private readonly Dictionary<HotkeySlot, HotkeyCaptureBox> _hotkeyBoxes = new();
    private readonly Label _deviceChip;

    public string ChannelId => _def.Id;

    public event Action<string>? DevicePickRequested;
    public event Action<string>? MakeDefaultRequested;

    public event Action<string, float>? VolumeLive;
    public event Action<string>? VolumeCommitted;
    public event Action<string>? MuteToggled;
    public event Action<string, string>? ExeDropped;
    public event Action<string>? ExeRemoved;
    public event Action<string, HotkeySlot, string?>? HotkeySet;
    public event Action<string>? DefinitionEdited;
    public event Action<string>? RemoveRequested;
    public event Action<string>? AddAppRequested;
    public event Action? HeaderDrag;

    public ChannelCard(ChannelDefinition def)
    {
        _def = def;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Padding = new Padding(10);

        var accent = ColorOf(def);

        _name = new Label
        {
            Text = def.Name,
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            Location = new Point(28, 12),
            Size = new Size(170, 22),
        };
        _name.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) HeaderDrag?.Invoke(); };
        Controls.Add(_name);

        _deviceChip = new Label
        {
            Text = "no device",
            AutoSize = true,
            ForeColor = Theme.Muted,
            BackColor = Theme.Chip,
            Padding = new Padding(5, 3, 5, 3),
            Location = new Point(Width - 190, 12),
            Cursor = Cursors.Hand,
        };
        _deviceChip.Click += (_, _) => DevicePickRequested?.Invoke(ChannelId);
        Controls.Add(_deviceChip);

        _gear = new Button
        {
            Text = "⚙",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Muted,
            BackColor = Color.Transparent,
            Size = new Size(26, 24),
            Location = new Point(210, 9),
            Cursor = Cursors.Hand,
        };
        _gear.FlatAppearance.BorderSize = 0;
        _gear.Click += (_, _) => ShowGearMenu();
        Controls.Add(_gear);

        _slider = new SliderBar { Fill = accent, Location = new Point(14, 44), Size = new Size(220, 30), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _slider.ValueChanged += v => { _def.Volume = v; UpdateDb(); VolumeLive?.Invoke(ChannelId, v); };
        _slider.EditCommitted += _ => VolumeCommitted?.Invoke(ChannelId);
        Controls.Add(_slider);

        _mute = new Button
        {
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Text,
            BackColor = Theme.Chip,
            Size = new Size(84, 24),
            Location = new Point(14, 80),
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        _mute.FlatAppearance.BorderSize = 0;
        _mute.Click += (_, _) => { _def.Muted = !_def.Muted; UpdateMute(); MuteToggled?.Invoke(ChannelId); };
        Controls.Add(_mute);

        _db = new Label
        {
            Text = "",
            ForeColor = Theme.Muted,
            BackColor = Color.Transparent,
            Location = new Point(104, 84),
            Size = new Size(110, 18),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_db);

        _apps = new FlowLayoutPanel
        {
            BackColor = Theme.CardInner,
            Location = new Point(12, 122),
            Size = new Size(224, 200),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            Padding = new Padding(5),
            AllowDrop = true,
        };
        _apps.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(Theme.Border), 0, 0, _apps.Width - 1, _apps.Height - 1);
        _apps.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(Chips.Format) == true) e.Effect = DragDropEffects.Move;
        };
        _apps.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(Chips.Format) is string exe) ExeDropped?.Invoke(exe, ChannelId);
        };
        Controls.Add(_apps);

        int y = 0;
        foreach (var slot in Slots)
        {
            var lbl = new Label
            {
                Text = SlotNames[slot],
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
                Location = new Point(14, y),
                Size = new Size(46, 24),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            var box = new HotkeyCaptureBox
            {
                Location = new Point(64, y - 1),
                Size = new Size(170, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Tag = def.GetHotkey(slot),
                Text = def.GetHotkey(slot) ?? "(none)",
                Font = new Font("Segoe UI", 9f),
            };
            var slotCaptured = slot;
            box.Committed += text => { _def.SetHotkey(slotCaptured, text); HotkeySet?.Invoke(ChannelId, slotCaptured, text); };
            Controls.Add(lbl);
            Controls.Add(box);
            _hotkeyBoxes[slot] = box;
            y += 30;
        }

        _slider.SetValueExternal(def.Volume);
        UpdateMute();
        UpdateDb();
    }

    private static Color ColorOf(ChannelDefinition def) => ColorTranslator.FromHtml(def.ColorHex);

    /// <summary>Periodic refresh from the engine snapshot.</summary>
    public void Update(IReadOnlyList<SessionView> owned, string? hotkeyError)
    {
        _slider.SetValueExternal(_def.Volume);
        UpdateMute();
        UpdateDb();
        _name.Text = _def.Name;
        _slider.Fill = ColorOf(_def);
        _deviceChip.Text = _def.DeviceId is null ? "no device" : _deviceLabel ?? "device ✓";
        _deviceChip.ForeColor = _def.DeviceId is null ? Theme.Muted : Theme.Accent;
        Tips.SetToolTip(_gear, string.IsNullOrWhiteSpace(hotkeyError) ? "Channel settings" : "⚠ " + hotkeyError);
        DiffChips(owned);
    }

    private string? _deviceLabel;
    public void SetDeviceLabel(string? label) { _deviceLabel = label; }

    private void DiffChips(IReadOnlyList<SessionView> owned)
    {
        var live = owned.Select(s => s.Exe).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desired = _def.Executables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        desired.UnionWith(live);

        for (int i = _apps.Controls.Count - 1; i >= 0; i--)
            if (_apps.Controls[i] is Label l && l.Tag is string exe && !desired.Contains(exe))
                _apps.Controls.RemoveAt(i);

        var existing = new Dictionary<string, Label>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in _apps.Controls.OfType<Label>())
            if (l.Tag is string ex) existing[ex] = l;

        foreach (var exe in desired)
        {
            if (!existing.TryGetValue(exe, out var chip))
            {
                chip = Chips.Make(exe, onRemove: e => ExeRemoved?.Invoke(e));
                _apps.Controls.Add(chip);
                _apps.Controls.SetChildIndex(chip, _apps.Controls.Count - 2); // above the "+ add" chip
            }
            chip.ForeColor = live.Contains(exe) ? Theme.Text : Theme.Muted;
        }

        bool hasAdd = _apps.Controls.ContainsKey("__add");
        if (!hasAdd)
        {
            var add = new Label
            {
                Name = "__add",
                Text = "+ add",
                AutoSize = true,
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
                Padding = new Padding(7, 5, 7, 5),
                Margin = new Padding(3),
                Cursor = Cursors.Hand,
            };
            add.Click += (_, _) => AddAppRequested?.Invoke(ChannelId);
            _apps.Controls.Add(add);
        }
    }

    private void UpdateMute()
    {
        _mute.Text = _def.Muted ? "🔇 Muted" : "🔊 On";
        _mute.ForeColor = _def.Muted ? Theme.Danger : Theme.Text;
    }

    private void UpdateDb()
        => _db.Text = _def.Volume <= 0.0005f ? "−∞ dB" : $"{20 * Math.Log10(_def.Volume),+0:0.0} dB";

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Region = new Region(SliderBar.RoundRect(0, 0, Width, Height, 10));
        _name.Width = Width - 70;
        _gear.Location = new Point(Width - 38, 9);
        _deviceChip.Location = new Point(Width - _deviceChip.Width - 40, 12);
        _db.Width = Width - 114;
        _apps.Size = new Size(Width - 24, Height - 122 - 100);
        int y = Height - 96;
        foreach (var slot in Slots)
        {
            _hotkeyBoxes[slot].Location = new Point(64, y - 1);
            _hotkeyBoxes[slot].Width = Width - 76;
            y += 30;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var bg = SliderBar.RoundRect(0, 0, Width - 1, Height - 1, 10);
        using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, bg);
        using var strip = SliderBar.RoundRect(0, 0, 6, Height, 3);
        using (var b = new SolidBrush(ColorOf(_def))) g.FillPath(b, strip);

        g.DrawString("APPS", new Font("Segoe UI", 6.5f, FontStyle.Bold), new SolidBrush(Theme.Muted), 14, 108);
    }

    private void ShowGearMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Rename…", null, (_, _) =>
        {
            var name = PromptDialog.Show(FindForm()!, "Rename channel", _def.Name);
            if (!string.IsNullOrWhiteSpace(name)) { _def.Name = name.Trim(); _name.Text = _def.Name; DefinitionEdited?.Invoke(ChannelId); }
        });
        menu.Items.Add("Color…", null, (_, _) =>
        {
            var colorMenu = new ContextMenuStrip();
            foreach (var hex in Theme.Palette)
            {
                var item = new ToolStripMenuItem("■") { ForeColor = ColorTranslator.FromHtml(hex), BackColor = Theme.Card };
                var captured = hex;
                item.Click += (_, _) => { _def.ColorHex = captured; _slider.Fill = ColorOf(_def); Invalidate(); DefinitionEdited?.Invoke(ChannelId); };
                colorMenu.Items.Add(item);
            }
            colorMenu.Show(Cursor.Position);
        });
        menu.Items.Add("Output device…", null, (_, _) => DevicePickRequested?.Invoke(ChannelId));
        if (_def.DeviceId is not null)
            menu.Items.Add("Set device as Windows default", null, (_, _) => MakeDefaultRequested?.Invoke(ChannelId));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Remove channel…", null, (_, _) => RemoveRequested?.Invoke(ChannelId));
        menu.Show(_gear, new Point(0, _gear.Height));
    }
}

/// <summary>Tiny one-field modal input.</summary>
internal static class PromptDialog
{
    public static string? Show(IWin32Window owner, string title, string initial)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(320, 92),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
        };
        var box = new TextBox { Left = 12, Top = 14, Width = 294, Text = initial };
        var ok = new Button { Text = "OK", Left = 231, Top = 48, Width = 75, DialogResult = DialogResult.OK };
        form.Controls.Add(box);
        form.Controls.Add(ok);
        form.AcceptButton = ok;
        return form.ShowDialog(owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }
}
