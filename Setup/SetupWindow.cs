using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Radii.Converters;
using static Radii.Menus;

namespace Radii.Setup;

/// <summary>
/// Installer wizard. Mode (automatic or manual) → [options] → progress → done.
/// Automatic: default folder, autostart, Start menu shortcut and every conversion engine, no questions.
/// </summary>
internal sealed class SetupWindow : Window
{
    private enum Page { Mode, Options, Progress, Done }

    private readonly SetupOptions _options;
    private readonly bool _dark = Theme.SystemDark();
    private readonly ContentControl _body = new();
    private readonly Button _back = new() { MinWidth = 92, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _next = new() { MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _cancel = new() { MinWidth = 92 };
    private readonly Button _uninstall = new() { Padding = new Thickness(10, 4, 10, 4) };
    private readonly string? _installed = Installer.InstalledDir();
    private readonly List<(CheckBox Box, long Bytes)> _engineBoxes = [];
    private readonly TextBlock _total = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _percent = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ProgressBar _progress = new() { Height = 8, Minimum = 0, Maximum = 1 };
    private readonly CancellationTokenSource _cts = new();

    private static bool _preview;

    private static bool Has(Engine engine) => !_preview && Engines.IsInstalled(engine);

    private Page _page;
    private bool _auto = true;
    private bool _working;
    private string _summary = "";
    private TextBox _dirBox = new();

    public SetupWindow(SetupOptions options, bool auto)
    {
        _options = options;
        Title = T("Radii setup", "Установка Radii");
        Width = 620;
        Height = 620;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush(_dark ? "#202020" : "#F3F3F3");
        Foreground = Brush(_dark ? "#FFFFFF" : "#1B1B1B");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;

        _back.Content = T("Back", "Назад");
        _cancel.Content = T("Cancel", "Отмена");
        _uninstall.Content = T("Uninstall", "Удалить");
        _uninstall.Visibility = _installed is null ? Visibility.Collapsed : Visibility.Visible;
        if (TryFindResource("AccentButtonStyle") is Style accent)
            _next.Style = accent;

        _back.Click += (_, _) => Show(Page.Mode);
        _cancel.Click += (_, _) => Close();
        _next.Click += async (_, _) => await NextAsync();
        _uninstall.Click += (_, _) =>
        {
            if (MessageBox.Show(T("Remove Radii?", "Удалить Radii?"), "Radii", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            var data = MessageBox.Show(T("Also delete settings and downloaded engines?", "Удалить заодно настройки и скачанные движки?"), "Radii", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            Installer.Uninstall(data);
            MessageBox.Show(T("Radii was removed.", "Radii удалён."), "Radii", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        };
        Closing += (_, e) =>
        {
            if (!_working)
                return;
            _cts.Cancel();
            e.Cancel = true; // finish through the normal path so the app stays consistent
        };

        var footer = new DockPanel { Margin = new Thickness(24, 12, 24, 18), LastChildFill = false };
        DockPanel.SetDock(_uninstall, Dock.Left);
        footer.Children.Add(_uninstall);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        buttons.Children.Add(_cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);

        var root = new Grid { Background = Background };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = Header();
        Grid.SetRow(header, 0);
        Grid.SetRow(_body, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(header);
        root.Children.Add(_body);
        root.Children.Add(footer);
        Content = root;

        if (auto)
            Loaded += async (_, _) =>
            {
                _auto = true;
                await StartInstallAsync();
            };
        else
            Show(Page.Mode);
    }

    /// <summary>Offscreen render of one page, for <c>--setup-snapshot</c>.</summary>
    public static int RenderTo(string path, string page, string? language = null)
    {
        _preview = true;
        if (language is not null)
            Settings.Current.Language = language;
#pragma warning disable WPF0001
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
#pragma warning restore WPF0001
        var window = new SetupWindow(new SetupOptions { Dir = @"C:\Users\you\AppData\Local\Programs\Radii" }, false) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0, ShowActivated = false };
        window._auto = page != "options";
        window._summary = T("Installed to C:\\Users\\you\\AppData\\Local\\Programs\\Radii\n• FFmpeg, ImageMagick, Pandoc: ready", "Установлено в C:\\Users\\you\\AppData\\Local\\Programs\\Radii\n• FFmpeg, ImageMagick, Pandoc: готово");
        window.Show();
        window.Show(page switch { "options" => Page.Options, "progress" => Page.Progress, "done" => Page.Done, _ => Page.Mode });
        if (page == "progress")
        {
            window._status.Text = T("Downloading FFmpeg (one time): 40 / 115 MB", "Скачиваю FFmpeg (один раз): 40 / 115 МБ");
            window._progress.Value = 0.35;
            window._percent.Text = "35%";
        }
        window.UpdateLayout();
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => { }));
        var element = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
            encoder.Save(file);
        window.Close();
        app.Shutdown();
        return 0;
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    private Brush Muted => Brush(_dark ? "#B0B0B0" : "#616161");

    private Brush CardBorder => Brush(_dark ? "#3A3A3A" : "#D6D6D6");

    private Brush CardFill => Brush(_dark ? "#2B2B2B" : "#FFFFFF");

    private UIElement Header()
    {
        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 8) };
        panel.Children.Add(new TextBlock { Text = "Radii", FontSize = 26, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = T("Radial context menu for Explorer and the desktop", "Круговое контекстное меню для Проводника и рабочего стола") + "  ·  v" + Installer.Version,
            Foreground = Muted,
            Margin = new Thickness(0, 2, 0, 0),
        });
        return panel;
    }

    private void Show(Page page)
    {
        _page = page;
        _back.Visibility = page == Page.Options ? Visibility.Visible : Visibility.Collapsed;
        _cancel.Visibility = page == Page.Done ? Visibility.Collapsed : Visibility.Visible;
        _cancel.IsEnabled = true;
        _next.IsEnabled = true;
        _uninstall.Visibility = page == Page.Mode && _installed is not null ? Visibility.Visible : Visibility.Collapsed;
        switch (page)
        {
            case Page.Mode:
                _next.Content = T("Next", "Далее");
                _body.Content = ModePage();
                break;
            case Page.Options:
                _next.Content = T("Install", "Установить");
                _body.Content = OptionsPage();
                break;
            case Page.Progress:
                _next.Content = T("Install", "Установить");
                _next.IsEnabled = false;
                _body.Content = ProgressPage();
                break;
            case Page.Done:
                _next.Content = T("Close", "Закрыть");
                _body.Content = DonePage();
                break;
        }
    }

    private static string Mb(long bytes) => $"{bytes / 1_000_000} " + T("MB", "МБ");

    private long MissingEnginesBytes()
    {
        long sum = 0;
        if (!Has(Engine.FFmpeg)) sum += Engines.ApproximateBytes(Engine.FFmpeg);
        if (!Has(Engine.Magick)) sum += Engines.ApproximateBytes(Engine.Magick);
        if (!Has(Engine.Pandoc)) sum += Engines.ApproximateBytes(Engine.Pandoc);
        return sum;
    }

    private UIElement ModePage()
    {
        var panel = new StackPanel { Margin = new Thickness(28, 12, 28, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = _installed is null
                ? T("Choose how to install.", "Выбери способ установки.")
                : T($"Radii {Installer.InstalledVersion()} is already installed. This will update it.", $"Radii {Installer.InstalledVersion()} уже установлен. Установка обновит его."),
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var auto = ModeCard(
            T("Automatic (recommended)", "Автоматическая (рекомендуется)"),
            T($"Just wait. Installs the program, starts it with Windows, adds a Start menu shortcut and downloads the conversion engines (FFmpeg, ImageMagick, Pandoc — about {Mb(MissingEnginesBytes())}). No questions asked.",
              $"Просто жди. Ставит программу, запуск вместе с Windows, ярлык в меню «Пуск» и скачивает движки конвертации (FFmpeg, ImageMagick, Pandoc — около {Mb(MissingEnginesBytes())}). Никаких вопросов."),
            _auto);
        var manual = ModeCard(
            T("Manual", "Ручная"),
            T("Choose the folder, shortcuts and which conversion engines to download now. Skipped engines are downloaded automatically the first time they are needed.",
              "Выбери папку, ярлыки и какие движки конвертации скачать сейчас. Пропущенные движки скачаются сами при первом использовании."),
            !_auto);
        ((RadioButton)auto.Child).Checked += (_, _) => _auto = true;
        ((RadioButton)manual.Child).Checked += (_, _) => _auto = false;
        panel.Children.Add(auto);
        panel.Children.Add(manual);
        return panel;
    }

    private Border ModeCard(string title, string text, bool selected)
    {
        var content = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
        content.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        var radio = new RadioButton { GroupName = "mode", IsChecked = selected, Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(8, 6, 8, 6) };
        return new Border
        {
            Child = radio,
            Background = CardFill,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 10),
        };
    }

    private UIElement OptionsPage()
    {
        var panel = new StackPanel { Margin = new Thickness(28, 6, 28, 0) };

        panel.Children.Add(Label(T("Install folder", "Папка установки")));
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 10) };
        var browse = new Button { Content = T("Browse…", "Обзор…"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        DockPanel.SetDock(browse, Dock.Right);
        _dirBox = new TextBox { Text = _options.Dir, VerticalContentAlignment = VerticalAlignment.Center };
        browse.Click += (_, _) =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = _dirBox.Text, UseDescriptionForTitle = true, Description = T("Install folder", "Папка установки") };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                _dirBox.Text = Path.Combine(dialog.SelectedPath, dialog.SelectedPath.EndsWith("Radii", StringComparison.OrdinalIgnoreCase) ? "" : "Radii");
        };
        row.Children.Add(browse);
        row.Children.Add(_dirBox);
        panel.Children.Add(row);

        panel.Children.Add(Check(T("Start with Windows", "Запускать вместе с Windows"), _options.Autostart, v => _options.Autostart = v));
        panel.Children.Add(Check(T("Start menu shortcut", "Ярлык в меню «Пуск»"), _options.StartMenu, v => _options.StartMenu = v));
        panel.Children.Add(Check(T("Desktop shortcut", "Ярлык на рабочем столе"), _options.Desktop, v => _options.Desktop = v));

        panel.Children.Add(Label(T("Conversion engines to download now", "Движки конвертации — скачать сейчас"), new Thickness(0, 14, 0, 4)));
        _engineBoxes.Clear();
        EngineRow(panel, "FFmpeg", T("video and audio", "видео и аудио"), Engines.ApproximateBytes(Engine.FFmpeg), Has(Engine.FFmpeg), _options.FFmpeg, v => _options.FFmpeg = v);
        EngineRow(panel, "ImageMagick", T("WebP, AVIF, HEIC, RAW and other pictures", "WebP, AVIF, HEIC, RAW и другие картинки"), Engines.ApproximateBytes(Engine.Magick), Has(Engine.Magick), _options.Magick, v => _options.Magick = v);
        EngineRow(panel, "Pandoc", T("Markdown, HTML, EPUB, text documents", "Markdown, HTML, EPUB, текстовые документы"), Engines.ApproximateBytes(Engine.Pandoc), Has(Engine.Pandoc), _options.Pandoc, v => _options.Pandoc = v);
        panel.Children.Add(new TextBlock
        {
            Text = T("Word, Excel and PowerPoint files are converted by Microsoft Office when it is installed; without it LibreOffice is downloaded the first time such a file is converted.",
                     "Файлы Word, Excel и PowerPoint конвертирует установленный Microsoft Office; без него LibreOffice скачается при первой такой конвертации."),
            Foreground = Muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        _total.Foreground = Muted;
        _total.Margin = new Thickness(0, 8, 0, 0);
        _total.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(_total);
        UpdateTotal();
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
    }

    private TextBlock Label(string text, Thickness? margin = null) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = margin ?? new Thickness(0) };

    private static CheckBox Check(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 2, 0, 2) };
        box.Checked += (_, _) => set(true);
        box.Unchecked += (_, _) => set(false);
        return box;
    }

    private void EngineRow(StackPanel panel, string name, string note, long bytes, bool installed, bool value, Action<bool> set)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
        line.Children.Add(new TextBlock
        {
            Text = installed ? "  —  " + T("already installed", "уже установлен") : "  —  " + note + "  ·  " + Mb(bytes),
            Foreground = Muted,
        });
        var box = new CheckBox { Content = line, IsChecked = installed || value, IsEnabled = !installed, Margin = new Thickness(0, 2, 0, 2) };
        box.Checked += (_, _) => { set(true); UpdateTotal(); };
        box.Unchecked += (_, _) => { set(false); UpdateTotal(); };
        panel.Children.Add(box);
        _engineBoxes.Add((box, installed ? 0 : bytes));
    }

    private void UpdateTotal()
    {
        var bytes = _engineBoxes.Where(b => b.Box.IsChecked == true).Sum(b => b.Bytes);
        _total.Text = bytes == 0
            ? T("No engines will be downloaded now; each one is fetched the first time a conversion needs it.", "Сейчас ничего не скачается; каждый движок подтянется при первой конвертации, которой он нужен.")
            : T($"To download: about {Mb(bytes)}.", $"Будет скачано: около {Mb(bytes)}.");
    }

    private UIElement ProgressPage()
    {
        var panel = new StackPanel { Margin = new Thickness(28, 36, 28, 0) };
        panel.Children.Add(new TextBlock { Text = T("Installing…", "Устанавливаю…"), FontSize = 18, FontWeight = FontWeights.SemiBold });
        _status.Foreground = Muted;
        _status.Margin = new Thickness(0, 14, 0, 10);
        _status.Text = T("Copying files…", "Копирую файлы…");
        panel.Children.Add(_status);
        panel.Children.Add(_progress);
        _percent.Foreground = Muted;
        _percent.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_percent);
        return panel;
    }

    private UIElement DonePage()
    {
        var panel = new StackPanel { Margin = new Thickness(28, 20, 28, 0) };
        panel.Children.Add(new TextBlock { Text = T("Done", "Готово"), FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = _summary, TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 10, 0, 14) });
        panel.Children.Add(new TextBlock
        {
            Text = T("Right-click a file in Explorer to see the ring; Shift + right click opens the converter. The tray icon opens settings.", "Правый клик по файлу в Проводнике откроет круг; Shift + правый клик — конвертер. Значок в трее открывает настройки."),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });
        panel.Children.Add(Check(T("Run Radii now", "Запустить Radii сейчас"), _options.Launch, v => _options.Launch = v));
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
    }

    private async Task NextAsync()
    {
        switch (_page)
        {
            case Page.Mode when _auto:
                await StartInstallAsync();
                break;
            case Page.Mode:
                Show(Page.Options);
                break;
            case Page.Options:
                if (string.IsNullOrWhiteSpace(_dirBox.Text) || !Path.IsPathFullyQualified(_dirBox.Text))
                {
                    MessageBox.Show(T("Enter a full path for the install folder.", "Укажи полный путь к папке установки."), "Radii", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _options.Dir = Path.GetFullPath(_dirBox.Text.Trim());
                await StartInstallAsync();
                break;
            case Page.Done:
                if (_options.Launch && _failed is null)
                    Installer.Launch(_options.Dir);
                _working = false;
                Close();
                break;
        }
    }

    private string? _failed;

    private async Task StartInstallAsync()
    {
        if (_auto)
        {
            // Automatic mode ignores whatever the manual page held.
            var keep = _options.Dir;
            var launch = _options.Launch;
            _options.Autostart = _options.StartMenu = _options.FFmpeg = _options.Magick = _options.Pandoc = true;
            _options.Desktop = false;
            _options.Dir = _installed ?? keep;
            _options.Launch = launch;
        }
        Show(Page.Progress);
        _working = true;
        _cancel.Content = T("Cancel", "Отмена");
        var lines = new List<string>();
        try
        {
            _status.Text = T("Copying files…", "Копирую файлы…");
            _progress.IsIndeterminate = true;
            await Task.Run(() => Installer.InstallApp(_options));
            lines.Add(T($"Installed to {_options.Dir}", $"Установлено в {_options.Dir}"));
            _progress.IsIndeterminate = false;

            try
            {
                var failures = await Installer.InstallEnginesAsync(_options, _cts.Token, (text, fraction) => Dispatcher.BeginInvoke(() =>
                {
                    if (text.Length > 0)
                        _status.Text = text;
                    if (fraction is { } f)
                    {
                        _progress.IsIndeterminate = false;
                        _progress.Value = f;
                        _percent.Text = $"{(int)(f * 100)}%";
                    }
                    else
                    {
                        _progress.IsIndeterminate = true;
                    }
                }));
                if (failures.Count == 0)
                    lines.Add(T("Conversion engines: ready", "Движки конвертации: готово"));
                else
                    lines.Add(T("Some engines could not be downloaded (they will be fetched the first time they are needed):\n", "Часть движков не скачалась (они скачаются сами при первом использовании):\n") + string.Join("\n", failures.Select(f => "• " + f)));
            }
            catch (OperationCanceledException)
            {
                lines.Add(T("Engine download cancelled; they will be fetched the first time they are needed.", "Скачивание движков отменено; они скачаются сами при первом использовании."));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _failed = ex.Message;
            lines.Add(T("Installation failed: ", "Установка не удалась: ") + ex.Message);
        }
        _working = false;
        _summary = string.Join("\n\n", lines);
        Show(Page.Done);
    }
}
