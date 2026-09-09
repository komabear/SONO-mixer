using NAudio.CoreAudioApi;
using SONO.Core.Audio;
using SONO.Core.Settings;

namespace SONO.App;

/// <summary>Main window: channel tracks on the left, live app list on the right, tray icon in the corner.</summary>
public class MixerForm : Form
{
    private readonly AudioEngine _engine;
    private readonly HotkeyManager _hotkeys;
    private readonly AppSettings _settings;
    private readonly OsdWindow _osd = new();
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _reconcileDebounce;
    private readonly Dictionary<string, ChannelCard> _cards = new();
    private readonly Dictionary<string, Panel> _appRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolStripMenuItem _miAutostart, _miMinimized;
    private readonly Label _status;
    private readonly TableLayoutPanel _table;
    private DarkComboBox _outputPick = new();
    private Button _outputBtn = new();
    private ToolStripDropDown _outputMenu = new();
    private readonly List<string> _outputIds = new();
    private bool _suppressPick;
    private readonly SmoothFlowPanel _appList = new();
    private bool _allowClose;
    private bool _balloonShown;
    private string _hotkeyError = "";
    private readonly bool _launchedAtBoot;
    private Image? _mascot;

    /// <summary>Raised when the UI must be recreated (theme change). Engine/routing/hotkeys are kept alive.</summary>
    public event Action? UiRestartRequested;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SuppressCleanup { get; set; }

    public MixerForm(AudioEngine engine, HotkeyManager hotkeys,
        AppSettings settings, bool launchedAtBoot)
    {
        _engine = engine;
        _hotkeys = hotkeys;
        _settings = settings;
        _launchedAtBoot = launchedAtBoot;
        _hotkeys.Pressed += OnHotkey;

        Text = "SONO Mixer";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 660);
        MinimumSize = new Size(660, 560);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        KeyPreview = false;
        Icon = MakeIcon();
        TitleBarTheme.Apply(this);
        _osd.AnchorProvider = () => { lock (_settings) return _settings.OsdAnchor; };
        _osd.PositionAtAnchorPublic();

        // ---- left: channel tracks ----
        // ---- left: channel tracks (responsive grid: 4-up when wide, 2×2 when narrow) ----
        _table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Padding = new Padding(10),
            ColumnCount = 4,
            RowCount = 1,
            AllowDrop = true,
        };
        for (int i = 0; i < 4; i++) _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        _table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _table.Resize += (_, _) => ReflowGrid();
        _table.DragOver += (_, e) => { if (e.Data?.GetDataPresent("SONO_CARD") == true) e.Effect = DragDropEffects.Move; };
        _table.DragDrop += FlowCardDrop;
        Controls.Add(_table);

        // ---- right: live applications ----
        var right = new Panel { Dock = DockStyle.Right, Width = 312, BackColor = Theme.Card };
        var rightHead = new Label
        {
            Text = "Applications",
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            Location = new Point(12, 10),
            Size = new Size(200, 22),
        };
        var rightSub = new Label
        {
            Text = "Drag an app onto a channel to route it",
            ForeColor = Theme.Muted,
            Location = new Point(12, 32),
            Size = new Size(288, 18),
        };
        var settingsBtn = new Button
        {
            Text = "⚙",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Muted,
            BackColor = Color.Transparent,
            Size = new Size(28, 26),
            Location = new Point(272, 8),
            Cursor = Cursors.Hand,
        };
        settingsBtn.FlatAppearance.BorderSize = 0;

        var settingsMenu = new ContextMenuStrip();
        _miAutostart = new ToolStripMenuItem("Start with Windows") { Checked = _settings.StartWithWindows };
        _miAutostart.Click += (_, _) =>
        {
            _settings.StartWithWindows = !_settings.StartWithWindows;
            _miAutostart.Checked = _settings.StartWithWindows;
            ApplyAutostart();
            Save();
        };
        _miMinimized = new ToolStripMenuItem("Start minimized to tray") { Checked = _settings.StartMinimized };
        _miMinimized.Click += (_, _) =>
        {
            _settings.StartMinimized = !_settings.StartMinimized;
            _miMinimized.Checked = _settings.StartMinimized;
            Save();
        };
        var miStep = new ToolStripMenuItem("Hotkey volume step…");
        miStep.Click += (_, _) =>
        {
            var v = PromptDialog.Show(this, "Hotkey step (% per press)", MathF.Round(_settings.HotkeyStep * 100).ToString("0"));
            if (v is not null && float.TryParse(v, out var pct))
            {
                _settings.HotkeyStep = Math.Clamp(pct, 1f, 100f) / 100f;
                Save();
            }
        };
        var miTheme = new ToolStripMenuItem("Theme");
        foreach (var t in ThemeCatalog.All)
        {
            var item = new ToolStripMenuItem(t.Name) { Checked = t.Id == _settings.ThemeId };
            var captured = t;
            item.Click += (_, _) =>
            {
                if (captured.Id == _settings.ThemeId) return;
                _settings.ThemeId = captured.Id;
                Save();
                Theme.Apply(captured);
                UiRestartRequested?.Invoke();
            };
            miTheme.DropDownItems.Add(item);
        }
        var miOsd = new ToolStripMenuItem("Volume popup position");
        foreach (var (label, id) in new[]
        {
            ("Off", "off"),
            ("Top left", "top-left"), ("Top", "top"), ("Top right", "top-right"),
            ("Left", "left"), ("Center", "center"), ("Right", "right"),
            ("Bottom left", "bottom-left"), ("Bottom", "bottom"), ("Bottom right", "bottom-right"),
        })
        {
            var captured = id;
            var item = new ToolStripMenuItem(label) { Checked = _settings.OsdAnchor == id };
            item.Click += (_, _) =>
            {
                _settings.OsdAnchor = captured;
                Save();
                foreach (ToolStripMenuItem other in miOsd.DropDownItems) other.Checked = false;
                item.Checked = true;
            };
            miOsd.DropDownItems.Add(item);
        }
        var miAbout = new ToolStripMenuItem("About SONO Mixer");
        miAbout.Click += (_, _) => new AboutDialog().ShowDialog(this);
        settingsMenu.Items.AddRange(new ToolStripItem[] { _miAutostart, _miMinimized, miStep, miOsd, miTheme, miAbout });
        settingsBtn.Click += (_, _) => settingsMenu.Show(settingsBtn, new Point(0, settingsBtn.Height));

        // ---- OUTPUT: field-styled button opening a fully themed dropdown (no native combo popup) ----
        var outCaption = new Label
        {
            Text = "OUTPUT  (mixed channels play here)",
            ForeColor = Theme.Muted,
            Location = new Point(12, 52),
            Size = new Size(288, 16),
            Font = new Font("Segoe UI", 7f, FontStyle.Bold),
        };
        _outputMenu = new ToolStripDropDown();
        MenuTheme.Stylize(_outputMenu);
        _outputBtn = new Button
        {
            Location = new Point(10, 70),
            Size = new Size(292, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Font = new Font("Segoe UI", 10f),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0),
        };
        _outputBtn.FlatAppearance.BorderSize = 0;
        _outputBtn.Click += (_, _) => ShowOutputMenu();

        // load theme mascot (e.g. Miku) early: its presence insets the app list so a visible
        // parent strip exists at the bottom for the owner-drawn image
        _mascot = null;
        if (Theme.Current.ImagePath is string mimg)
        {
            var mfull = Path.Combine(AppContext.BaseDirectory, mimg);
            if (File.Exists(mfull)) _mascot = Image.FromFile(mfull);
        }
        int mascotInset = 130;   // reserved bottom band in ALL themes (mascot paints empty when absent)

        _appList = new SmoothFlowPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.CardInner,
            Location = new Point(10, 106),
            Size = new Size(292, 490 - mascotInset),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Padding = new Padding(6),
        };
        // keep the reserved bottom band visible at ANY window height: clamp the list bottom
        // on every layout pass (anchors would otherwise stretch it over the band).
        // Compact mode (window ≤ half screen height): mascot at ¼ size, band shrinks.
        {
            void ClampList()
            {
                bool compact = Height <= Screen.PrimaryScreen!.Bounds.Height / 2;
                foreach (var c in _cards.Values) c.SetCompact(compact);
                int mascotH = compact ? 34 : 130;
                int maxBottom = right.Height - 10 - mascotH - 4;   // grid margin + mascot band + gap
                if (_appList.Bottom > maxBottom) _appList.Height = maxBottom - _appList.Top;
            }
            right.Resize += (_, _) => ClampList();
            _appList.Resize += (_, _) => ClampList();
        }

        _status = new Label
        {
            ForeColor = Theme.Muted,
            Location = new Point(12, 604),
            Size = new Size(288, 34),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };

        right.Controls.Add(rightHead);
        right.Controls.Add(rightSub);
        right.Controls.Add(settingsBtn);
        right.Controls.Add(outCaption);
        right.Controls.Add(_outputBtn);
        right.Controls.Add(_appList);
        right.Controls.Add(_status);

        // theme mascot: owner-drawn in the reserved strip between the app list and the status
        // line → real alpha, bottom-center, SCALED TO FIT the band (aspect preserved).
        // Bottom margin matches the card grid (10px).
        if (_mascot is not null)
        {
            right.Paint += (_, e) =>
            {
                if (_mascot is null) return;
                bool compact = right.Height < 430;
                int bandH = compact ? 34 : 130;
                int availW = right.Width - 20;                       // 10px side margins
                float scale = Math.Min(availW / (float)_mascot.Width, bandH / (float)_mascot.Height);
                int w = Math.Max(1, (int)(_mascot.Width * scale));
                int h = Math.Max(1, (int)(_mascot.Height * scale));
                int x = (right.Width - w) / 2;
                int y = right.Height - h - 10;                       // same bottom margin as the cards
                e.Graphics.DrawImage(_mascot, x, y, w, h);
            };
            right.Resize += (_, _) => right.Invalidate();
        }
        Controls.Add(right);
        _table.BringToFront();   // Fill control must be laid out LAST so the Right panel reserves its strip

        // ---- tray ----
        _tray = new NotifyIcon
        {
            Icon = Icon,
            Text = "SONO — Audio Mixer",
            Visible = true,
        };
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open SONO", null, (_, _) => ShowWindow());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Mute all channels", null, (_, _) => SetAllMuted(true));
        trayMenu.Items.Add("Unmute all channels", null, (_, _) => SetAllMuted(false));
        trayMenu.Items.Add(new ToolStripSeparator());

        // quick output switch (AudioSwitch-style): lists every non-cable render device,
        // checkmark on the current Windows default, click switches the system default.
        // Rebuilt every time the menu opens so new/removed devices show up.
        var outputItem = new ToolStripMenuItem("Output");
        outputItem.DropDownItems.Add(new ToolStripMenuItem("…"));
        outputItem.DropDownOpening += (_, _) => RebuildTrayOutputMenu(outputItem);
        trayMenu.Items.Add(outputItem);

        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => CloseReally());
        _tray.ContextMenuStrip = trayMenu;
        // coming back to SONO = user wants current truth: refresh sessions immediately
        // (the 600 ms tick alone makes the list feel stale right after focusing)
        Activated += (_, _) => _engine.ReconcileNow();
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.MouseClick += (_, e) =>
        {
            // left-click opens the mixer (same as double-click, but snappier)
            if (e.Button == MouseButtons.Left) ShowWindow();
        };

        _reconcileDebounce = new System.Windows.Forms.Timer { Interval = 150 };
        _reconcileDebounce.Tick += (_, _) =>
        {
            _reconcileDebounce.Stop();
            _engine.ReconcileNow();
        };

        Load += (_, _) =>
        {
            // ONE MODE: group volumes via Windows session APIs; apps play straight to
            // the Windows default output. No driver, no setup.
            bool dirty = false;
            lock (_settings)
                foreach (var c in _settings.Channels)
                    if (c.DeviceId is not null) { c.DeviceId = null; dirty = true; }   // migrate from old VAC mode
            if (dirty) Save();
            BuildCards();
            ReflowGrid();
            IReadOnlyList<ChannelDefinition> Snapshot() { lock (_settings) return _settings.Channels.ToList(); }
            _engine.SetChannelSource(Snapshot);   // group ownership: claimed apps follow their group's volume
            _engine.Start(600);
            RebindHotkeys();
            UpdateOutputButtonLabel();
            if (_launchedAtBoot && _settings.StartMinimized) HideToTray(showBalloon: true);
        };

        FormClosing += (_, e) =>
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                HideToTray(showBalloon: !_balloonShown);
                _balloonShown = true;
            }
        };

        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(showBalloon: !_balloonShown); };
    }

    // ---------- visibility / tray ----------

    private Rectangle _lastGoodBounds = Rectangle.Empty;

    private void ShowWindow()
    {
        // restore robustly: tray restore used to leave the window on the taskbar but
        // not visible on screen (window managers / minimized-state leftovers)
        if (!Visible) Show();
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;

        if (_lastGoodBounds is { Width: > 0, Height: > 0 } b)
            Bounds = b;
        else
            CenterToScreen();

        // clamp back on-screen in case the saved spot belongs to a disconnected monitor
        var screen = Screen.FromControl(this).Bounds;
        if (!screen.IntersectsWith(Bounds))
            Location = new Point(
                Math.Max(screen.Left, screen.Left + (screen.Width - Width) / 2),
                Math.Max(screen.Top, screen.Top + (screen.Height - Height) / 2));

        // Force a FULL synchronous repaint of the whole tree. After Hide()/Show() the
        // window manager (komorebi) re-tiles the window around our layout pass, leaving
        // children with stale/blank surfaces (the "broken layout" screenshots). Refresh()
        // = Invalidate + synchronous Update for form and every child — one-shot, cheap.
        PerformLayout();
        Refresh();

        BeginInvoke(() => Refresh());   // once more after the WM settles the final bounds

        BringToFront();
        Activate();
    }

    private void HideToTray(bool showBalloon)
    {
        // remember where to restore to (only meaningful in Normal state)
        if (WindowState == FormWindowState.Normal && Visible)
            _lastGoodBounds = Bounds;
        Hide();
        if (showBalloon)
            _tray.ShowBalloonTip(2500, "SONO is still running",
                "Audio channels stay active. Double-click the tray icon to open the mixer.", ToolTipIcon.Info);
    }

    private void CloseReally()
    {
        _allowClose = true;
        Close();
    }

    private static Icon MakeIcon()
    {
        // use the app's embedded multi-size icon (pink audio cable) — the exe carries
        // it via <ApplicationIcon>; extract the size Windows needs, no runtime drawing
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath)
                   ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    // ---------- cards ----------

    private void BuildCards()
    {
        lock (_settings)
            foreach (var def in _settings.Channels.ToList())
                CreateCard(def);
    }

    private void CreateCard(ChannelDefinition def)
    {
        var card = new ChannelCard(def, _hotkeys) { Dock = DockStyle.Fill, Margin = new Padding(8) };
        card.VolumeLive += (_, _) => { _reconcileDebounce.Start(); };
        card.VolumeCommitted += _ => { Save(); _engine.ReconcileNow(); };
        card.MuteToggled += _ => { Save(); _engine.ReconcileNow();  };
        card.ExeDropped += (exe, chId) => AssignExe(exe, chId);
        card.ExeRemoved += exe => { RemoveExeEverywhere(exe); Save(); _engine.ReconcileNow(); RefreshRows(_last!); };
        card.HotkeySet += (_, _, _) => { RebindHotkeys(); Save(); };
        card.DefinitionEdited += _ => Save();
        card.AddAppRequested += id => ShowAddAppDialog(id);
        _cards[def.Id] = card;
        _table.Controls.Add(card);
    }

    private void AssignExe(string exe, string channelId)
    {
        lock (_settings)
        {
            foreach (var c in _settings.Channels) c.Executables.RemoveAll(x => x.Equals(exe, StringComparison.OrdinalIgnoreCase));
            var target = _settings.Channels.FirstOrDefault(c => c.Id == channelId);
            if (target is not null && !target.Executables.Contains(exe, StringComparer.OrdinalIgnoreCase))
                target.Executables.Add(exe);
            if (!_settings.KnownApps.Contains(exe, StringComparer.OrdinalIgnoreCase)) _settings.KnownApps.Add(exe);
        }
        Save();
        _engine.ReconcileNow();
        if (_last is not null) { RefreshRows(_last); UpdateCards(_last); }

    }


    private void RemoveExeEverywhere(string exe)
    {
        lock (_settings)
            foreach (var c in _settings.Channels)
                c.Executables.RemoveAll(x => x.Equals(exe, StringComparison.OrdinalIgnoreCase));
    }

    private void ShowAddAppDialog(string channelId)
    {
        var owned = _settings.Channels.FirstOrDefault(c => c.Id == channelId)?.Executables
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new();
        var live = _last?.Sessions.Select(s => s.Exe).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new();
        var choices = live.Concat(_settings.KnownApps)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(x => !owned.Contains(x))
            .OrderBy(x => x)
            .ToList();
        if (choices.Count == 0)
        {
            MessageBox.Show(this, "No other applications seen yet. Start an app (or play a sound) and it will appear in the right panel — then drag it here.",
                "Nothing to add", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var pick = new Form
        {
            Text = "Add application",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(340, 110),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ShowIcon = false,
            BackColor = Theme.Card,
        };
        TitleBarTheme.Apply(pick);
        var combo = new DarkComboBox { Left = 14, Top = 16, Width = 312 };
        combo.Items.AddRange(choices.ToArray());
        combo.SelectedIndex = 0;
        var ok = new Button
        {
            Text = "Add", Left = 253, Top = 62, Width = 73, Height = 28,
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Theme.Bg,
            Cursor = Cursors.Hand,
        };
        ok.FlatAppearance.BorderSize = 0;
        pick.Controls.Add(combo);
        pick.Controls.Add(ok);
        pick.AcceptButton = ok;
        pick.CancelButton = ok;
        if (pick.ShowDialog(this) == DialogResult.OK && combo.SelectedItem is string chosen)
            AssignExe(chosen, channelId);
    }

    // channels are fixed 4-up columns, so a card "drop" is a no-op now
    private void FlowCardDrop(object? sender, DragEventArgs e) { }
    // ---------- right panel rows ----------

    private void RefreshRows(EngineSnapshot snap)
    {
        var ownedExes = snap.Sessions.Where(s => s.Owned).Select(s => s.Exe).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // live = apps with an audio session right now (active or inactive), NOT every app
        // ever seen — the list must mirror reality; KnownApps only feeds the "+ add" picker
        var live = snap.Sessions.Where(s => s.Exe != "unknown").Select(s => s.Exe).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // exe → owning channel's color (for tinting the per-app sliders)
        var exeColor = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        lock (_settings)
            foreach (var ch in _settings.Channels)
                foreach (var exe in ch.Executables)
                    exeColor[exe] = Theme.FromHex(ch.ColorHex);

        for (int i = _appList.Controls.Count - 1; i >= 0; i--)
            if (_appList.Controls[i] is Panel p && p.Tag is string exe && !live.Contains(exe))
            { _appRows.Remove(exe); _appList.Controls.RemoveAt(i); }

        foreach (var exe in live)
        {
            if (!_appRows.TryGetValue(exe, out var row))
            {
                row = MakeAppRow(exe);
                _appRows[exe] = row;
                _appList.Controls.Add(row);
            }
            row.Enabled = true;
            if (row.Controls[0] is Label chip)
                chip.ForeColor = ownedExes.Contains(exe) ? Theme.Muted : Theme.Text;
            if (row.Controls[1] is SliderBar s)
            {
                // informative-only: always mirror the real session volume (no enable
                // toggling — the readout reflects every app, owned or not)
                s.Fill = exeColor.TryGetValue(exe, out var c) ? c : Theme.Accent;
                var sess = snap.Sessions.FirstOrDefault(x => x.Exe == exe);
                if (sess is not null) s.SetValueExternal(sess.Volume);
            }
        }
    }

    private Panel MakeAppRow(string exe)
    {
        var row = new Panel { Height = 36, Width = 272, BackColor = Color.Transparent, Tag = exe, Margin = new Padding(2, 1, 2, 1) };
        var chip = Chips.Make(exe);
        chip.Size = new Size(112, 28);
        chip.AutoSize = false;
        chip.AutoEllipsis = true;
        chip.TextAlign = ContentAlignment.MiddleLeft;
        row.Controls.Add(chip);

        // informative-only volume readout: the slider MIRRORS the app's session volume.
        // Changing app volume belongs to the owning channel (or Windows Volume mixer for
        // unowned apps) — an interactive slider here fought the engine every tick and
        // needed full session reconciles per drag frame (the lag report).
        var slider = new SliderBar { Location = new Point(116, 2), Size = new Size(122, 30), Anchor = AnchorStyles.Left, ReadOnly = true };
        slider.Fill = Theme.Accent;
        slider.SetValueExternal(1f);
        row.Controls.Add(slider);

        var mute = new Button
        {
            Text = "🔈",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Text,
            BackColor = Theme.Chip,
            Size = new Size(30, 28),
            Location = new Point(242, 3),
            Cursor = Cursors.Hand,
        };
        mute.FlatAppearance.BorderSize = 0;
        bool muted = false;
        mute.Click += (_, _) =>
        {
            muted = !muted;
            mute.ForeColor = muted ? Theme.Danger : Theme.Text;
            _engine.SetIndependentVolume(exe, null, muted);
        };
        row.Controls.Add(mute);
        return row;
    }

    // ---------- engine ticks ----------

    private EngineSnapshot? _last;

    private void OnTick(EngineSnapshot snap)
    {
        _last = snap;
        UpdateCards(snap);
        RefreshRows(snap);
        RememberKnownApps(snap);
        _status.Text = snap.Error is not null
            ? $"⚠ {snap.Error}"
            : _hotkeyError is not ""
                ? $"⚠ Hotkey: {_hotkeyError}"
                : $"▶ {snap.OutputDevice}";
    }

    private void UpdateCards(EngineSnapshot snap)
    {
        foreach (var (id, card) in _cards)
        {
            var owned = snap.Sessions.Where(s => s.ChannelId == id).ToList();
            card.Update(owned, _hotkeyError);
        }
    }

    private readonly Dictionary<string, string> _deviceNames = new();
    private string DeviceName(string? id)
    {
        if (id is null) return "";
        if (_deviceNames.TryGetValue(id, out var n)) return n;
        try
        {
            var d = new MMDeviceEnumerator().GetDevice(id);
            _deviceNames[id] = d.FriendlyName;
            return d.FriendlyName;
        }
        catch { return "?"; }
    }

    /// <summary>One-elevation setup: driver install (if needed), cables=4, endpoint renames,
    /// device bounce. Shows a progress dialog throughout; reports per-step results at the end.</summary>

    /// <summary>Wide window → 4 columns; narrow/square → 2×2, so cards never get cramped.</summary>
    private void ReflowGrid()
    {
        if (_table.Width <= 0) return;
        bool wide = _table.ClientSize.Width > 2 * (_table.ClientSize.Height * 4 / 3);
        // wide: 4 cols × 1 row. narrow: 2 cols × 2 rows.
        int wantCols = wide ? 4 : 2;
        if (_table.ColumnCount == wantCols) return;

        _table.SuspendLayout();
        _table.ColumnCount = wantCols;
        _table.RowCount = wide ? 1 : 2;
        _table.ColumnStyles.Clear();
        for (int i = 0; i < wantCols; i++) _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / wantCols));
        _table.RowStyles.Clear();
        _table.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        if (!wide) _table.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _table.ResumeLayout();
    }

    /// <summary>Build the themed output dropdown: all render devices except the virtual cables.</summary>
    private void ShowOutputMenu()
    {
        var en = new MMDeviceEnumerator();
        var devices = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Where(d => !d.FriendlyName.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase))
            .ToList();
        string defaultId = "";
        try { defaultId = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID; } catch { }
        string current = _settings.RealOutputId is string r && devices.Any(d => d.ID == r) ? r : defaultId;

        _outputMenu.Items.Clear();
        _outputIds.Clear();
        foreach (var d in devices)
        {
            _outputIds.Add(d.ID);
            bool isDefault = d.ID == defaultId;
            bool isCurrent = d.ID == current;
            var item = new ToolStripMenuItem(isDefault ? d.FriendlyName + "   ◈ default" : d.FriendlyName)
            {
                Font = new Font("Segoe UI", 10f, isCurrent ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = isCurrent ? Theme.Accent : Theme.Text,
            };
            var captured = d.ID;
            item.Click += (_, _) =>
            {
                // AudioSwitch semantics: picking an output here switches the WINDOWS
                // DEFAULT output — every app on "default" follows system-wide — and
                // SONO's mixer follows it too. (Previously this only moved SONO's
                // private mix, so the switch looked like a no-op.)
                try
                {
                    SONO.Core.Audio.PolicyConfigApi.SetDefaultDeviceAllRoles(captured);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Could not switch default output: {ex.Message}", "SONO");
                }
                _settings.RealOutputId = captured;
                Save();
                UpdateOutputButtonLabel(captured);
            };
            _outputMenu.Items.Add(item);
        }
        _outputMenu.Show(_outputBtn, new Point(0, _outputBtn.Height));
        UpdateOutputButtonLabel(current);
    }

    /// <summary>Fill the tray's Output submenu: every non-cable render device, checkmark on
    /// the current Windows default; clicking switches the system default (AudioSwitch-style).
    /// Runs on each menu open so the device list is always fresh.</summary>
    private void RebuildTrayOutputMenu(ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        var en = new MMDeviceEnumerator();
        string defaultId = "";
        try { defaultId = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID; } catch { }

        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                     .Where(d => !d.FriendlyName.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase)))
        {
            bool isDefault = d.ID == defaultId;
            var item = new ToolStripMenuItem(d.FriendlyName)
            {
                Checked = isDefault,
                CheckOnClick = false,
                Font = new Font("Segoe UI", 10f, isDefault ? FontStyle.Bold : FontStyle.Regular),
            };
            var captured = d.ID;
            item.Click += (_, _) =>
            {
                try { SONO.Core.Audio.PolicyConfigApi.SetDefaultDeviceAllRoles(captured); }
                catch (Exception ex) { MessageBox.Show(this, $"Could not switch default output: {ex.Message}", "SONO"); return; }
                lock (_settings) _settings.RealOutputId = captured;
                Save();
                UpdateOutputButtonLabel(captured);
            };
            parent.DropDownItems.Add(item);
        }
    }

    private void UpdateOutputButtonLabel(string? forced = null)
    {
        string id = forced ?? _settings.RealOutputId ?? "";
        string name = "?";
        try { name = new MMDeviceEnumerator().GetDevice(id).FriendlyName; }
        catch
        {
            try { name = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).FriendlyName; } catch { }
        }
        _outputBtn.Text = "▾  " + name;
    }



    private void RememberKnownApps(EngineSnapshot snap)
    {
        bool dirty = false;
        lock (_settings)
            foreach (var exe in snap.Sessions.Select(s => s.Exe))
                if (exe != "unknown" && !_settings.KnownApps.Contains(exe, StringComparer.OrdinalIgnoreCase))
                { _settings.KnownApps.Add(exe); dirty = true; }
        if (dirty) Save();
    }

    // ---------- hotkeys ----------

    private void RebindHotkeys()
    {
        var bindings = new List<(string, HotkeySlot, string?)>();
        lock (_settings)
            foreach (var c in _settings.Channels)
                foreach (HotkeySlot slot in Enum.GetValues<HotkeySlot>())
                    bindings.Add((c.Id, slot, c.GetHotkey(slot)));
        var errors = _hotkeys.ApplyAll(bindings);
        _hotkeyError = errors.Count > 0 ? errors[0] : "";
        if (_hotkeyError is not "") _status.Text = $"⚠ Hotkey: {_hotkeyError}";
    }

    private void OnHotkey(string channelId, HotkeySlot slot)
    {
        var def = _settings.Channels.FirstOrDefault(c => c.Id == channelId);
        if (def is null) return;
        float step = _settings.HotkeyStep;
        switch (slot)
        {
            case HotkeySlot.VolDown: def.Volume = MathF.Max(0f, def.Volume - step); break;
            case HotkeySlot.VolUp: def.Volume = MathF.Min(1f, def.Volume + step); break;
            case HotkeySlot.Mute: def.Muted = !def.Muted; break;
        }
        Save();
        _engine.ReconcileNow();
        
        if (_last is not null) UpdateCards(_last);
        if (_settings.OsdAnchor != "off") _osd.Notify(def);
    }

    // ---------- misc ----------

    private void SetAllMuted(bool muted)
    {
        lock (_settings)
            foreach (var c in _settings.Channels.Where(c => c.Kind == ChannelKind.Group))
                c.Muted = muted;
        Save();
        _engine.ReconcileNow();
        
        if (_last is not null) UpdateCards(_last);
    }

    private void ApplyAutostart()
    {
        try { Core.SystemIntegrations.Autostart.Set(_settings.StartWithWindows); }
        catch (Exception ex) { MessageBox.Show(this, $"Could not update autostart: {ex.Message}", "SONO"); }
    }

    private void Save()
    {
        lock (_settings) SONO.Core.Settings.SettingsStore.Save(_settings);
    }

    /// <summary>Close without the tray-hide intercept, keeping shared services alive (theme swap).</summary>
    public void ForceClose()
    {
        _allowClose = true;
        SuppressCleanup = true;
        Close();
    }

    private void EngineTickHandler(EngineSnapshot snap)
    {
        // ticks arrive on a timer thread; if the handle is gone (mid theme-swap) just drop the frame
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(() => OnTick(snap)); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _engine.Tick += EngineTickHandler;

        // permanent form-level double buffering: safe (no handle recreation), covers
        // layout-time repaint flashes that per-control OptimizedDoubleBuffer misses
        typeof(Control).GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(this, true);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _engine.Tick -= EngineTickHandler;
        _hotkeys.Pressed -= OnHotkey;
        _tray.Visible = false;
        _tray.Dispose();
        _reconcileDebounce.Dispose();
        base.OnFormClosed(e);
    }
}

/// <summary>FlowLayoutPanel with double buffering (kills flicker while dragging/resizing).</summary>
public class BufferedFlow : FlowLayoutPanel
{
    public BufferedFlow() => DoubleBuffered = true;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width <= 1 || Height <= 1) return;
        Region = new Region(SliderBar.RoundRect(0, 0, Width, Height, 14));
    }
}
