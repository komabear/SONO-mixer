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
    private readonly RoutingHealthMonitor _health = new();
    private readonly OsdWindow _osd = new();
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _reconcileDebounce;
    private readonly Dictionary<string, ChannelCard> _cards = new();
    private readonly Dictionary<string, Panel> _appRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolStripMenuItem _miAutostart, _miMinimized;
    private bool _setupRunning;
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

    private readonly RoutingEngine _routing;                  // shared ApplicationContext-wide
                                                              // (theme swaps must NOT re-create it)
                                                              // initialized from ctor param below
    public MixerForm(AudioEngine engine, HotkeyManager hotkeys, RoutingEngine routing,
        AppSettings settings, bool launchedAtBoot)
    {
        _engine = engine;
        _hotkeys = hotkeys;
        _routing = routing;
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
        var miSetup = new ToolStripMenuItem("Run automatic device setup…");
        miSetup.Click += (_, _) => RunDeviceSetupWizard();
        var miMode = new ToolStripMenuItem("Mode");
        var miModeSimple = new ToolStripMenuItem("Simple — group volumes, no driver");
        var miModeFull = new ToolStripMenuItem("Full — virtual devices (needs VAC)");
        void SyncModeChecks()
        {
            lock (_settings)
            {
                miModeSimple.Checked = _settings.Mode == "simple";
                miModeFull.Checked = _settings.Mode != "simple";
            }
        }
        miModeSimple.Click += (_, _) =>
        {
            lock (_settings) _settings.Mode = "simple";
            Save(); SyncModeChecks();
            MessageBox.Show(this,
                "Simple mode will be active after SONO restarts.\n\n" +
                "Groups control their apps' volumes directly (Windows session volumes) — no driver needed. " +
                "Apps play straight to your normal output; the OUTPUT picker switches the Windows default.",
                "SONO Mixer — mode", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        miModeFull.Click += (_, _) =>
        {
            lock (_settings) _settings.Mode = "full";
            Save(); SyncModeChecks();
            var have = Core.SystemIntegrations.DeviceSetup.SonoEndpointsPresent();
            MessageBox.Show(this,
                (have
                    ? "Full mode will be active after SONO restarts."
                    : "Full mode will be active after SONO restarts.\n\nNo \"SONO - …\" devices found yet — the device setup wizard will run next launch (or ⚙ → Run automatic device setup…)."),
                "SONO Mixer — mode", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        miMode.DropDownItems.Add(miModeSimple);
        miMode.DropDownItems.Add(miModeFull);
        SyncModeChecks();

        var miAbout = new ToolStripMenuItem("About SONO Mixer");
        miAbout.Click += (_, _) => new AboutDialog().ShowDialog(this);
        settingsMenu.Items.AddRange(new ToolStripItem[] { _miAutostart, _miMinimized, miStep, miOsd, miMode, miTheme, miSetup, miAbout });
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
        _status.Click += (_, _) =>
        {
            if (_misroutes.Count > 0)
            {
                var lines = _misroutes.Take(6).Select(m => $"• {m.Exe} → should play on \"SONO - {m.ChannelName}\" (now on {m.ActualDevice})");
                var ask = MessageBox.Show(this,
                    "These apps are not playing into their channel's virtual device, so their channel slider cannot control them:\n\n"
                    + string.Join("\n", lines)
                    + "\n\nOpen Windows Volume mixer now so you can set each app's Output to its channel's \"SONO - …\" device?\n"
                    + "(This is a one-time Windows setting per app — Windows provides no API to set it programmatically.)",
                    "Mis-routed apps", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (ask == DialogResult.Yes)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:appsvolume") { UseShellExecute = true });
            }
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
        // single left-click right after a mis-route BALLOON (not any later click) → open the fix.
        // The flag expires: a stale click must just open the window like a normal tray click.
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_misroutes.Count > 0)
            {
                _misrouteBalloonPending = false;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:appsvolume") { UseShellExecute = true });
            }
        };
        _tray.MouseClick += (_, e) =>
        {
            // left-click within the balloon's grace window follows the balloon's action;
            // otherwise a left-click behaves like double-click (opens the mixer)
            if (e.Button == MouseButtons.Left)
            {
                if (_misrouteBalloonPending && _misroutes.Count > 0)
                {
                    _misrouteBalloonPending = false;
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:appsvolume") { UseShellExecute = true });
                }
                else ShowWindow();
            }
        };

        _reconcileDebounce = new System.Windows.Forms.Timer { Interval = 150 };
        _reconcileDebounce.Tick += (_, _) =>
        {
            _reconcileDebounce.Stop();
            _engine.ReconcileNow();
        };

        Load += (_, _) =>
        {
            // resolve mode: explicit setting wins; "" auto-detects (SONO devices → Full, else Simple)
            bool simple;
            lock (_settings)
            {
                if (_settings.Mode == "")
                    _settings.Mode = Core.SystemIntegrations.DeviceSetup.SonoEndpointsPresent() ? "full" : "simple";
                simple = _settings.Mode == "simple";
            }
            BuildCards();
            ReflowGrid();
            IReadOnlyList<ChannelDefinition> Snapshot() { lock (_settings) return _settings.Channels.ToList(); }
            _engine.SetChannelSource(Snapshot);   // session-ownership keeps unclaimed apps at unity
            _engine.Start(600);
            _routing.SetChannelSource(Snapshot);
            RebindHotkeys();
            // sanitize: real output must never be one of the channel cables (feedback loop)
            if (_settings.RealOutputId is string ro && _settings.Channels.Any(c => c.DeviceId == ro))
            { _settings.RealOutputId = null; Save(); }
            if (simple)
            {
                // SIMPLE MODE: no virtual devices. Group volumes run through session APIs;
                // apps play straight to the Windows default. Clear any stale device bindings.
                bool dirty = false;
                lock (_settings)
                    foreach (var c in _settings.Channels)
                        if (c.DeviceId is not null) { c.DeviceId = null; dirty = true; }
                if (dirty) Save();
            }
            else ResolveDeviceMappings();
            UpdateOutputButtonLabel();
            _routing.Rebuild(_settings.RealOutputId);
            RefreshRoutingVolumes();
            // first-run wizard: FULL MODE only. Simple mode needs no setup at all.
            if (!simple && !Core.SystemIntegrations.DeviceSetup.SonoEndpointsPresent())
            {
                BeginInvoke(() =>
                {
                    if (MessageBox.Show(this,
                            "Welcome to SONO Mixer!\n\nNo \"SONO - …\" audio devices were found. Run the automatic setup now?\n\n" +
                            "It will configure the Virtual Audio Cable driver (4 cables), name the devices\n" +
                            "SONO - Game / Chat / Media / Aux, and activate them. Requires administrator rights.",
                            "SONO Mixer — first run", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        RunDeviceSetupWizard();
                });
            }
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
        card.VolumeLive += (_, _) => { RefreshRoutingVolumes(); _reconcileDebounce.Start(); };
        card.VolumeCommitted += _ => { Save(); _engine.ReconcileNow(); };
        card.MuteToggled += _ => { Save(); _engine.ReconcileNow(); RefreshRoutingVolumes(); };
        card.ExeDropped += (exe, chId) => AssignExe(exe, chId);
        card.ExeRemoved += exe => { RemoveExeEverywhere(exe); Save(); _engine.ReconcileNow(); RefreshRows(_last!); };
        card.HotkeySet += (_, _, _) => { RebindHotkeys(); Save(); };
        card.DefinitionEdited += _ => Save();
        card.AddAppRequested += id => ShowAddAppDialog(id);
        card.MakeDefaultRequested += id =>
        {
            var d = _settings.Channels.FirstOrDefault(c => c.Id == id);
            if (d?.DeviceId is string did)
                try { SONO.Core.Audio.PolicyConfigApi.SetDefaultDevice(did); }
                catch (Exception ex) { MessageBox.Show(this, $"Could not set default device: {ex.Message}", "SONO"); }
        };
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

        // AUTOMATIC ROUTING (Full mode only): write Windows' per-app endpoint store so the
        // app's audio lands on this channel's device from its next session (restart).
        // Simple mode has no channel devices — group volumes run via session APIs instead.
        if (_settings.Mode != "simple")
            RouteExeToChannel(exe, channelId);
    }

    /// <summary>Point an exe's Windows per-app output at its channel's device via the
    /// DefaultEndpoint store (undocumented but stable; verified empirically). Runs on a
    /// worker thread — registry + process queries must not block the UI.</summary>
    private void RouteExeToChannel(string exe, string channelId)
    {
        Task.Run(() =>
        {
            try
            {
                string? deviceId = null;
                lock (_settings)
                    deviceId = _settings.Channels.FirstOrDefault(c => c.Id == channelId)?.DeviceId;
                if (deviceId is null) return;

                // find a live process of this exe ( SONO only sees running apps in the list;
                //  offline apps get routed when they next appear via the reconcile path )
                var procs = System.Diagnostics.Process.GetProcessesByName(exe);
                var anyPid = procs.FirstOrDefault(p => p is not null)?.Id;
                if (procs is { Length: > 0 })
                    foreach (var p in procs) p?.Dispose();

                if (anyPid is int pid &&
                    Core.SystemIntegrations.AppEndpointStore.SetAppEndpoint(pid, deviceId))
                {
                    Core.Diagnostics.Log.Write($"routed {exe} -> channel device {deviceId} (takes effect on app restart)");
                }
                else
                {
                    Core.Diagnostics.Log.Write($"could not route {exe} (no live process or store write failed) — Volume mixer remains the fallback");
                }
            }
            catch (Exception ex) { Core.Diagnostics.Log.Write($"RouteExeToChannel failed: {ex.Message}"); }
        });
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

    private List<RoutingHealthMonitor.MisRoute> _misroutes = new();
    private int _healthCooldown;
    private bool _misrouteBalloonPending;   // balloon shown → next tray click opens Volume mixer

    private void OnTick(EngineSnapshot snap)
    {
        _last = snap;
        // health scan every ~5s (6 ticks × 600ms), off the hot path
        if (++_healthCooldown >= 6)
        {
            _healthCooldown = 0;
            Task.Run(() =>
            {
                var found = _health.Scan(SnapshotChannels());
                BeginInvoke(() =>
                {
                    var before = _misroutes.Count;
                    _misroutes = found;
                    if (_misroutes.Count != before && _status is not null)
                        _status.Text = $"▶ {snap.OutputDevice}{RoutingSuffix()}";
                    // new mis-route (count grew) → tray balloon, since the status bar is
                    // easy to miss and a mis-routed app bypasses its channel entirely
                    if (_misroutes.Count > before)
                    {
                        var first = _misroutes.FirstOrDefault();
                        _tray.ShowBalloonTip(4000, "SONO — app mis-routed",
                            first is null ? "" :
                            $"{first.Exe} is playing on \"{first.ActualDevice}\" instead of \"SONO - {first.ChannelName}\"." +
                            (_misroutes.Count > 1 ? $" (+{_misroutes.Count - 1} more)" : "") +
                            "\nClick to open Volume mixer and fix it.", ToolTipIcon.Warning);
                        _misrouteBalloonPending = true;
                        // the balloon is transient: its click-action must expire with it
                        Task.Delay(6000).ContinueWith(_ => _misrouteBalloonPending = false);
                    }
                });
            });
        }
        UpdateCards(snap);
        RefreshRows(snap);
        RememberKnownApps(snap);
        _status.Text = snap.Error is not null
            ? $"⚠ {snap.Error}"
            : _hotkeyError is not ""
                ? $"⚠ Hotkey: {_hotkeyError}"
                : $"▶ {snap.OutputDevice}{RoutingSuffix()}";
    }

    private IReadOnlyList<ChannelDefinition> SnapshotChannels()
    { lock (_settings) return _settings.Channels.ToList(); }

    private string RoutingSuffix() => _routing.Error is not null
        ? $"  |  ⚠ routing: {_routing.Error}"
        : _misroutes.Count > 0
            ? $"  |  ⚠ {_misroutes.Count} app(s) mis-routed — click here to fix"
            : _routing.ActiveStreams > 0
                ? $"  |  routing {_routing.ActiveStreams} ch"
                : "";

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
    private async void RunDeviceSetupWizard()
    {
        if (_setupRunning) return;          // no double-run
        _setupRunning = true;
        try { await RunDeviceSetupWizardCore(); }
        finally { _setupRunning = false; }
    }

    private async System.Threading.Tasks.Task RunDeviceSetupWizardCore()
    {
        var endpoints = Core.SystemIntegrations.DeviceSetup.VacRenderEndpoints();
        string? driverLog = null;

        using var progress = new SetupProgressDialog { StartPosition = FormStartPosition.CenterParent };

        // ---- Phase 0: no VAC devices at all → install the driver from the user's own package
        if (endpoints.Count == 0 && !Core.SystemIntegrations.DeviceSetup.VacServicePresent)
        {
            using var pick = new SetupFolderDialog();
            if (pick.ShowDialog(this) != DialogResult.OK) return;

            progress.SetStatus("Installing the Virtual Audio Cable driver…\nApprove the administrator prompt.");
            progress.Show(this);
            var step = new Progress<string>(s => progress.SetStatus(s));
            driverLog = await Core.SystemIntegrations.DeviceSetup.RunElevatedAsync(
                Core.SystemIntegrations.DeviceSetup.BuildDriverInstallScript(pick.InfPath), step);

            progress.SetStatus("Waiting for the new audio devices to appear…");
            // wait for the driver to enumerate (pnputil + bounce can take a while)
            for (int wait = 0; wait < 15 && endpoints.Count == 0; wait++)
            {
                await Task.Delay(1000);
                endpoints = Core.SystemIntegrations.DeviceSetup.VacRenderEndpoints();
            }
        }

        endpoints = Core.SystemIntegrations.DeviceSetup.VacRenderEndpoints();
        if (endpoints.Count == 0)
        {
            progress.Close();
            MessageBox.Show(this,
                "No Virtual Audio Cable devices were found.\n\n" +
                (driverLog is null
                    ? "Install VAC 4.x first (see README), then run this again."
                    : "Driver install ran, but no devices appeared.\n\nSetup log:\n" + driverLog.Replace("\r\n", "\n")),
                "SONO Mixer — setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        progress.SetStatus($"Configuring {endpoints.Count} device(s) — naming channels SONO - Game / Chat / Media / Aux…\nApprove the administrator prompt.");
        if (!progress.Visible) progress.Show(this);
        var step2 = new Progress<string>(s => progress.SetStatus(s));

        if (endpoints.Count < 4)
            driverLog += $"\nOnly {endpoints.Count} VAC device(s) found — 4 expected. Cable count will be set to 4 by setup.";

        var script = Core.SystemIntegrations.DeviceSetup.BuildScript(endpoints);
        string log;
        try { log = await Core.SystemIntegrations.DeviceSetup.RunElevatedAsync(script, step2); }
        catch (Exception ex)
        {
            progress.Close();
            MessageBox.Show(this, $"Setup was cancelled or failed: {ex.Message}", "SONO Mixer — setup",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        progress.SetStatus("Almost done — restarting the devices and setting defaults…");
        // wait a moment for the device bounce to re-enumerate, then re-resolve
        await Task.Delay(4500);

        // fresh identities → re-resolve mappings, then make SONO - Game the Windows default
        ResolveDeviceMappings();
        _routing.Rebuild(_settings.RealOutputId);
        UpdateOutputButtonLabel();

        string defaultNote = "";
        // make SONO - Game the Windows default: resolve the device by name right here —
        // ResolveDeviceMappings may have just rewritten it, and endpoint IDs can be stale
        var game = _settings.Channels.FirstOrDefault(c => c.Name == "Game");
        string? gid = game?.DeviceId;
        if (string.IsNullOrEmpty(gid) || !gid.StartsWith("0.0.0.0"))
        {
            try
            {
                gid = new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .FirstOrDefault(d => d.FriendlyName.StartsWith("SONO - Game", StringComparison.OrdinalIgnoreCase))?.ID;
            }
            catch { }
        }
        if (string.IsNullOrEmpty(gid))
        {
            defaultNote = "\nCould not find \"SONO - Game\" to set as Windows default — set it manually in Sound settings.";
            Core.Diagnostics.Log.Write("setup: SONO - Game endpoint not found for default-set");
        }
        else
        {
            try
            {
                SONO.Core.Audio.PolicyConfigApi.SetDefaultDeviceAllRoles(gid);
                defaultNote = "\nWindows default output set to SONO - Game.";
                Core.Diagnostics.Log.Write($"setup: default output set to SONO - Game ({gid})");
            }
            catch (Exception ex)
            {
                defaultNote = $"\nCould not set SONO - Game as Windows default automatically ({ex.Message}).";
                Core.Diagnostics.Log.Write($"setup: SetDefaultEndpoint failed: {ex.Message}");
            }
        }

        progress.Close();
        var ok = log.Contains(": OK") || (driverLog is not null && driverLog.Contains("device node created"));
        MessageBox.Show(this,
            (ok ? "Device setup finished.\n\n" : "Setup finished with some errors.\n\n") +
            ((driverLog is not null ? "[driver install]\n" + driverLog.Replace("\r\n", "\n") + "\n\n" : "") +
            (log.Length > 0 ? "[device naming]\n" + log.Replace("\r\n", "\n") : "") +
            defaultNote),
            "SONO Mixer — setup", MessageBoxButtons.OK,
            ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

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
                _routing.Rebuild(_settings.RealOutputId);
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
                _routing.Rebuild(captured);
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

    private void RefreshRoutingVolumes()
    {
        lock (_settings) _routing.ApplyVolumes(_settings.Channels.ToList());
    }

    /// <summary>Channels bind to devices BY NAME: "Game" → endpoint starting with "SONO - Game".
    /// Survives driver reinstalls/re-enumeration, and renaming a channel re-binds it automatically.</summary>
    private void ResolveDeviceMappings()
    {
        var renders = new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        bool dirty = false;
        lock (_settings)
            foreach (var ch in _settings.Channels)
            {
                var want = "SONO - " + ch.Name;
                var match = renders.FirstOrDefault(d => d.FriendlyName.StartsWith(want, StringComparison.OrdinalIgnoreCase));
                if (match is not null && ch.DeviceId != match.ID) { ch.DeviceId = match.ID; dirty = true; }
            }
        if (dirty) Save();
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
        RefreshRoutingVolumes();
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
        RefreshRoutingVolumes();
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
        if (!SuppressCleanup) _routing.Dispose();   // shared engine; disposed only at real app exit
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
