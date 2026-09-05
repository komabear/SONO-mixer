using System.Drawing.Drawing2D;
using SONO.Core.Audio;

namespace SONO.App;

/// <summary>One channel track: header (name / device / gear), volume row, app drop-area, hotkey binds. Fully dock-based — fills whatever space it gets.</summary>
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

    private const int HeaderH = 46, VolRowH = 52, HotkeyRowH = 30, CaptionH = 20;

    private readonly ChannelDefinition _def;
    private readonly FlowLayoutPanel _apps;
    private readonly Label _name;
    private readonly Label _db;
    private readonly Label _deviceChip;
    private readonly Button _mute;
    private readonly Button _gear;
    private readonly SliderBar _slider;
    private readonly Dictionary<HotkeySlot, HotkeyCaptureBox> _hotkeyBoxes = new();

    public string ChannelId => _def.Id;

    public event Action<string, float>? VolumeLive;
    public event Action<string>? VolumeCommitted;
    public event Action<string>? MuteToggled;
    public event Action<string, string>? ExeDropped;
    public event Action<string>? ExeRemoved;
    public event Action<string, HotkeySlot, string?>? HotkeySet;
    public event Action<string>? DefinitionEdited;
    public event Action<string>? AddAppRequested;
    public event Action<string>? DevicePickRequested;
    public event Action<string>? MakeDefaultRequested;

    public ChannelCard(ChannelDefinition def)
    {
        _def = def;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Padding = new Padding(12);
        var accent = ColorOf(def);

        // ---- header: name | device chip | gear ----
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = HeaderH, BackColor = Color.Transparent, ColumnCount = 3, RowCount = 1 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));

        _name = new Label
        {
            Text = def.Name,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        header.Controls.Add(_name, 0, 0);

        _deviceChip = new Label
        {
            Text = def.DeviceId is null ? "no device" : "device",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            ForeColor = Theme.Muted,
            BackColor = Theme.Chip,
            Padding = new Padding(6, 4, 6, 4),
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(0, 0, 8, 0),
        };
        _deviceChip.Click += (_, _) => DevicePickRequested?.Invoke(ChannelId);
        header.Controls.Add(_deviceChip, 1, 0);

        _gear = new Button
        {
            Text = "⚙",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Muted,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
            Margin = new Padding(0),
        };
        _gear.FlatAppearance.BorderSize = 0;
        _gear.Click += (_, _) => ShowGearMenu();
        header.Controls.Add(_gear, 2, 0);

        // ---- volume row: mute | slider | dB ----
        var volRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = VolRowH, BackColor = Color.Transparent, ColumnCount = 3, RowCount = 1 };
        volRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        volRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        volRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

        _mute = new Button
        {
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Text,
            BackColor = Theme.Chip,
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 10f),
            Margin = new Padding(0, 0, 8, 0),
        };
        _mute.FlatAppearance.BorderSize = 0;
        _mute.Click += (_, _) => { _def.Muted = !_def.Muted; UpdateMute(); MuteToggled?.Invoke(ChannelId); };
        volRow.Controls.Add(_mute, 0, 0);

        _slider = new SliderBar { Dock = DockStyle.Fill, Fill = accent, Margin = new Padding(4, 6, 4, 6) };
        _slider.ValueChanged += v => { _def.Volume = v; UpdateDb(); VolumeLive?.Invoke(ChannelId, v); };
        _slider.EditCommitted += _ => VolumeCommitted?.Invoke(ChannelId);
        volRow.Controls.Add(_slider, 1, 0);

        _db = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.Muted,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9.5f),
        };
        volRow.Controls.Add(_db, 2, 0);

        // ---- hotkeys (bottom) — 3rd column is an explicit clear button ----
        var hotkeys = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = HotkeyRowH * 3 + 6, BackColor = Color.Transparent, ColumnCount = 3, RowCount = 3 };
        hotkeys.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        hotkeys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hotkeys.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        int y = 0;
        foreach (var slot in Slots)
        {
            hotkeys.RowStyles.Add(new RowStyle(SizeType.Absolute, HotkeyRowH));
            var lbl = new Label
            {
                Text = SlotNames[slot],
                Dock = DockStyle.Fill,
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 10f),
            };
            var box = new HotkeyCaptureBox
            {
                Dock = DockStyle.Fill,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Tag = def.GetHotkey(slot),
                Text = def.GetHotkey(slot) ?? "(none)",
                Font = new Font("Segoe UI", 10.5f),
                Margin = new Padding(0, 2, 0, 2),
            };
            var clear = new Button
            {
                Text = "✕",
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Padding(4, 2, 0, 2),
            };
            clear.FlatAppearance.BorderSize = 0;
            var slotCaptured = slot;
            void ApplyHotkey(string? text)
            {
                _def.SetHotkey(slotCaptured, text);
                box.Tag = text;
                box.Text = text ?? "(none)";
                HotkeySet?.Invoke(ChannelId, slotCaptured, text);
            }
            box.Committed += ApplyHotkey;
            clear.Click += (_, _) => ApplyHotkey(null);
            Tips.SetToolTip(clear, "Clear this shortcut");
            hotkeys.Controls.Add(lbl, 0, y);
            hotkeys.Controls.Add(box, 1, y);
            hotkeys.Controls.Add(clear, 2, y);
            _hotkeyBoxes[slot] = box;
            y++;
        }

        // ---- apps area (fill): caption + flow ----
        var appsWrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.CardInner, Padding = new Padding(1) };
        appsWrap.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(Theme.Border), 0, 0, appsWrap.Width - 1, appsWrap.Height - 1);
        var caption = new Label
        {
            Text = "APPS  (drag to route)",
            Dock = DockStyle.Top,
            Height = CaptionH,
            ForeColor = Theme.Muted,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 7f, FontStyle.Bold),
            Padding = new Padding(4, 2, 0, 0),
        };
        _apps = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.CardInner,
            Padding = new Padding(5),
            AllowDrop = true,
        };
        _apps.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(Chips.Format) == true) e.Effect = DragDropEffects.Move;
        };
        _apps.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(Chips.Format) is string exe) ExeDropped?.Invoke(exe, ChannelId);
        };
        appsWrap.Controls.Add(_apps);
        appsWrap.Controls.Add(caption);

        // dock order: WinForms lays out docked children in REVERSE add order, so the
        // header must be added LAST to be processed FIRST (top strip). Result top→bottom:
        // header (name/device/gear) → mute+volume → apps (fill) → hotkeys.
        Controls.Add(appsWrap);
        Controls.Add(volRow);
        Controls.Add(hotkeys);
        Controls.Add(header);

        _slider.SetValueExternal(def.Volume);
        UpdateMute();
        UpdateDb();
        UpdateDeviceChip();
    }

    private string? _deviceLabel;
    public void SetDeviceLabel(string? label) { _deviceLabel = label; UpdateDeviceChip(); }

    private void UpdateDeviceChip()
    {
        _deviceChip.Text = _def.DeviceId is null ? "no device" : _deviceLabel ?? "device ✓";
        _deviceChip.ForeColor = _def.DeviceId is null ? Theme.Muted : Theme.Accent;
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
        UpdateDeviceChip();
        Tips.SetToolTip(_gear, string.IsNullOrWhiteSpace(hotkeyError) ? "Channel settings" : "⚠ " + hotkeyError);
        DiffChips(owned);
    }

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
                Font = new Font("Segoe UI", 10f),
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
        if (Width <= 1 || Height <= 1) return;   // transient zero-size during table layout
        Region = new Region(SliderBar.RoundRect(0, 0, Width, Height, 12));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 1 || Height <= 1) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var bg = SliderBar.RoundRect(0, 0, Width - 1, Height - 1, 12);
        using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, bg);
        using var strip = SliderBar.RoundRect(0, 0, 6, Height, 3);
        using (var b = new SolidBrush(ColorOf(_def))) g.FillPath(b, strip);
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
