using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static Radii.Menus;

namespace Radii.Updates;

/// <summary>The small "a new version is out, update?" window. Closing it any other way than "Update" means "later".</summary>
internal sealed class UpdateDialog : Window
{
    public bool Accepted { get; private set; }

    /// <summary>Offscreen render with sample data, for <c>--update-snapshot</c>.</summary>
    public static int RenderTo(string path)
    {
#pragma warning disable WPF0001
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
#pragma warning restore WPF0001
        var sample = new UpdateInfo(new Version(1, 1, 0), "", "What's new\n- Faster ring\n- Word to PDF through Office\n- Bug fixes", "", "", 0, null);
        var window = new UpdateDialog(sample, true) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0, ShowActivated = false };
        window.Show();
        window.UpdateLayout();
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => { }));
        var element = (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(window, 0);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = System.IO.File.Create(path))
            encoder.Save(file);
        window.Close();
        app.Shutdown();
        return 0;
    }

    public UpdateDialog(UpdateInfo update, bool canInstall)
    {
        var dark = Theme.SystemDark();
        Brush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        var muted = Brush(dark ? "#B0B0B0" : "#616161");

        Title = T("Radii update", "Обновление Radii");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush(dark ? "#202020" : "#F3F3F3");
        Foreground = Brush(dark ? "#FFFFFF" : "#1B1B1B");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        panel.Children.Add(new TextBlock { Text = T("A new version is available", "Доступна новая версия"), FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = T($"Radii {update.Version} (you have {Setup.Installer.Version}).", $"Radii {update.Version} (у вас {Setup.Installer.Version})."),
            Foreground = muted,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var notes = update.Notes.Replace("\r", "").Trim();
        if (notes.Length > 0)
        {
            if (notes.Length > 600)
                notes = notes[..600].TrimEnd() + "…";
            panel.Children.Add(new Border
            {
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(12, 8, 12, 8),
                CornerRadius = new CornerRadius(8),
                Background = Brush(dark ? "#2B2B2B" : "#FFFFFF"),
                Child = new ScrollViewer
                {
                    MaxHeight = 160,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = muted },
                },
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = canInstall
                ? T("It downloads in the corner of the screen, then Radii restarts. Settings are kept.", "Загрузка пойдёт в уголке экрана, затем Radii перезапустится. Настройки сохранятся.")
                : T("This copy was not installed by the setup, so the release page opens instead.", "Эта копия не ставилась установщиком, поэтому откроется страница релиза."),
            Foreground = muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        });

        var yes = new Button { Content = canInstall ? T("Update", "Обновить") : T("Open page", "Открыть страницу"), MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        if (TryFindResource("AccentButtonStyle") is Style accent)
            yes.Style = accent;
        var later = new Button { Content = T("Later", "Позже"), MinWidth = 90 };
        yes.Click += (_, _) =>
        {
            Accepted = true;
            Close();
        };
        later.Click += (_, _) => Close();
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { yes, later },
        });
        Content = panel;
    }
}
