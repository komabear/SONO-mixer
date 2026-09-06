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

    private const int HeaderH = 42, VolRowH = 30, HotkeyRowH = 34;

    private readonly ChannelDefinition _def;
    private readonly HotkeyManager _hotkeys;
    private readonly FlowLayoutPanel _apps;
    private readonly Label _name;
    private readonly Button _mute;
    private readonly Button _gear;
    private readonly SliderBar _slider;
    private readonly Dictionary<HotkeySlot, HotkeyCaptureBox> _hotkeyBoxes = new();
    private readonly Dictionary<HotkeySlot, Panel> _hotkeyFields = new();

    public string ChannelId => _def.Id;

    public event Action<string, float>? VolumeLive;
    public event Action<string>? VolumeCommitted;
    public event Action<string>? MuteToggled;
    public event Action<string, string>? ExeDropped;
    public event Action<string>? ExeRemoved;
    public event Action<string, HotkeySlot, string?>? HotkeySet;
    public event Action<string>? DefinitionEdited;
    public event Action<string>? AddAppRequested;
    public event Action<string>? MakeDefaultRequested;

    public ChannelCard(ChannelDefinition def, SONO.App.HotkeyManager hotkeys)
    {
        _def = def;
        _hotkeys = hotkeys;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Padding = new Padding(12);
        var accent = ColorOf(def);

        // ---- header: name | mute | gear (both small square icon buttons) ----
        // device mapping is implicit by name (Game → "SONO - Game"), so no chip/picker here
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = HeaderH, BackColor = Color.Transparent, ColumnCount = 3, RowCount = 1 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));

        _name = new Label
        {
            Text = def.Name,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),   // extra breathing room before the title
        };
        header.Controls.Add(_name, 0, 0);

        _mute = new Button
        {
            Text = "🔊",
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 6, 4, 6),
            Font = new Font("Segoe UI", 11f),
        };
        _mute.FlatAppearance.BorderSize = 0;
        _mute.Click += (_, _) => { _def.Muted = !_def.Muted; UpdateMute(); MuteToggled?.Invoke(ChannelId); };
        Tips.SetToolTip(_mute, "Mute / unmute channel");
        header.Controls.Add(_mute, 1, 0);

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

        // ---- slim volume bar directly under the header (padded wrapper: dock ignores Margin) ----
        var volWrap = new Panel { Dock = DockStyle.Top, Height = VolRowH + 6, BackColor = Color.Transparent, Padding = new Padding(8, 3, 8, 3) };
        _slider = new SliderBar { Dock = DockStyle.Fill, Fill = accent, Margin = new Padding(0) };
        _slider.ToolTip = "Channel volume (dB)";
        _slider.ValueChanged += v => { _def.Volume = v; VolumeLive?.Invoke(ChannelId, v); };
        _slider.EditCommitted += _ => VolumeCommitted?.Invoke(ChannelId);
        volWrap.Controls.Add(_slider);

        // ---- hotkeys (bottom, with breathing room) — 3rd column is an explicit clear button ----
        var hotkeysWrap = new Panel { Dock = DockStyle.Bottom, Height = HotkeyRowH * 3 + 22, BackColor = Color.Transparent, Padding = new Padding(0, 10, 0, 12) };
        var hotkeysGrid = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 3, RowCount = 3 };
        hotkeysGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        hotkeysGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hotkeysGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        int y = 0;
        foreach (var slot in Slots)
        {
            hotkeysGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, HotkeyRowH));
            var lbl = new Label
            {
                Text = SlotNames[slot],
                Dock = DockStyle.Fill,
                ForeColor = Theme.Muted,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 10f),
                Padding = new Padding(6, 0, 0, 0),   // align labels with the title inset
            };
            // pill container supplies the inner horizontal padding (TextBox ignores Padding).
            // Vertical padding stays small: a single-line EDIT clips its text when its client
            // height is under the font's line height (~19px at 10pt) — keep the box ≥ 22px.
            var field = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Field,
                Padding = new Padding(14, 4, 14, 4),
                Margin = new Padding(0, 2, 0, 2),
            };
            field.Resize += (_, _) =>
            {
                if (field.Width > 1 && field.Height > 1)
                    field.Region = new Region(SliderBar.RoundRect(0, 0, field.Width, field.Height, field.Height / 2));
            };
            var box = new HotkeyCaptureBox
            {
                Dock = DockStyle.Fill,
                Tag = def.GetHotkey(slot),
                Text = def.GetHotkey(slot) ?? "(none)",
                Font = new Font("Segoe UI", 10f),
                Margin = new Padding(0),
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
            box.OnCaptureStart = () => _hotkeys.Suspend();
            box.OnCaptureEnd = () => _hotkeys.Resume();
            void ApplyHotkey(string? text)
            {
                _def.SetHotkey(slotCaptured, text);
                box.Tag = text;
                box.HotkeyText = text ?? "(none)";   // owner-drawn control: update + repaint
                HotkeySet?.Invoke(ChannelId, slotCaptured, text);
            }
            box.Committed += ApplyHotkey;
            clear.Click += (_, _) =>
            {
                clear.Focus();              // take focus so the capture box exits capture mode
                ApplyHotkey(null);
                box.Parent?.Focus();        // hand focus back to the pill container
            };
            Tips.SetToolTip(clear, "Clear this shortcut");
            hotkeysGrid.Controls.Add(lbl, 0, y);
            field.Controls.Add(box);
            hotkeysGrid.Controls.Add(field, 1, y);
            hotkeysGrid.Controls.Add(clear, 2, y);
            _hotkeyBoxes[slot] = box;
            _hotkeyFields[slot] = field;
            y++;
        }
        hotkeysWrap.Controls.Add(hotkeysGrid);

        // ---- apps area (fill): tonal surface, no stroke (material) ----
        var appsWrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Elevated, Padding = new Padding(1) };
        var caption = new Label
        {
            Text = "APPS  (drag to route)",
            Dock = DockStyle.Top,
            Height = 20,
            ForeColor = Theme.Muted,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 7f, FontStyle.Bold),
            Padding = new Padding(4, 2, 0, 0),
        };
        _apps = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Elevated,
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
        // header (name/mute/gear) → slim volume bar → apps (fill) → hotkeys.
        Controls.Add(appsWrap);
        Controls.Add(volWrap);
        Controls.Add(hotkeysWrap);
        Controls.Add(header);

        _slider.SetValueExternal(def.Volume);
        UpdateMute();
    }

    private static Color ColorOf(ChannelDefinition def) => ColorTranslator.FromHtml(def.ColorHex);

    private bool _compact;

    /// <summary>Compact mode (window ≤ half screen height): hide the shortcut block entirely.</summary>
    public void SetCompact(bool compact)
    {
        if (_compact == compact) return;
        _compact = compact;
        foreach (var f in _hotkeyFields.Values) f.Visible = !compact;
        foreach (var b in _hotkeyBoxes.Values) b.Visible = !compact;
        hotkeysWrapVisible();
    }

    private void hotkeysWrapVisible()
    {
        // the wrapper itself collapses so the apps area reclaims the space
        Controls.OfType<Panel>().FirstOrDefault(p => p.Dock == DockStyle.Bottom && p.Padding == new Padding(0, 10, 0, 12))!
            .Height = _compact ? 0 : HotkeyRowH * 3 + 22;
    }

    /// <summary>Periodic refresh from the engine snapshot.</summary>
    public void Update(IReadOnlyList<SessionView> owned, string? hotkeyError)
    {
        _slider.SetValueExternal(_def.Volume);
        UpdateMute();
        _name.Text = _def.Name;
        _slider.Fill = ColorOf(_def);
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
        _mute.Text = _def.Muted ? "🔇" : "🔊";
        _mute.ForeColor = _def.Muted ? Theme.Danger : Theme.Text;
        Tips.SetToolTip(_mute, _def.Muted ? "Unmute channel" : "Mute channel");
    }

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
        menu.Items.Add("Set device as Windows default", null, (_, _) => MakeDefaultRequested?.Invoke(ChannelId));
        menu.Show(_gear, new Point(0, _gear.Height));
    }
}

/// <summary>Tiny one-field modal input — fixed dialog, dark material styling, never resizable.</summary>
internal static class PromptDialog
{
    public static string? Show(IWin32Window owner, string title, string initial)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(320, 96),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ShowIcon = false,
            BackColor = Theme.Card,
        };
        var box = new TextBox
        {
            Left = 14, Top = 16, Width = 292,
            Text = initial,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 10.5f),
        };
        var ok = new Button
        {
            Text = "OK", Left = 233, Top = 52, Width = 73, Height = 28,
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Theme.Bg,
            Cursor = Cursors.Hand,
        };
        ok.FlatAppearance.BorderSize = 0;
        form.Controls.Add(box);
        form.Controls.Add(ok);
        form.AcceptButton = ok;
        form.CancelButton = ok;
        return form.ShowDialog(owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }
}
