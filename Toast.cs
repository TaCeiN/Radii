using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static Radii.Native;

namespace Radii;

/// <summary>
/// Small Win11-style notification in the bottom-right corner of the primary screen (above the taskbar).
/// Never takes focus. Several can be on screen at once; they stack upwards. Safe to call from any thread.
/// </summary>
internal static class Toast
{
    private static readonly List<ToastWindow> Open = [];

    private static System.Windows.Threading.Dispatcher Ui => Application.Current.Dispatcher;

    /// <summary>One-shot message that disappears on its own.</summary>
    public static void Show(string title, string text)
    {
        var handle = Begin(title, text, null);
        handle.Finish(text, null, error: false, lifetime: TimeSpan.FromSeconds(4));
    }

    /// <summary>A toast that follows a running job: progress bar, optional cancel button.</summary>
    public static ToastHandle Begin(string title, string text, CancellationTokenSource? cancel)
    {
        var handle = new ToastHandle();
        Ui.BeginInvoke(() =>
        {
            var window = new ToastWindow(title, text, cancel);
            handle.Attach(window);
            Open.Add(window);
            window.Closed += (_, _) =>
            {
                Open.Remove(window);
                Relayout();
            };
            window.Present();
            Relayout();
        });
        return handle;
    }

    internal static void Relayout()
    {
        var monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        GetDpiForMonitor(monitor, 0, out var dpi, out _);
        var scale = dpi / 96.0;
        var gap = (int)Math.Round(8 * scale);
        var margin = (int)Math.Round(14 * scale);
        var bottom = info.rcWork.Bottom - margin;
        foreach (var window in Open)
        {
            var size = window.Measured;
            var width = (int)Math.Round(size.Width * scale);
            var height = (int)Math.Round(size.Height * scale);
            window.MoveTo(info.rcWork.Right - margin - width, bottom - height, width, height);
            bottom -= height + gap;
        }
    }
}

internal sealed class ToastHandle
{
    private ToastWindow? _window;
    private readonly List<Action<ToastWindow>> _pending = [];

    internal void Attach(ToastWindow window)
    {
        _window = window;
        foreach (var action in _pending)
            action(window);
        _pending.Clear();
    }

    private void Do(Action<ToastWindow> action)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_window is null)
                _pending.Add(action);
            else
                action(_window);
        });
    }

    /// <param name="progress">0..1, or null for an indeterminate bar.</param>
    public void Update(string text, double? progress) => Do(w => w.SetProgress(text, progress));

    public void Done(string text, string? revealPath = null) => Finish(text, revealPath, error: false, lifetime: TimeSpan.FromSeconds(7));

    public void Fail(string text) => Finish(text, null, error: true, lifetime: TimeSpan.FromSeconds(12));

    internal void Finish(string text, string? revealPath, bool error, TimeSpan lifetime) => Do(w => w.Complete(text, revealPath, error, lifetime));
}

internal sealed class ToastWindow : Window
{
    private const double ContentWidth = 360;

    private readonly Theme _theme = Theme.Current();
    private readonly TextBlock _title;
    private readonly TextBlock _text;
    private readonly ProgressBar _bar;
    private readonly TextBlock _action;
    private readonly CancellationTokenSource? _cancel;
    private readonly System.Windows.Threading.DispatcherTimer _life = new();
    private string? _reveal;
    private bool _finished;
    private bool _closing;

    public ToastWindow(string title, string text, CancellationTokenSource? cancel)
    {
        _cancel = cancel;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        Opacity = 0;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };

        var font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        _title = new TextBlock { Text = title, FontFamily = font, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = _theme.Text, TextTrimming = TextTrimming.CharacterEllipsis };
        _text = new TextBlock { Text = text, FontFamily = font, FontSize = 12, Foreground = _theme.TextMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        _bar = new ProgressBar
        {
            Height = 3,
            Margin = new Thickness(0, 10, 0, 0),
            Minimum = 0,
            Maximum = 1,
            IsIndeterminate = true,
            Foreground = new SolidColorBrush(_theme.Accent),
            Background = _theme.RowHover,
            BorderThickness = new Thickness(0),
        };
        _action = new TextBlock { FontFamily = font, FontSize = 12, Foreground = new SolidColorBrush(_theme.Accent), Margin = new Thickness(0, 8, 0, 0), Cursor = Cursors.Hand, Visibility = Visibility.Collapsed };

        var stack = new StackPanel { Width = ContentWidth - 28 };
        stack.Children.Add(_title);
        stack.Children.Add(_text);
        stack.Children.Add(_bar);
        stack.Children.Add(_action);

        var close = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            Foreground = _theme.TextMuted,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 10, 0),
            ToolTip = cancel is null ? null : Menus.T("Cancel", "Отменить"),
        };
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!_finished)
                _cancel?.Cancel();
            Dismiss();
        };

        var card = new Border
        {
            Background = _theme.FlyoutBackground,
            BorderBrush = _theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Child = stack,
            Margin = new Thickness(0),
        };
        var root = new Grid { Width = ContentWidth };
        root.Children.Add(card);
        root.Children.Add(close);
        root.MouseLeftButtonUp += (_, _) => Clicked();
        Content = root;

        _life.Tick += (_, _) => Dismiss();
        Measure();
    }

    /// <summary>Size in DIPs; the position is set by <see cref="Toast.Relayout"/>.</summary>
    public Size Measured { get; private set; }

    private void Measure()
    {
        if (Content is not FrameworkElement root)
            return;
        root.Measure(new Size(ContentWidth, double.PositiveInfinity));
        Measured = new Size(ContentWidth, Math.Ceiling(root.DesiredSize.Height));
        Width = Measured.Width;
        Height = Measured.Height;
    }

    public void MoveTo(int x, int y, int width, int height)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        SetWindowPos(handle, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void Present()
    {
        Show();
        if (Settings.Current.Animations)
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        else
            Opacity = 1;
    }

    public void SetProgress(string text, double? progress)
    {
        if (_finished)
            return;
        _text.Text = text;
        if (progress is { } value)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = Math.Clamp(value, 0, 1);
        }
        else
        {
            _bar.IsIndeterminate = true;
        }
        Resize();
    }

    public void Complete(string text, string? revealPath, bool error, TimeSpan lifetime)
    {
        _finished = true;
        _text.Text = text;
        _text.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xE8, 0x5C, 0x4A)) : _theme.TextMuted;
        _bar.Visibility = Visibility.Collapsed;
        _reveal = revealPath;
        if (revealPath is not null)
        {
            _action.Text = Menus.T("Show in folder", "Показать в папке");
            _action.Visibility = Visibility.Visible;
        }
        Resize();
        _life.Interval = lifetime;
        _life.Start();
    }

    private void Resize()
    {
        Measure();
        Toast.Relayout();
    }

    private void Clicked()
    {
        if (_reveal is not null && (System.IO.File.Exists(_reveal) || System.IO.Directory.Exists(_reveal)))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_reveal}\"") { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
        if (_finished)
            Dismiss();
    }

    private void Dismiss()
    {
        if (_closing)
            return;
        _closing = true;
        _life.Stop();
        if (!Settings.Current.Animations)
        {
            Close();
            return;
        }
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
