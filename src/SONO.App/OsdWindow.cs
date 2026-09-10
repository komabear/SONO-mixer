using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SONO.App;

/// <summary>
/// Borderless, topmost, never-focus volume OSD: channel name + % + channel-colored bar,
/// auto-hides after ~1.2s. Positioned per Settings.OsdAnchor ("off" disables).
/// </summary>
public sealed class OsdWindow : Window
{
    private readonly TextBlock _name;
    private readonly TextBlock _pct;
    private readonly Border _barFill;
    private readonly TextBlock _muteTag;
    private readonly Border _track;
    private DispatcherTimer? _hideTimer;

    private const int W = 330, H = 100, ScreenMargin = 18;

    public OsdWindow()
    {
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        IsHitTestVisible = false;
        Focusable = false;
        Topmost = true;
        CanResize = false;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Width = W;
        Height = H;

        _name = new TextBlock { FontSize = 14, FontWeight = FontWeight.Medium };
        _pct = new TextBlock { FontSize = 24, FontWeight = FontWeight.Bold };
        _muteTag = new TextBlock { Text = "MUTED", FontSize = 11, FontWeight = FontWeight.SemiBold,
            Opacity = 0, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _barFill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Height = 8 };
        _track = new Border { Height = 8, CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = _barFill };

        // top row: name (left) + % (right) — bottom row: full-width bar. Wide rectangle.
        var topRow = new Grid { ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(GridLength.Auto) } };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { _name, _muteTag } };
        topRow.Children.Add(nameRow);
        Grid.SetColumn(_pct, 1);
        _pct.TextAlignment = TextAlignment.Right;
        topRow.Children.Add(_pct);

        var card = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(24, 16),
            CornerRadius = new CornerRadius(16),
            Child = new StackPanel
            {
                Spacing = 10,
                Children = { topRow, _track },
            },
        };
        Content = card;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        var p = Themes.ThemeManager.Current;
        IBrush Brush(string hex) => Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : Brushes.Gray;
        if (Content is Border card)
        {
            card.Background = Brush(p.Elevated);
            _name.Foreground = Brush(p.Muted);
            _pct.Foreground = Brush(p.Text);
            _muteTag.Foreground = Brush(p.Danger);
            _track.Background = Brush(p.Field);
        }
    }

    /// <summary>Show (or refresh) the OSD for a channel. Must run on the UI thread.</summary>
    public void ShowOsd(string channelName, string colorHex, double volume, bool muted, string anchor)
    {
        if (anchor == "off") return;
        _name.Text = channelName;
        _pct.Text = $"{(int)Math.Round(volume * 100)}%";
        _muteTag.Opacity = muted ? 1 : 0;
        _barFill.Background = Color.TryParse(colorHex, out var c) ? new SolidColorBrush(c) : Brushes.White;
        _track.SizeChanged += (_, _) => _barFill.Width = Math.Clamp(volume, 0, 1) * _track.Bounds.Width;
        _barFill.Width = Math.Clamp(volume, 0, 1) * (_track.Bounds.Width > 0 ? _track.Bounds.Width : 180);
        ApplyTheme();
        PlaceOnScreen(anchor);

        _hideTimer?.Stop();
        Opacity = 1;   // cancel any in-flight fade
        if (!IsVisible && Owner is null) Show();   // first show: unowned (never Show(this) — it becomes its own owner on reuse → crash)
        else if (!IsVisible && Owner is Window w) Show(w);
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer!.Stop();
            _hideTimer = null;
            FadeOutAndHide();   // smooth 250 ms fade, then hide
        };
        _hideTimer.Start();
    }

    /// <summary>Manual opacity fade (12 steps × ~21 ms ≈ 250 ms) — avoids animation-API churn.</summary>
    private void FadeOutAndHide()
    {
        int step = 0;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(21) };
        t.Tick += (_, _) =>
        {
            step++;
            Opacity = 1.0 - step / 12.0;
            if (step >= 12)
            {
                t.Stop();
                Opacity = 1;   // reset for next show
                Hide();
            }
        };
        t.Start();
    }

    private void PlaceOnScreen(string anchor)
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var wa = screen.WorkingArea;

        double x = anchor.Contains("left") ? wa.X + ScreenMargin
                 : anchor.Contains("right") ? wa.X + wa.Width - W - ScreenMargin
                 : wa.X + (wa.Width - W) / 2;
        double y = anchor.Contains("top") ? wa.Y + ScreenMargin
                 : anchor.Contains("bottom") ? wa.Y + wa.Height - H - ScreenMargin
                 : wa.Y + (wa.Height - H) / 2;
        Position = new PixelPoint((int)x, (int)y);
    }

    /// <summary>Static helper used by MainWindow (safe when osd is null or anchor off).</summary>
    public static void Show(OsdWindow? osd, string anchor, string name, string color, double vol, bool muted)
    {
        if (osd is null || anchor == "off") return;
        osd.ShowOsd(name, color, vol, muted, anchor);
    }
}
