using SONO.Core.Audio;
using SONO.Core.Settings;

namespace SONO.App;

/// <summary>Main window: channel tracks on the left, live app list on the right, tray icon in the corner.</summary>
public class MixerForm : Form
{
    private readonly AudioEngine _engine;
    private readonly HotkeyManager _hotkeys;
    private readonly AppSettings _settings;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _reconcileDebounce;
    private readonly Dictionary<string, ChannelCard> _cards = new();
    private readonly Dictionary<string, Panel> _appRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolStripMenuItem _miAutostart, _miMinimized;
    private readonly Label _status;
    private readonly FlowLayoutPanel _flow;
    private readonly FlowLayoutPanel _appList;
    private bool _allowClose;
    private bool _balloonShown;
    private string _hotkeyError = "";
    private readonly bool _launchedAtBoot;

    public MixerForm(AudioEngine engine, HotkeyManager hotkeys, AppSettings settings, bool launchedAtBoot)
    {
        _engine = engine;
        _hotkeys = hotkeys;
        _settings = settings;
        _launchedAtBoot = launchedAtBoot;

        Text = "SONO Mixer";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 660);
        MinimumSize = new Size(940, 560);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        KeyPreview = false;
        Icon = MakeIcon();

        // ---- left: channel tracks ----
        _flow = new BufferedFlow
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Padding = new Padding(10),
            AutoScroll = true,
            AllowDrop = true,
        };
        _flow.DragOver += (_, e) => { if (e.Data?.GetDataPresent("SONO_CARD") == true) e.Effect = DragDropEffects.Move; };
        _flow.DragDrop += FlowCardDrop;
        Controls.Add(_flow);

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
        settingsMenu.Items.AddRange(new ToolStripItem[] { _miAutostart, _miMinimized, miStep });
        settingsBtn.Click += (_, _) => settingsMenu.Show(settingsBtn, new Point(0, settingsBtn.Height));

        _appList = new BufferedFlow
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.CardInner,
            Location = new Point(10, 56),
            Size = new Size(292, 540),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Padding = new Padding(6),
        };

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
        right.Controls.Add(_appList);
        right.Controls.Add(_status);
        Controls.Add(right);
        right.BringToFront();

        var newBtn = new Button
        {
            Text = "+ New channel",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Text,
            BackColor = Theme.Card,
            Size = new Size(248, 34),
            Margin = new Padding(8),
            Cursor = Cursors.Hand,
        };
        newBtn.FlatAppearance.BorderColor = Theme.Border;
        newBtn.Click += (_, _) => AddChannel();
        _flow.Controls.Add(newBtn);

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
        trayMenu.Items.Add("Exit", null, (_, _) => CloseReally());
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => ShowWindow();

        _reconcileDebounce = new System.Windows.Forms.Timer { Interval = 150 };
        _reconcileDebounce.Tick += (_, _) =>
        {
            _reconcileDebounce.Stop();
            _engine.ReconcileNow();
        };

        Load += (_, _) =>
        {
            BuildCards();
            _engine.SetChannelSource(() => { lock (_settings) return _settings.Channels.ToList(); });
            _engine.Tick += snap => BeginInvoke(() => OnTick(snap));
            _engine.Start(600);
            RebindHotkeys();
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

    private void ShowWindow()
    {
        if (!Visible)
        {
            WindowState = FormWindowState.Normal;
            Show();
        }
        Activate();
    }

    private void HideToTray(bool showBalloon)
    {
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
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            int[] xs = { 6, 14, 22 };
            int[] hs = { 12, 20, 9 };
            for (int i = 0; i < 3; i++)
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, xs[i], 16 - hs[i] / 2, 4, hs[i]);
        }
        return Icon.FromHandle(bmp.GetHicon());
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
        var card = new ChannelCard(def) { Size = new Size(248, 430), Margin = new Padding(8) };
        card.VolumeLive += (_, _) => _reconcileDebounce.Start();
        card.VolumeCommitted += _ => { Save(); _engine.ReconcileNow(); };
        card.MuteToggled += _ => { Save(); _engine.ReconcileNow(); };
        card.ExeDropped += (exe, chId) => AssignExe(exe, chId);
        card.ExeRemoved += exe => { RemoveExeEverywhere(exe); Save(); _engine.ReconcileNow(); RefreshRows(_last!); };
        card.HotkeySet += (_, _, _) => { RebindHotkeys(); Save(); };
        card.DefinitionEdited += _ => Save();
        card.HeaderDrag += () => card.DoDragDrop(new DataObject("SONO_CARD", card.ChannelId), DragDropEffects.Move);
        card.RemoveRequested += id => RemoveChannel(id);
        card.AddAppRequested += id => ShowAddAppDialog(id);
        _cards[def.Id] = card;
        _flow.Controls.Add(card);
        _flow.Controls.SetChildIndex(card, _flow.Controls.Count - 2); // before the "+ New channel" button
        card.BringToFront();
    }

    private void RemoveChannel(string id)
    {
        if (MessageBox.Show(this, "Remove this channel? Assigned apps become independent again.",
                "Remove channel", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        lock (_settings)
        {
            var def = _settings.Channels.FirstOrDefault(c => c.Id == id);
            if (def is not null) _settings.Channels.Remove(def);
        }
        if (_cards.Remove(id, out var card)) { _flow.Controls.Remove(card); card.Dispose(); }
        RebindHotkeys();
        Save();
        _engine.ReconcileNow();
    }

    private void AddChannel()
    {
        var def = new ChannelDefinition
        {
            Name = $"Channel {_settings.Channels.Count(c => c.Kind == ChannelKind.Group) + 1}",
            ColorHex = Theme.Palette[_settings.Channels.Count % Theme.Palette.Length],
        };
        lock (_settings) _settings.Channels.Add(def);
        CreateCard(def);
        Save();
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
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(340, 110),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
        };
        var combo = new ComboBox { Left = 12, Top = 14, Width = 314, DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(choices.ToArray());
        combo.SelectedIndex = 0;
        var ok = new Button { Text = "Add", Left = 251, Top = 66, Width = 75, DialogResult = DialogResult.OK };
        pick.Controls.Add(combo);
        pick.Controls.Add(ok);
        pick.AcceptButton = ok;
        if (pick.ShowDialog(this) == DialogResult.OK && combo.SelectedItem is string chosen)
            AssignExe(chosen, channelId);
    }

    private void FlowCardDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData("SONO_CARD") is not string) return;
        var dragged = DraggedCard;
        DraggedCard = null;
        if (dragged is null || dragged.IsDisposed) return;
        var p = _flow.PointToClient(new Point(e.X, e.Y));
        var others = _flow.Controls.OfType<ChannelCard>().Where(c => c != dragged).OrderBy(c => c.Left).ToList();
        int idx = others.Count(c => c.Right < p.X);   // cards entirely left of the cursor
        _flow.Controls.SetChildIndex(dragged, Math.Clamp(idx, 0, others.Count));
    }

    private ChannelCard? DraggedCard;

    // ---------- right panel rows ----------

    private void RefreshRows(EngineSnapshot snap)
    {
        var ownedExes = snap.Sessions.Where(s => s.Owned).Select(s => s.Exe).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var live = snap.Sessions.Where(s => s.Exe != "unknown").Select(s => s.Exe).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        live.UnionWith(_settings.KnownApps.Select(k => k.ToLowerInvariant()));

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
                s.Enabled = !ownedExes.Contains(exe);
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

        var slider = new SliderBar { Location = new Point(116, 2), Size = new Size(122, 30), Anchor = AnchorStyles.Left };
        slider.Fill = Theme.Accent;
        slider.SetValueExternal(1f);
        slider.ValueChanged += v => _engine.SetIndependentVolume(exe, v, null);
        slider.EditCommitted += _ => SyncRowTag(row);
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

    private void SyncRowTag(Panel row)
    {
        // engine tick will re-sync real volumes; nothing else needed here
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
            card.Update(owned, snap.Mic?.DeviceName, _hotkeyError);
        }
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
        _engine.ReconcileNow();
        if (_last is not null) UpdateCards(_last);
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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
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
}
