using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using static Radii.Native;

namespace Radii;

/// <summary>What the ring was opened on: the resolved paths (or the folder) and the Explorer window the shell menu belongs to.</summary>
internal sealed record ShellContext(IReadOnlyList<string> Paths, string? Folder, IntPtr Owner);

/// <summary>
/// Borderless, topmost, never-activated window that hosts the ring and, beside it, the flyouts with shell extensions.
/// Because none of these windows takes focus, Explorer stays the foreground window and the actions
/// (shortcuts sent with SendInput) reach it.
/// </summary>
internal sealed class RadialWindow : Window
{
    private readonly InputHook _hook;
    private readonly List<FlyoutWindow> _flyouts = [];
    private RadialView? _view;
    private Theme _theme = Theme.Current();
    private ShellContext _context = new([], null, IntPtr.Zero);
    private ShellMenuSession? _session;
    private RECT _bounds;
    private RECT _ringRect;
    private RECT[] _flyoutRects = [];
    private int _flyoutToken;
    private double _dpiScale = 1;
    private readonly System.Windows.Threading.DispatcherTimer _awayTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    /// <summary>Distance (DIP) the pointer may stray from the ring and the flyouts before everything closes.</summary>
    private const double AwayDistance = 140;

    public RadialWindow(InputHook hook)
    {
        _hook = hook;
        _awayTimer.Tick += (_, _) => CloseIfAway();
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
    }

    /// <summary>Where the pointer was when the ring last opened: a second ring (formats) opens on the same spot.</summary>
    public POINT LastCursor { get; private set; }

    public bool ContainsRing(POINT pt) => Inside(_bounds, pt);

    /// <summary>Ring or any flyout. Called from the hook thread.</summary>
    public bool Contains(POINT pt)
    {
        if (Inside(_bounds, pt))
            return true;
        foreach (var rect in _flyoutRects)
        {
            if (Inside(rect, pt))
                return true;
        }
        return false;
    }

    private static bool Inside(RECT r, POINT pt) => pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;

    public void ShowAt(IReadOnlyList<RadialItem> items, Theme theme, string summary, POINT cursor, ShellContext context)
    {
        HideMenu(false);
        LastCursor = cursor;
        _theme = theme;
        _context = context;
        var settings = Settings.Current;

        // Physical pixels throughout: the process is per-monitor DPI aware.
        var monitor = MonitorFromPoint(cursor, 2);
        uint dpi = 96;
        GetDpiForMonitor(monitor, 0, out dpi, out _);
        var dpiScale = dpi / 96.0;
        _dpiScale = dpiScale;
        var ringScale = settings.RingScale;
        var side = (int)Math.Round(RadialView.Size * ringScale * dpiScale);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var x = Math.Clamp(cursor.X - side / 2, info.rcWork.Left, Math.Max(info.rcWork.Left, info.rcWork.Right - side));
        var y = Math.Clamp(cursor.Y - side / 2, info.rcWork.Top, Math.Max(info.rcWork.Top, info.rcWork.Bottom - side));

        // Taken before the window exists, so the screenshot shows what is really behind the ring.
        var backdrop = theme.Acrylic ? Backdrop.Grab(x, y, side, side) : null;
        var view = new RadialView(items, theme, summary, new ViewOptions(settings.ShowHints, settings.Animations, backdrop));
        view.MouseMove += (_, e) =>
        {
            var index = view.HitTest(e.GetPosition(view));
            view.SetHover(index);
            OnRingHover(view.ItemAt(index));
        };
        view.MouseLeave += (_, _) =>
        {
            view.SetHover(-1);
            _dwell.Stop();
            _dismiss.Stop(); // heading for a flyout: keep it
        };
        view.MouseLeftButtonDown += (_, e) =>
        {
            if (view.HitTest(e.GetPosition(view)) < 0)
                HideMenu();
        };
        view.MouseLeftButtonUp += (_, e) => Activate(view.ItemAt(view.HitTest(e.GetPosition(view))));
        view.MouseRightButtonDown += (_, _) => HideMenu();
        _view = view;
        Content = new Viewbox { Stretch = Stretch.Uniform, Child = view };
        Width = Height = RadialView.Size * ringScale;

        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        if (!IsVisible)
            Show();
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, HWND_TOPMOST, x, y, side, side, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        GetWindowRect(handle, out _bounds);
        var pad = (int)Math.Round(RadialView.Pad * ringScale * dpiScale);
        _ringRect = new RECT { Left = _bounds.Left + pad, Top = _bounds.Top + pad, Right = _bounds.Right - pad, Bottom = _bounds.Bottom - pad };

        _hook.MenuVisible = true;
        _awayTimer.Start();
        Animate(view, settings.Animations);
    }

    private void CloseIfAway()
    {
        if (!IsVisible)
        {
            _awayTimer.Stop();
            return;
        }
        GetCursorPos(out var cursor);
        var limit = AwayDistance * _dpiScale;
        var nearest = DistanceTo(_ringRect, cursor);
        foreach (var flyout in _flyouts)
            nearest = Math.Min(nearest, DistanceTo(flyout.VisibleBounds, cursor));
        if (nearest > limit)
            HideMenu();
    }

    private static double DistanceTo(RECT r, POINT pt)
    {
        var dx = Math.Max(Math.Max(r.Left - pt.X, 0), pt.X - r.Right);
        var dy = Math.Max(Math.Max(r.Top - pt.Y, 0), pt.Y - r.Bottom);
        return Math.Sqrt((double)dx * dx + (double)dy * dy);
    }

    private int _hideGeneration;

    // --- hover-to-open for the extensions sector ---
    private readonly System.Windows.Threading.DispatcherTimer _dwell = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _dwellWired;
    private readonly System.Windows.Threading.DispatcherTimer _dismiss = new() { Interval = TimeSpan.FromMilliseconds(320) };

    private void OnRingHover(RadialItem? item)
    {
        if (!_dwellWired)
        {
            _dwellWired = true;
            _dismiss.Tick += (_, _) =>
            {
                _dismiss.Stop();
                if (_view is { } v && v.ItemAt(v.Hovered) is { Flyout: false } && _flyouts.Count > 0)
                    CloseExtensions();
            };
            _dwell.Tick += (_, _) =>
            {
                _dwell.Stop();
                if (_view is { } v && v.ItemAt(v.Hovered) is { Flyout: true })
                    OpenExtensions();
            };
        }
        if (item is null)
        {
            _dwell.Stop();
            _dismiss.Stop(); // centre or gap: keep whatever is open, the pointer may be heading for a flyout
            return;
        }
        if (item.Flyout)
        {
            _dismiss.Stop();
            if (_flyouts.Count == 0 && !_dwell.IsEnabled)
                _dwell.Start();
            return;
        }
        _dwell.Stop();
        // Short grace period: crossing another sector on the way to the flyout must not close it.
        if (_flyouts.Count > 0 && !_dismiss.IsEnabled)
            _dismiss.Start();
    }

    private void CloseExtensions()
    {
        _flyoutToken++;
        _session?.Dispose();
        _session = null;
        CloseFlyouts(0, Settings.Current.Animations);
    }

    public void HideMenu() => HideMenu(true);

    private void HideMenu(bool animate)
    {
        _awayTimer.Stop();
        _dwell.Stop();
        _dismiss.Stop();
        _hook.MenuVisible = false;
        var generation = ++_hideGeneration;
        animate &= Settings.Current.Animations && IsVisible;
        CloseFlyouts(0, animate);
        _flyoutToken++;
        _session?.Dispose();
        _session = null;
        var view = _view;
        _view = null;
        if (!animate || view is null)
        {
            if (IsVisible)
                Hide();
            Content = null;
            return;
        }
        // Reverse of the opening: shrink a little and fade out, then hide (unless the menu was reopened meanwhile).
        view.IsHitTestVisible = false;
        var scale = view.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        view.RenderTransformOrigin = new Point(0.5, 0.5);
        view.RenderTransform = scale;
        var duration = TimeSpan.FromMilliseconds(130);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, 0.85, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, 0.85, duration) { EasingFunction = ease });
        var fade = new DoubleAnimation(Opacity, 0, duration);
        fade.Completed += (_, _) =>
        {
            if (generation != _hideGeneration)
                return;
            Hide();
            Content = null;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The right button was released after a drag: pick the sector under the pointer if it is clearly outside the centre.</summary>
    public void ReleaseAt(POINT pt)
    {
        if (_view is not { } view || !IsVisible)
            return;
        // Straight from the window rectangle: the ring is scaled uniformly, so view units = pixels * Size / window width.
        var width = _bounds.Right - _bounds.Left;
        if (width <= 0)
            return;
        var factor = RadialView.Size / width;
        var local = new Point((pt.X - _bounds.Left) * factor, (pt.Y - _bounds.Top) * factor);
        var centre = RadialView.CenterPoint;
        var distance = Math.Sqrt(Math.Pow(local.X - centre.X, 2) + Math.Pow(local.Y - centre.Y, 2));
        var index = view.HitTest(local);
        if (index >= 0 && distance > RadialView.Inner + 6)
            Activate(view.ItemAt(index));
    }

    public void HandleWheel(int delta)
    {
        if (_view is not { } view || view.Count == 0)
            return;
        var next = view.Hovered < 0 ? (delta > 0 ? view.Count - 1 : 0) : (view.Hovered + (delta > 0 ? -1 : 1) + view.Count) % view.Count;
        view.SetHover(next);
    }

    public void HandleKey(uint vk)
    {
        if (_view is not { } view)
            return;
        switch (vk)
        {
            case 0x1B:
                HideMenu();
                break;
            case 0x0D:
                Activate(view.ItemAt(view.Hovered));
                break;
            case 0x25 or 0x26:
                HandleWheel(1);
                break;
            case 0x27 or 0x28:
                HandleWheel(-1);
                break;
            case >= 0x31 and <= 0x39:
                Activate(view.ItemAt((int)vk - 0x31));
                break;
            case >= 0x61 and <= 0x69:
                Activate(view.ItemAt((int)vk - 0x61));
                break;
        }
    }

    private void Activate(RadialItem? item)
    {
        if (item is null)
            return;
        if (item.Flyout)
        {
            OpenExtensions();
            return;
        }
        Settings.RecordUse(item.Id);
        HideMenu();
        // Let the windows disappear and Explorer settle before the shortcut is sent.
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            item.Run();
        });
    }

    // --- extensions flyout ---------------------------------------------------------------------------------------

    private FlyoutWindow NewFlyout() => new(_theme, OnFlyoutHover, OnFlyoutActivate);

    private void OpenExtensions()
    {
        if (_flyouts.Count > 0)
            return; // already open (hover got there first)
        var token = ++_flyoutToken;
        var first = NewFlyout();
        first.Populate([new ShellEntry { Id = -2, Label = Menus.T("Loading…", "Загрузка…"), Disabled = true }]);
        first.ShowBeside(_ringRect, null, preferLeft: true);
        _flyouts.Add(first);
        PublishRects();
        _ = LoadExtensionsAsync(first, token);
    }

    private async Task LoadExtensionsAsync(FlyoutWindow first, int token)
    {
        ShellMenuSession? session = null;
        try
        {
            session = await ShellMenuHost.QueryAsync(_context.Paths, _context.Folder, _context.Owner);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
        }
        if (token != _flyoutToken || !_hook.MenuVisible)
        {
            session?.Dispose();
            return;
        }
        _session = session;
        WriteLog(session);
        var entries = session is null ? [] : ShellFilter.Clean(session.Entries);
        if (entries.Count > 0)
            entries.Add(new ShellEntry { IsSeparator = true });
        entries.Add(new ShellEntry { Id = NativeMenuId, Label = Menus.T("Windows menu", "Стандартное меню Windows"), Hint = "Shift+F10" });
        first.Populate(entries);
        first.ShowBeside(_ringRect, null, preferLeft: true);
        PublishRects();
    }

    private const int NativeMenuId = -1;

    /// <summary>What the shell returned for the last flyout, for diagnosing missing entries or icons.</summary>
    private void WriteLog(ShellMenuSession? session)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Settings.Folder);
            var text = $"{DateTime.Now:s}  owner={_context.Owner}  folder={_context.Folder}{Environment.NewLine}"
                + $"paths: {string.Join(" | ", _context.Paths)}{Environment.NewLine}{Environment.NewLine}"
                + (session is null ? "(no menu)" : ShellMenuHost.Describe(session.Entries));
            System.IO.File.WriteAllText(System.IO.Path.Combine(Settings.Folder, "last-menu.txt"), text);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
        }
    }

    private void OnFlyoutHover(ShellEntry entry, FlyoutView view, FrameworkElement row)
    {
        var level = _flyouts.FindIndex(f => ReferenceEquals(f.View, view));
        if (level < 0)
            return;
        CloseFlyouts(level + 1);
        if (!entry.HasChildren || entry.Disabled)
            return;
        var child = NewFlyout();
        child.Populate(entry.Children);
        var parent = _flyouts[level];
        var top = (int)row.PointToScreen(new Point(0, 0)).Y;
        child.ShowBeside(parent.VisibleBounds, top, parent.WentLeft);
        _flyouts.Add(child);
        PublishRects();
    }

    private void OnFlyoutActivate(ShellEntry entry)
    {
        var session = _session;
        _session = null;
        var owner = _context.Owner;
        GetCursorPos(out var cursor);
        _flyoutToken++;
        HideMenu();
        if (entry.Id == NativeMenuId)
        {
            session?.Dispose();
            _ = Task.Run(async () =>
            {
                await Task.Delay(60);
                Keys.Chord(Keys.Shift, Keys.F10);
            });
            return;
        }
        if (session is null)
            return;
        Settings.RecordUse("ext:" + (entry.Verb ?? entry.Label));
        _ = session.InvokeAsync(entry, cursor, owner).ContinueWith(_ => session.Dispose(), TaskScheduler.Default);
    }

    private void CloseFlyouts(int from, bool animate = false)
    {
        for (var i = _flyouts.Count - 1; i >= from; i--)
        {
            if (animate)
                _flyouts[i].FadeClose();
            else
                _flyouts[i].Close();
            _flyouts.RemoveAt(i);
        }
        PublishRects();
    }

    private void PublishRects() => _flyoutRects = _flyouts.Select(f => f.Bounds).ToArray();

    // --- visuals -------------------------------------------------------------------------------------------------

    private void Animate(RadialView view, bool animations)
    {
        if (!animations)
        {
            Opacity = 1;
            return;
        }
        var scale = new ScaleTransform(0.8, 0.8);
        view.RenderTransformOrigin = new Point(0.5, 0.5);
        view.RenderTransform = scale;
        // A little overshoot gives the opening a spring.
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        var duration = TimeSpan.FromMilliseconds(240);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.8, 1, duration) { EasingFunction = spring });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.8, 1, duration) { EasingFunction = spring });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100)));
    }
}

/// <summary>Screenshot of a screen rectangle (physical pixels), used as the blurred "acrylic" behind the ring.</summary>
internal static class Backdrop
{
    public static ImageSource? Grab(int x, int y, int width, int height)
    {
        try
        {
            using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
            var handle = bitmap.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(handle);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
