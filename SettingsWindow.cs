using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using static Radii.Menus;

namespace Radii;

/// <summary>The tray's mini interface: every option, own commands, version and author. Changes apply immediately.</summary>
internal sealed class SettingsWindow : Window
{
    private const string AutostartKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static SettingsWindow? _instance;

    private readonly bool _dark = Theme.SystemDark();
    private readonly StackPanel _page = new() { Margin = new Thickness(20, 16, 20, 24) };
    private readonly ListBox _commandList = new() { MinHeight = 64, MaxHeight = 160 };

    public static void ShowOrActivate()
    {
        if (_instance is not null)
        {
            _instance.Activate();
            return;
        }
        _instance = new SettingsWindow();
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
        _instance.Activate();
    }

    /// <summary>Offscreen render of the whole page, for <c>--snapshot-settings</c>.</summary>
    public static int RenderTo(string path)
    {
        var window = new SettingsWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0, ShowActivated = false };
        window._page.Background = window.Background;
        window.Show();
        window.UpdateLayout();
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => { }));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window._page.ActualWidth + 40, (int)window._page.ActualHeight + 40, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window._page);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
            encoder.Save(file);
        window.Close();
        return 0;
    }

    private SettingsWindow()
    {
        Title = "Radii";
        Width = 520;
        Height = 760;
        MinWidth = 420;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush(_dark ? "#202020" : "#F3F3F3");
        Foreground = Brush(_dark ? "#FFFFFF" : "#1B1B1B");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _page };
        Build();
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    private Brush Muted => Brush(_dark ? "#B0B0B0" : "#616161");

    private void Build()
    {
        var settings = Settings.Current;
        var version = (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

        _page.Children.Add(new TextBlock { Text = "Radii", FontSize = 26, FontWeight = FontWeights.SemiBold });
        _page.Children.Add(new TextBlock { Text = T("Radial context menu for Explorer and the desktop", "Круговое контекстное меню для Проводника и рабочего стола"), Foreground = Muted, Margin = new Thickness(0, 2, 0, 8) });

        Section(T("General", "Общие"));
        Card(
            Toggle(T("Radial menu enabled", "Круговое меню включено"), T("Off: the native Windows menu comes back", "Выключено: возвращается обычное меню Windows"), () => settings.Enabled, v => settings.Enabled = v),
            Toggle(T("Start with Windows", "Запускать вместе с Windows"), null, IsAutostart, SetAutostart),
            Combo(T("Language", "Язык"), [("auto", T("System", "Как в системе")), ("ru", "Русский"), ("en", "English")], () => settings.Language, v => settings.Language = v));

        Section(T("Behaviour", "Поведение"));
        Card(
            Toggle(T("Sort sectors by usage", "Сортировать секторы по частоте"), T("The first sector keeps its place", "Первый сектор остаётся на месте"), () => settings.SortByUsage, v => settings.SortByUsage = v));

        Section(T("Converter and editors", "Конвертер и правка"));
        Card(
            Toggle(T("Shift + right click opens the converter", "Shift + ПКМ открывает конвертер"), T("Off: Shift + right click shows the native Windows menu again", "Выключено: Shift + ПКМ снова показывает обычное меню Windows"), () => settings.ShiftConvert, v => settings.ShiftConvert = v),
            Toggle(T("Ctrl + right click opens the editors", "Ctrl + ПКМ открывает правку"), T("Crop a photo, trim audio, merge PDFs. Off: Ctrl + right click opens the usual ring", "Кроп фото, обрезка аудио, объединение PDF. Выключено: Ctrl + ПКМ открывает обычное кольцо"), () => settings.CtrlEdit, v => settings.CtrlEdit = v),
            Toggle(T("Show the two pills beside the ring", "Показывать две плашки у кольца"), T("A reminder of the shortcuts; you can also click them or release the button over them", "Напоминание о сочетаниях; на них можно нажать или отпустить кнопку над ними"), () => settings.ModeChips, v => settings.ModeChips = v),
            Info(T("How", "Как"), T("Point at a file, hold Shift or Ctrl and right-click: the ring shows the formats it can be converted to, or the editors for it. Engines (FFmpeg, ImageMagick, Pandoc) are downloaded on first use.", "Наведи на файл, зажми Shift или Ctrl и нажми ПКМ: кольцо покажет форматы для конвертации или редакторы для этого файла. Движки (FFmpeg, ImageMagick, Pandoc) скачиваются при первом использовании.")));

        Section(T("Look", "Вид"));
        Card(
            Combo(T("Theme", "Тема"), [("system", T("System", "Как в системе")), ("dark", T("Dark", "Тёмная")), ("light", T("Light", "Светлая"))], () => settings.Theme, v => settings.Theme = v),
            Scale(T("Ring size", "Размер кольца")),
            Toggle(T("Blurred background", "Размытый фон"), T("Frosted-glass look behind the sectors", "Эффект матового стекла за секторами"), () => settings.Acrylic, v => settings.Acrylic = v),
            Toggle(T("Animations", "Анимации"), null, () => settings.Animations, v => settings.Animations = v));

        _page.Children.Add(new Expander
        {
            Header = T("Sectors (show or hide each one)", "Секторы (показать или скрыть каждый)"),
            Margin = new Thickness(0, 18, 0, 0),
            Content = MakeCard(Menus.Builtins.Select(b => Toggle(T(b.En, b.Ru), null, () => !settings.HiddenItems.Contains(b.Id), visible =>
            {
                settings.HiddenItems.Remove(b.Id);
                if (!visible)
                    settings.HiddenItems.Add(b.Id);
            })).ToArray()),
        });

        Section(T("Your commands", "Свои команды"));
        RefreshCommands();
        var add = new Button { Content = T("Add…", "Добавить…"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 4, 14, 4) };
        add.Click += (_, _) =>
        {
            if (new CommandDialog { Owner = this }.Ask() is { } command)
            {
                settings.Commands.RemoveAll(c => c.Name == command.Name);
                settings.Commands.Add(command);
                Settings.Notify();
                RefreshCommands();
            }
        };
        var remove = new Button { Content = T("Remove", "Удалить"), Padding = new Thickness(14, 4, 14, 4) };
        remove.Click += (_, _) =>
        {
            if (_commandList.SelectedIndex >= 0)
            {
                settings.Commands.RemoveAt(_commandList.SelectedIndex);
                Settings.Notify();
                RefreshCommands();
            }
        };
        var hint = new TextBlock
        {
            Text = T("Placeholders: {path} {paths} {folder} {name}", "Подстановки: {path} {paths} {folder} {name}"),
            Foreground = Muted,
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 8),
        };
        Card(_commandList, hint, new StackPanel { Orientation = Orientation.Horizontal, Children = { add, remove } });

        Section(T("About", "О программе"));
        var open = new Button { Content = T("Settings folder", "Папка настроек"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 4, 14, 4) };
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(Settings.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Settings.Folder}\"") { UseShellExecute = true });
        };
        var reset = new Button { Content = T("Reset usage", "Сбросить статистику"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 4, 14, 4) };
        reset.Click += (_, _) => Settings.ResetUsage();
        var exit = new Button { Content = T("Exit Radii", "Выйти из Radii"), Padding = new Thickness(14, 4, 14, 4) };
        exit.Click += (_, _) => Application.Current.Shutdown();
        var check = new Button { Content = T("Check for updates", "Проверить обновление"), Padding = new Thickness(14, 4, 14, 4) };
        check.Click += async (_, _) => await Updates.Updater.CheckNowAsync();
        Card(
            Info(T("Version", "Версия"), version),
            Toggle(T("Check for updates automatically", "Проверять обновления автоматически"), T("At start and once a day: one request to GitHub Releases, nothing else is sent", "При запуске и раз в сутки: один запрос к GitHub Releases, больше ничего не отправляется"), () => settings.CheckUpdates, v => settings.CheckUpdates = v),
            Row(T("Updates", "Обновления"), null, check),
            Info(T("Author", "Автор"), "TaCeiN"),
            Info(T("Native menu", "Обычное меню"), T("Entry in the Extensions flyout (or turn off Shift + right click = converter)", "Пункт в списке расширений (или выключи «Shift + ПКМ = конвертер»)")),
            new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Children = { open, reset, exit } });
    }

    private void Section(string title) =>
        _page.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 18, 0, 8) });

    private void Card(params UIElement[] rows) => _page.Children.Add(MakeCard(rows));

    private Border MakeCard(UIElement[] rows)
    {
        var stack = new StackPanel();
        foreach (var row in rows)
            stack.Children.Add(row);
        return new Border
        {
            Background = Brush(_dark ? "#2B2B2B" : "#FFFFFF"),
            BorderBrush = Brush(_dark ? "#38FFFFFF" : "#14000000"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 8, 0, 0),
            Child = stack,
        };
    }

    private UIElement Row(string title, string? description, UIElement control)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (description is not null)
            text.Children.Add(new TextBlock { Text = description, Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn((FrameworkElement)control, 1);
        ((FrameworkElement)control).VerticalAlignment = VerticalAlignment.Center;
        ((FrameworkElement)control).Margin = new Thickness(16, 0, 0, 0);
        grid.Children.Add(control);
        return grid;
    }

    private UIElement Toggle(string title, string? description, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox { IsChecked = get(), MinWidth = 0 };
        box.Click += (_, _) =>
        {
            set(box.IsChecked == true);
            Settings.Notify();
        };
        return Row(title, description, box);
    }

    private UIElement Combo(string title, (string Value, string Label)[] options, Func<string> get, Action<string> set)
    {
        var combo = new ComboBox { MinWidth = 150 };
        foreach (var (value, label) in options)
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        combo.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => o.Value == get()));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string value })
            {
                set(value);
                Settings.Notify();
            }
        };
        return Row(title, null, combo);
    }

    private UIElement Scale(string title)
    {
        var settings = Settings.Current;
        var slider = new Slider { Minimum = 0.7, Maximum = 1.6, Value = settings.RingScale, Width = 170, TickFrequency = 0.1, IsSnapToTickEnabled = true };
        var value = new TextBlock { Text = $"{settings.RingScale:P0}", Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) =>
        {
            settings.RingScale = Math.Round(e.NewValue, 2);
            value.Text = $"{settings.RingScale:P0}";
            Settings.Notify();
        };
        return Row(title, null, new StackPanel { Orientation = Orientation.Horizontal, Children = { slider, value } });
    }

    private UIElement Info(string title, string value) =>
        Row(title, null, new TextBlock { Text = value, Foreground = Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 280, TextAlignment = TextAlignment.Right });

    private void RefreshCommands()
    {
        _commandList.Items.Clear();
        foreach (var command in Settings.Current.Commands)
            _commandList.Items.Add($"{command.Name}  —  {command.Command} {command.Arguments}".TrimEnd());
        if (_commandList.Items.Count == 0)
            _commandList.Items.Add(T("No commands yet", "Команд пока нет"));
    }

    private static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutostartKey);
        return key?.GetValue("Radii") is string;
    }

    private static void SetAutostart(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutostartKey, writable: true);
        if (key is null)
            return;
        if (on && Environment.ProcessPath is { } path)
            key.SetValue("Radii", $"\"{path}\"");
        else
            key.DeleteValue("Radii", throwOnMissingValue: false);
    }
}

/// <summary>Small modal form for one custom command.</summary>
internal sealed class CommandDialog : Window
{
    private static readonly (string Glyph, string En, string Ru)[] Glyphs =
    [
        ("", "Play", "Запуск"), ("", "Star", "Звезда"), ("", "Code", "Код"), ("", "Terminal", "Терминал"),
        ("", "Globe", "Сайт"), ("", "Document", "Документ"), ("", "Edit", "Правка"), ("", "Share", "Отправить"),
        ("", "Download", "Загрузка"), ("", "Folder", "Папка"), ("", "Settings", "Настройки"), ("", "Package", "Пакет"),
    ];

    private readonly TextBox _name = new();
    private readonly ComboBox _glyph = new();
    private readonly ComboBox _applies = new();
    private readonly TextBox _extensions = new();
    private readonly TextBox _command = new();
    private readonly TextBox _arguments = new();
    private readonly CheckBox _admin = new() { Content = T("Run as administrator", "От имени администратора") };
    private CustomCommand? _result;

    public CommandDialog()
    {
        Title = T("New command", "Новая команда");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var dark = Theme.SystemDark();
        Background = (SolidColorBrush)new BrushConverter().ConvertFromString(dark ? "#202020" : "#F3F3F3")!;
        Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(dark ? "#FFFFFF" : "#1B1B1B")!;

        foreach (var (glyph, en, ru) in Glyphs)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Width = 28 });
            line.Children.Add(new TextBlock { Text = T(en, ru) });
            _glyph.Items.Add(new ComboBoxItem { Content = line, Tag = glyph });
        }
        _glyph.SelectedIndex = 0;
        foreach (var (value, label) in new[] { ("any", T("Anything", "Всё")), ("file", T("Files", "Файлы")), ("folder", T("Folders", "Папки")), ("background", T("Empty space", "Пустое место")) })
            _applies.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        _applies.SelectedIndex = 0;

        var form = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        AddField(form, T("Name", "Название"), _name);
        AddField(form, T("Icon", "Иконка"), _glyph);
        AddField(form, T("Applies to", "Для чего"), _applies);
        AddField(form, T("Extensions (txt, md — empty: all)", "Расширения (txt, md — пусто: все)"), _extensions);
        AddField(form, T("Program", "Программа"), _command);
        AddField(form, T("Arguments, e.g. \"{path}\"", "Аргументы, например \"{path}\""), _arguments);
        form.Children.Add(_admin);

        var ok = new Button { Content = T("Add", "Добавить"), IsDefault = true, Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = T("Cancel", "Отмена"), IsCancel = true, Padding = new Thickness(18, 4, 18, 4) };
        form.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel } });
        Content = form;
    }

    private static void AddField(StackPanel form, string label, Control control)
    {
        form.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3), FontSize = 12 });
        form.Children.Add(control);
    }

    public CustomCommand? Ask()
    {
        ShowDialog();
        return _result;
    }

    private void Accept()
    {
        if (_name.Text.Trim().Length == 0 || _command.Text.Trim().Length == 0)
            return;
        _result = new CustomCommand
        {
            Name = _name.Text.Trim(),
            Glyph = (string)((ComboBoxItem)_glyph.SelectedItem).Tag,
            AppliesTo = (string)((ComboBoxItem)_applies.SelectedItem).Tag,
            Extensions = _extensions.Text.Trim(),
            Command = _command.Text.Trim(),
            Arguments = _arguments.Text.Trim(),
            Admin = _admin.IsChecked == true,
        };
        Close();
    }
}
