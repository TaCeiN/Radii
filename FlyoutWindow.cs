using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static Radii.Native;

namespace Radii;

/// <summary>Never-activated window that hosts one flyout list; positioned in physical pixels beside a parent rectangle.</summary>
internal sealed class FlyoutWindow : Window
{
    private readonly FlyoutView _view;
    private RECT _bounds;
    private double _scale = 1;

    public FlyoutWindow(Theme theme, Action<ShellEntry, FlyoutView, FrameworkElement> hover, Action<ShellEntry> activate)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        // Almost transparent, so the shadow margin still receives (and swallows) clicks.
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        _view = new FlyoutView(theme, 460, hover, activate);
        Content = _view;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
    }

    /// <summary>Screen rectangle of the window in physical pixels (valid once shown).</summary>
    public RECT Bounds => _bounds;

    /// <summary>The visible panel: the window without the transparent margin that holds the shadow.</summary>
    public RECT VisibleBounds
    {
        get
        {
            var margin = (int)Math.Round(FlyoutView.ShadowMargin * _scale);
            return new RECT { Left = _bounds.Left + margin, Top = _bounds.Top + margin, Right = _bounds.Right - margin, Bottom = _bounds.Bottom - margin };
        }
    }

    public FlyoutView View => _view;

    /// <summary>Fades the window out, then closes it.</summary>
    public void FadeClose()
    {
        if (!IsVisible)
        {
            Close();
            return;
        }
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>True when the last placement put the window to the left of its anchor.</summary>
    public bool WentLeft { get; private set; }

    public void Populate(IReadOnlyList<ShellEntry> entries) => _view.Populate(entries);

    /// <summary>Places the window to the right of <paramref name="anchor"/> (or to the left when there is no room).</summary>
    /// <param name="top">Wanted top edge in pixels; null centres the window vertically on the anchor.</param>
    public void ShowBeside(RECT anchor, int? top, bool preferLeft = false)
    {
        var centre = new POINT { X = (anchor.Left + anchor.Right) / 2, Y = (anchor.Top + anchor.Bottom) / 2 };
        var monitor = MonitorFromPoint(centre, 2);
        uint dpi = 96;
        GetDpiForMonitor(monitor, 0, out dpi, out _);
        var scale = dpi / 96.0;
        _scale = scale;
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);

        _view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = (int)Math.Ceiling(_view.DesiredSize.Width * scale);
        var height = (int)Math.Ceiling(_view.DesiredSize.Height * scale);
        Width = _view.DesiredSize.Width;
        Height = _view.DesiredSize.Height;

        // The anchor is a visible edge; the window has a transparent shadow margin around its panel, so the window itself
        // starts a margin short of where the panel should (2 DIP from the anchor).
        var margin = (int)Math.Round(FlyoutView.ShadowMargin * scale);
        var gap = (int)Math.Round(2 * scale);
        var rightX = anchor.Right + gap - margin;
        var leftX = anchor.Left - gap + margin - width;
        var fitsRight = rightX + width - margin <= info.rcWork.Right;
        var fitsLeft = leftX + margin >= info.rcWork.Left;
        WentLeft = preferLeft ? fitsLeft || !fitsRight : !fitsRight && fitsLeft;
        var x = WentLeft ? leftX : rightX;
        // Line the first row of this list up with the hovered row (panel border 1 + padding 4).
        var wantedTop = top.HasValue ? top.Value - margin - (int)Math.Round(5 * scale) : centre.Y - height / 2;
        var y = Math.Clamp(wantedTop, info.rcWork.Top, Math.Max(info.rcWork.Top, info.rcWork.Bottom - height));
        x = Math.Clamp(x, info.rcWork.Left, Math.Max(info.rcWork.Left, info.rcWork.Right - width));

        var firstTime = !IsVisible;
        if (firstTime)
        {
            Opacity = 0;
            Show();
        }
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        GetWindowRect(handle, out _bounds);
        if (firstTime)
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90)));
    }
}
