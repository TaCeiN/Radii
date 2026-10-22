using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using static Radii.Native;

namespace Radii;

/// <summary>Which ring a right click opens: the usual one, the converter (Shift) or the editors (Ctrl).</summary>
internal enum RingMode
{
    Normal,
    Convert,
    Edit,
}

/// <summary>Which of the two other rings make sense for the item under the pointer.</summary>
internal sealed record ModeChips(bool Convert, bool Edit)
{
    public bool Any => Convert || Edit;

    public static ModeChips For(IReadOnlyList<string> paths) => paths.Count == 0
        ? new ModeChips(false, false)
        : new ModeChips(Converters.ConvertMenu.Supports(paths), Editors.EditorMenu.Supports(paths));
}

/// <summary>One pill under the ring: where it leads, a hint (shortcut or key) and what to do when it is chosen.</summary>
internal sealed record ChipSpec(RingMode? Mode, string Name, string Hint, string Glyph, Action Activate, bool HintLeft = false);

/// <summary>The pills under the ring: other ring modes and, inside a second-level ring, "Back". They work as buttons and remind of the shortcuts.</summary>
internal sealed class ChipsPanel : StackPanel
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");

    private readonly Theme _theme;
    private readonly List<Chip> _chips = [];

    private sealed record Chip(ChipSpec Spec, Border Border, TextBlock[] Texts);

    public ChipsPanel(Theme theme, IReadOnlyList<ChipSpec> specs)
    {
        _theme = theme;
        Orientation = Orientation.Horizontal;
        foreach (var spec in specs)
            Add(spec);
    }

    public event Action<ChipSpec>? Chosen;

    public IReadOnlyList<(ChipSpec Spec, FrameworkElement Element)> Items => _chips.Select(c => (c.Spec, (FrameworkElement)c.Border)).ToList();

    private void Add(ChipSpec spec)
    {
        var (glyph, name, keys) = (spec.Glyph, spec.Name, spec.Hint);
        var icon = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Foreground = _theme.Text };
        var label = new TextBlock { Text = name, FontFamily = TextFont, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = _theme.Text };
        var hint = new TextBlock { Text = keys, FontFamily = TextFont, FontSize = 11, Margin = spec.HintLeft ? new Thickness(0, 0, 9, 0) : new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = _theme.TextMuted };
        var border = new Border
        {
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(_chips.Count == 0 ? 0 : 8, 0, 0, 0),
            Background = _theme.FlyoutBackground,
            BorderBrush = _theme.Border,
            BorderThickness = new Thickness(1),
            Child = spec.HintLeft
                ? new StackPanel { Orientation = Orientation.Horizontal, Children = { hint, icon, label } }
                : new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label, hint } },
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = _theme.Dark ? 0.5 : 0.25, Color = Colors.Black },
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        var chip = new Chip(spec, border, [icon, label, hint]);
        _chips.Add(chip);
        border.MouseEnter += (_, _) => SetHover(spec);
        border.MouseLeave += (_, _) => SetHover(null);
        border.MouseLeftButtonUp += (_, _) => Chosen?.Invoke(spec);
        Children.Add(border);
    }

    public void SetHover(ChipSpec? spec)
    {
        foreach (var chip in _chips)
        {
            var on = ReferenceEquals(chip.Spec, spec);
            chip.Border.Background = on ? _theme.Hover : _theme.FlyoutBackground;
            chip.Texts[0].Foreground = chip.Texts[1].Foreground = on ? _theme.OnAccent : _theme.Text;
            chip.Texts[2].Foreground = on ? _theme.OnAccent : _theme.TextMuted;
        }
    }
}

/// <summary>Never-activated window with the chips, placed just under the ring (above it when there is no room).</summary>
internal sealed class ChipsWindow : Window
{
    private const double Margin_ = 14;

    private readonly ChipsPanel _panel;
    private List<(ChipSpec Spec, RECT Rect)> _rects = [];

    public ChipsWindow(Theme theme, IReadOnlyList<ChipSpec> specs, Action<ChipSpec> chosen)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        _panel = new ChipsPanel(theme, specs) { Margin = new Thickness(Margin_) };
        _panel.Chosen += chosen;
        Content = _panel;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
    }

    /// <summary>Whole window in screen pixels (set by <see cref="ShowNear"/>); read from the hook thread.</summary>
    public RECT Bounds { get; private set; }

    public void ShowNear(RECT ring, RECT work, double dpiScale, bool animate)
    {
        _panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = (int)Math.Ceiling(_panel.DesiredSize.Width * dpiScale);
        var height = (int)Math.Ceiling(_panel.DesiredSize.Height * dpiScale);
        var margin = (int)Math.Round(Margin_ * dpiScale);
        var gap = (int)Math.Round(6 * dpiScale);

        var x = Math.Clamp((ring.Left + ring.Right - width) / 2, work.Left, Math.Max(work.Left, work.Right - width));
        var y = ring.Bottom + gap - margin;
        if (y + height > work.Bottom)
            y = ring.Top - gap + margin - height;
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));

        Opacity = 0;
        Show();
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Bounds = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
        UpdateLayout();
        _rects = _panel.Items.Select(item =>
        {
            var origin = item.Element.TransformToAncestor(this).Transform(new Point(0, 0));
            var left = x + (int)Math.Round(origin.X * dpiScale);
            var top = y + (int)Math.Round(origin.Y * dpiScale);
            return (item.Spec, new RECT { Left = left, Top = top, Right = left + (int)Math.Round(item.Element.ActualWidth * dpiScale), Bottom = top + (int)Math.Round(item.Element.ActualHeight * dpiScale) });
        }).ToList();
        if (animate)
        {
            // Fade in and rise a few pixels, a beat after the ring itself.
            var rise = new TranslateTransform(0, 8);
            _panel.RenderTransform = rise;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(60), EasingFunction = ease });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = TimeSpan.FromMilliseconds(40) });
        }
        else
        {
            Opacity = 1;
        }
    }

    /// <summary>Chip under this screen point (physical pixels), for a right button released after a drag.</summary>
    public ChipSpec? HitChip(POINT pt)
    {
        foreach (var (spec, rect) in _rects)
        {
            if (pt.X >= rect.Left && pt.X < rect.Right && pt.Y >= rect.Top && pt.Y < rect.Bottom)
                return spec;
        }
        return null;
    }

    public void FadeClose()
    {
        IsHitTestVisible = false;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
