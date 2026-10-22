using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Radii.Converters;
using static Radii.Menus;

namespace Radii.Editors;

/// <summary>
/// Shared shell of the mini editors: themed window, content area, status line, Cancel and the main button.
/// An editor fills <see cref="Body"/> and implements <see cref="SaveAsync"/>; the shell runs it, shows progress
/// and errors, and finishes with a toast ("Show in folder").
/// </summary>
internal abstract class EditorWindow : Window
{
    private readonly ContentPresenter _body = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _extra = new() { Orientation = Orientation.Horizontal };
    private readonly ProgressBar _progress = new() { Height = 3, Visibility = Visibility.Collapsed, IsIndeterminate = false, Maximum = 1 };
    private CancellationTokenSource? _job;
    private string _hint = "";

    protected EditorWindow(string title, double width, double height, string primaryText)
    {
        var theme = Theme.Current();
        Dark = theme.Dark;
        _acrylic = theme.Acrylic && Environment.OSVersion.Version.Build >= 22621;
        Muted = Hex(Dark ? "#B0B0B0" : "#616161");
        // With the system backdrop the window and panels are translucent so the blur shows through (like the ring).
        PanelBrush = Hex(Dark ? (_acrylic ? "#1FFFFFFF" : "#2B2B2B") : (_acrylic ? "#99FFFFFF" : "#FBFBFB"));
        CanvasBrush = Hex(Dark ? (_acrylic ? "#59000000" : "#181818") : (_acrylic ? "#14000000" : "#E6E6E6"));
        LineBrush = Hex(Dark ? "#3A3A3A" : "#E0E0E0");
        AccentBrush = new SolidColorBrush(theme.Accent);
        DangerBrush = Hex(Dark ? "#FF8A80" : "#C42B1C");

        Title = title;
        Width = width;
        Height = height;
        MinWidth = Math.Min(width, 520);
        MinHeight = Math.Min(height, 300);
        Topmost = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Hex(Dark ? "#202020" : "#F3F3F3");
        if (_acrylic)
        {
            // Like the ring: a screenshot of what is behind, blurred, under a tint. Taken now, before the window is shown.
            Native.GetCursorPos(out var cursor);
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).Bounds;
            _shotArea = screen;
            _shot = Backdrop.Grab(screen.Left, screen.Top, screen.Width, screen.Height);
            _acrylic = _shot is not null;
            if (_acrylic)
            {
                SourceInitialized += (_, _) => SetDarkCaption();
                Loaded += (_, _) => PositionGlass();
                LocationChanged += (_, _) => PositionGlass();
                SizeChanged += (_, _) => PositionGlass();
                DpiChanged += (_, _) => PositionGlass();
            }
        }
        Foreground = Hex(Dark ? "#FFFFFF" : "#1B1B1B");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;
        UseLayoutRounding = true;

        _status.Foreground = Muted;
        _status.FontSize = 12;

        // Quiet footer: no panel of its own, a small accent button and plain text buttons, so the content stays the main thing.
        Primary = new Button { Content = primaryText, MinWidth = 104, Height = 32, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(6, 0, 0, 0) };
        if (TryFindResource("AccentButtonStyle") is Style accent)
            Primary.Style = accent;
        Cancel = new Button { Content = T("Cancel", "Отмена"), Height = 32 };
        Ghost(Cancel);
        Primary.Click += async (_, _) => await RunSaveAsync(SaveAsync);
        Cancel.Click += (_, _) => CancelOrClose();

        var footer = new Grid { Margin = new Thickness(Gap, 4, Gap, 16) };
        // Cancel on the left, the status in the middle, extra actions and the main button on the right.
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(Cancel);
        Grid.SetColumn(_status, 1);
        _status.Margin = new Thickness(12, 0, 12, 0);
        footer.Children.Add(_status);
        Grid.SetColumn(_extra, 2);
        footer.Children.Add(_extra);
        Grid.SetColumn(Primary, 3);
        footer.Children.Add(Primary);

        var bar = new StackPanel { Children = { _progress, footer } };
        var footerBorder = new Border { Child = bar };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_body);
        Grid.SetRow(footerBorder, 1);
        root.Children.Add(footerBorder);
        if (_acrylic)
        {
            // The title bar is ours so that the same tint covers it and the body (the system one stays lighter).
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 32,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(-1),
                UseAeroCaptionButtons = true,
                CornerRadius = new CornerRadius(0),
            });
            var caption = new TextBlock
            {
                Text = title,
                Foreground = Muted,
                FontSize = 12,
                Margin = new Thickness(14, 0, 150, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false,
            };
            var outer = new Grid();
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            outer.RowDefinitions.Add(new RowDefinition());
            _glassImage = new Image
            {
                Source = _shot,
                Stretch = Stretch.Fill,
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 40, KernelType = System.Windows.Media.Effects.KernelType.Gaussian, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance },
            };
            var glass = new Canvas { ClipToBounds = true, IsHitTestVisible = false };
            glass.Children.Add(_glassImage);
            Grid.SetRowSpan(glass, 2);
            outer.Children.Add(glass);
            var tint = new Border { Background = Hex(Dark ? "#99181818" : "#99F3F3F3"), IsHitTestVisible = false };
            Grid.SetRowSpan(tint, 2);
            outer.Children.Add(tint);
            outer.Children.Add(caption);
            Grid.SetRow(root, 1);
            outer.Children.Add(root);
            StateChanged += (_, _) => outer.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
            Content = outer;
        }
        else
            Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox)
            {
                e.Handled = true;
                CancelOrClose();
            }
        };
        Loaded += (_, _) =>
        {
            PlaceOnCursorMonitor();
            AnimateOpen();
        };
        Closed += (_, _) => _job?.Cancel();
    }

    private bool _acrylic;
    private ImageSource? _shot;
    private System.Drawing.Rectangle _shotArea;
    private Image? _glassImage;

    /// <summary>Dark caption buttons for the dark theme.</summary>
    private void SetDarkCaption()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var dark = Dark ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)); // IMMERSIVE_DARK_MODE
    }

    /// <summary>Keeps the blurred screenshot aligned with the screen, so the window looks like frosted glass wherever it is moved.</summary>
    private void PositionGlass()
    {
        if (_glassImage is null || _shot is null)
            return;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !Native.GetWindowRect(handle, out var rect))
            return;
        var dpi = VisualTreeHelper.GetDpi(this);
        _glassImage.Width = _shotArea.Width / dpi.DpiScaleX;
        _glassImage.Height = _shotArea.Height / dpi.DpiScaleY;
        Canvas.SetLeft(_glassImage, (_shotArea.Left - rect.Left) / dpi.DpiScaleX);
        Canvas.SetTop(_glassImage, (_shotArea.Top - rect.Top) / dpi.DpiScaleY);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Flat text button: no fill or border until the pointer is over it.</summary>
    protected void Ghost(Button button)
    {
        var hover = Dark ? "#1FFFFFFF" : "#14000000";
        var pressed = Dark ? "#14FFFFFF" : "#0D000000";
        var template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
            "<Border x:Name='b' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='5' Padding='{TemplateBinding Padding}'>" +
            "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
            "<ControlTemplate.Triggers>" +
            $"<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='{hover}'/></Trigger>" +
            $"<Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='Background' Value='{pressed}'/></Trigger>" +
            "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='b' Property='Opacity' Value='0.4'/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>");
        button.Template = template;
        button.Padding = new Thickness(14, 0, 14, 0);
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.Margin = new Thickness(0, 0, 2, 0);
    }

    protected bool Dark { get; }
    protected Brush Muted { get; }
    protected Brush PanelBrush { get; }
    protected Brush CanvasBrush { get; }
    protected Brush LineBrush { get; }
    protected Brush AccentBrush { get; }
    protected Brush DangerBrush { get; }
    protected Button Primary { get; }
    protected Button Cancel { get; }

    protected UIElement? Body
    {
        get => _body.Content as UIElement;
        set => _body.Content = value;
    }

    /// <summary>Writes the result and returns the path of the new file.</summary>
    protected abstract Task<string> SaveAsync(ConvertContext context);

    /// <summary>Toast text of the finished action when "Saved &lt;file&gt;" does not fit (renaming); reset after each use.</summary>
    protected string? DoneOverride { get; set; }

    /// <summary>False while the editor still loads its data; only the snapshot diagnostics wait for it.</summary>
    protected virtual bool SnapshotReady => true;

    /// <summary>Text of the toast title after a successful save.</summary>
    protected abstract string DoneTitle { get; }

    /// <summary>Quiet line in the footer: where the result will go.</summary>
    protected void SetHint(string text)
    {
        _hint = text;
        if (_job is null)
            ShowStatus(text, error: false);
    }

    protected void ShowStatus(string text, bool error)
    {
        _status.Text = text;
        _status.Foreground = error ? DangerBrush : Muted;
        _status.ToolTip = text;
    }

    /// <summary>Progress report for engine downloads and long steps; may be called from any thread.</summary>
    protected ConvertContext NewContext(CancellationToken cancel) => new(cancel, (text, fraction) => Dispatcher.BeginInvoke(() =>
    {
        if (text.Length > 0)
            ShowStatus(text, error: false);
        if (_job is null)
            return; // a late report after the job ended must not leave the bar on screen
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = fraction is null;
        if (fraction is { } f)
            _progress.Value = f;
    }));

    protected static SolidColorBrush Hex(string color) => (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;

    // ---- Shared layout kit: every editor is built from these so that the windows look like one family. ----

    /// <summary>Outer margin of the content and the gap between its blocks.</summary>
    protected const double Gap = 20;

    /// <summary>Width of the right-hand "inspector" column of the editors that show a preview.</summary>
    protected const double SideWidth = 260;

    /// <summary>Lets the window height follow its content (up to <paramref name="maxHeight"/>) instead of a fixed size; for list and form editors.</summary>
    protected void FitHeight(double maxHeight)
    {
        SizeToContent = SizeToContent.Height;
        MaxHeight = maxHeight;
    }

    /// <summary>Small quiet section title.</summary>
    protected TextBlock Heading(string text, double top = 18) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = Muted,
        Margin = new Thickness(2, top, 0, 8),
    };

    /// <summary>Rounded panel (Win11 settings card) that groups related controls.</summary>
    protected Border Card(UIElement child, Thickness? padding = null) => new()
    {
        Background = PanelBrush,
        BorderBrush = LineBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = padding ?? new Thickness(14, 12, 14, 12),
        Child = child,
    };

    /// <summary>Section title followed by a card with the given blocks stacked inside.</summary>
    protected StackPanel Section(string title, params UIElement[] blocks)
    {
        var inner = new StackPanel();
        foreach (var block in blocks)
            inner.Children.Add(block);
        var section = new StackPanel();
        if (title.Length > 0)
            section.Children.Add(Heading(title, section.Children.Count == 0 ? 0 : 18));
        section.Children.Add(Card(inner));
        return section;
    }

    /// <summary>Stacks sections with an even gap; the first one has no top gap.</summary>
    protected StackPanel Column(params UIElement[] sections)
    {
        var column = new StackPanel();
        foreach (var section in sections)
        {
            if (section is FrameworkElement element && column.Children.Count > 0)
                element.Margin = new Thickness(element.Margin.Left, element.Margin.Top + 16, element.Margin.Right, element.Margin.Bottom);
            column.Children.Add(section);
        }
        return column;
    }

    /// <summary>Settings row: label (and quiet hint) on the left, the control on the right.</summary>
    protected Grid Row(string label, UIElement control, string? hint = null, double top = 0)
    {
        var grid = new Grid { Margin = new Thickness(0, top, 0, 0), MinHeight = 32 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(hint))
            text.Children.Add(new TextBlock { Text = hint, FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(text);
        if (control is FrameworkElement fe)
            fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>Slider row: label with the value on the right, the slider under it.</summary>
    protected StackPanel SliderRow(string label, Slider slider, TextBlock value, double top = 0)
    {
        var head = new Grid();
        head.Children.Add(new TextBlock { Text = label });
        value.Foreground = Muted;
        value.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.Add(value);
        slider.Margin = new Thickness(0, 4, 0, 0);
        return new StackPanel { Margin = new Thickness(0, top, 0, 0), Children = { head, slider } };
    }

    /// <summary>Quiet explanatory text.</summary>
    protected TextBlock Note(string text, double top = 8) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Muted,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, 0),
    };

    /// <summary>Compact preset button (aspect ratio, percent, level...).</summary>
    protected static Button Chip(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(11, 4, 11, 4), MinWidth = 44 };
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>Preview area: dark rounded canvas that clips its child.</summary>
    protected Border PreviewBox(UIElement child) => new()
    {
        Background = CanvasBrush,
        BorderBrush = LineBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        ClipToBounds = true,
        Child = child,
    };

    /// <summary>Preview on the left, inspector column with the given sections on the right; both with the standard margins.</summary>
    protected Grid PreviewLayout(UIElement preview, params UIElement[] sections)
    {
        var grid = new Grid { Margin = new Thickness(Gap, Gap, Gap, Gap) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(preview);
        var side = new ScrollViewer
        {
            Width = SideWidth + 8,
            Margin = new Thickness(Gap - 8, 0, -8, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Margin = new Thickness(0, 0, 8, 0), Children = { Column(sections) } },
        };
        Grid.SetColumn(side, 1);
        grid.Children.Add(side);
        return grid;
    }

    private void CancelOrClose()
    {
        if (_job is not null)
            _job.Cancel();
        else
            Close();
    }

    /// <summary>Runs <paramref name="work"/> with the footer progress; returns false when it was cancelled or failed (the reason is shown).</summary>
    protected async Task<bool> RunAsync(Func<ConvertContext, Task> work)
    {
        if (_job is not null)
            return false;
        using var cancel = new CancellationTokenSource();
        _job = cancel;
        Primary.IsEnabled = false;
        _extra.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = true;
        try
        {
            await work(NewContext(cancel.Token));
            return true;
        }
        catch (OperationCanceledException)
        {
            ShowStatus(T("Cancelled", "Отменено"), error: false);
        }
        catch (ConvertException ex)
        {
            ShowStatus(ex.Message, error: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException)
        {
            ShowStatus(T("Failed: ", "Ошибка: ") + ex.Message, error: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never leave an editor silently stuck: log it and say so in the footer.
            Program.LogCrash(ex);
            ShowStatus(T("Unexpected error: ", "Неожиданная ошибка: ") + ex.Message, error: true);
        }
        finally
        {
            _job = null;
            _progress.Visibility = Visibility.Collapsed;
            Primary.IsEnabled = true;
            _extra.IsEnabled = true;
        }
        return false;
    }

    /// <summary>A second action button left of Cancel (for example "Extract selected").</summary>
    protected Button AddAction(string text, Func<ConvertContext, Task<string>> save)
    {
        var button = new Button { Content = text, Height = 32 };
        Ghost(button);
        button.Click += async (_, _) => await RunSaveAsync(save);
        _extra.Children.Add(button);
        return button;
    }

    /// <summary>Runs a save with the footer progress; on success a toast with "Show in folder" and the window closes.</summary>
    protected async Task RunSaveAsync(Func<ConvertContext, Task<string>> save)
    {
        string? output = null;
        if (!await RunAsync(async context => output = await save(context)) || output is null)
            return;
        var text = DoneOverride ?? T("Saved ", "Сохранено: ") + Path.GetFileName(output);
        DoneOverride = null;
        Toast.Begin(DoneTitle, "", null).Done(text, output);
        Close();
    }

    private bool _closeAnimated;

    /// <summary>The diagnostics (snapshots, test saves) run without the open and close animations.</summary>
    private bool _quiet;

    /// <summary>Opening: the window rises a little with a soft ease while its content fades in.</summary>
    private void AnimateOpen()
    {
        if (_quiet || !Settings.Current.Animations || Content is not UIElement content)
            return;
        var final = Top;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(TopProperty, new DoubleAnimation(final + 22, final, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    /// <summary>Closing: the content fades and the window sinks a little before it goes.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closeAnimated || _quiet || !Settings.Current.Animations || !IsVisible || Content is not UIElement content)
            return;
        _closeAnimated = true;
        e.Cancel = true;
        IsHitTestVisible = false;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
        fade.Completed += (_, _) => Close();
        BeginAnimation(TopProperty, new DoubleAnimation(Top, Top + 14, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        content.BeginAnimation(OpacityProperty, fade);
    }

    private void PlaceOnCursorMonitor()
    {
        // All coordinates are physical pixels (per-monitor DPI aware process).
        Native.GetCursorPos(out var point);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(point.X, point.Y));
        var area = screen.WorkingArea;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Native.GetWindowRect(handle, out var rect);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var x = area.Left + Math.Max(0, (area.Width - width) / 2);
        var y = area.Top + Math.Max(0, (area.Height - height) / 2);
        Native.SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
    }

    /// <summary>Diagnostics (<c>--editor-save</c>): sets the editor's choices from "key=value,flag" text before saving.</summary>
    protected virtual void ConfigureForTest(string spec)
    {
    }

    /// <summary>Shows the window offscreen, applies <paramref name="spec"/>, saves, and returns the output path or the error text.</summary>
    public string SaveForTest(string spec)
    {
        _quiet = true;
        Left = -4000;
        Top = 0;
        ShowActivated = false;
        Show();
        var until = DateTime.UtcNow.AddSeconds(120);
        while (!SnapshotReady && DateTime.UtcNow < until)
            PumpOnce();
        ConfigureForTest(spec);
        var work = SaveAsync(NewContext(CancellationToken.None));
        while (!work.IsCompleted && DateTime.UtcNow < until)
            PumpOnce();
        Close();
        if (!work.IsCompleted)
            return "TIMEOUT";
        return work.IsFaulted ? "FAILED: " + work.Exception!.GetBaseException().Message : "OK: " + work.Result;
    }

    private void PumpOnce()
    {
        Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
        Thread.Sleep(30);
    }

    /// <summary>Diagnostics (<c>--editor-show</c>): shows the window on screen and saves what the screen really shows (backdrop included).</summary>
    public int ShowAndCapture(string path)
    {
        _quiet = true;
        Show();
        var ready = DateTime.UtcNow.AddSeconds(30);
        while (!SnapshotReady && DateTime.UtcNow < ready)
            PumpOnce();
        if (Environment.GetEnvironmentVariable("RADII_SHOT_SPEC") is { Length: > 0 } spec)
            ConfigureForTest(spec);
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until)
            PumpOnce();
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Native.GetWindowRect(handle, out var rect);
        using var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Close();
        return 0;
    }

    /// <summary>Offscreen render of the window content for <c>--snapshot-editor</c>.</summary>
    public int Render(string path)
    {
        _quiet = true;
        Left = -4000;
        Top = 0;
        ShowActivated = false;
        Show();
        // Editors that load in the background (waveform, thumbnails) get a few seconds to finish.
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!SnapshotReady && DateTime.UtcNow < until)
        {
            Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(50);
        }
        UpdateLayout();
        Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => { }));
        var element = (FrameworkElement)Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen())
        {
            // Snapshots have no blur behind them: paint an opaque base under the translucent colours.
            context.DrawRectangle(Hex(Dark ? "#1B1B1B" : "#E4E4E4"), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            context.DrawRectangle(Background, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        bitmap.Render(backdrop);
        bitmap.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
            encoder.Save(file);
        Close();
        return 0;
    }
}
