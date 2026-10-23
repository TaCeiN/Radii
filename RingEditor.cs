using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static Radii.Menus;

namespace Radii;

/// <summary>
/// Visual editor of the rings: pick what was clicked (file, folder, picture, ...), see the real ring, click a sector and choose
/// which action sits there. Only actions that make sense for that context are offered. Saved at once into <see cref="Settings.Layouts"/>.
/// </summary>
internal sealed class RingEditorWindow : Window
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static RingEditorWindow? _instance;

    private readonly bool _dark = Theme.UserDark();
    private readonly Theme _theme;
    private readonly WrapPanel _contextChips = new();
    private readonly Grid _ringHost = new() { Width = RadialView.Size, Height = RadialView.Size };
    private readonly TextBlock _heading = new() { FontSize = 15, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _sub = new() { FontSize = 12, Margin = new Thickness(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _pool = new();
    private readonly Button _left = new(), _right = new(), _remove = new(), _add = new(), _reset = new();

    private string _key = "file";
    private Menus.RingPlan _plan = null!;
    private List<RadialItem> _ring = [];
    private int _selected;
    private RadialView? _view;

    public static void ShowOrActivate(Window? owner = null)
    {
        if (_instance is not null)
        {
            _instance.Activate();
            return;
        }
        _instance = new RingEditorWindow { Owner = owner };
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
        _instance.Activate();
    }

    /// <summary>Offscreen render for <c>--snapshot-ring-editor out.png [context] [selected sector]</c>.</summary>
    public static int RenderTo(string path, string? key, int selected)
    {
        var window = new RingEditorWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0, ShowActivated = false };
        window.SelectContext(key ?? "file");
        window._selected = selected;
        window.Refresh();
        window.Show();
        window.UpdateLayout();
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => { }));
        var root = (FrameworkElement)window.Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path))
            encoder.Save(file);
        window.Close();
        return 0;
    }

    private RingEditorWindow()
    {
        Title = T("Ring editor", "Редактор кольца");
        Width = 860;
        Height = 640;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush(Skin.Window(_dark));
        Foreground = Brush(Skin.Text(_dark));
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        // The ring in the editor is drawn flat (no screenshot behind it), so it is a little more opaque than the real one.
        _theme = new Theme(_dark, acrylic: false);

        foreach (var (key, en, ru) in Contexts)
        {
            var chip = new RadioButton { Content = T(en, ru), Tag = key, GroupName = "context", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 3, 10, 3), Style = (Style)FindResource("SkChip") };
            chip.Checked += (_, _) =>
            {
                if (_key != key || _plan is null)
                    SelectContext(key);
            };
            chip.IsChecked = key == _key;
            _contextChips.Children.Add(chip);
        }

        Content = BuildLayout();
        SelectContext(_key);
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    private Brush Muted => Brush(Skin.Muted(_dark));

    private UIElement BuildLayout()
    {
        _sub.Foreground = Muted;
        var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = T("What was right-clicked", "По чему кликнули"), FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 0, 0, 6) });
        top.Children.Add(_contextChips);
        root.Children.Add(top);

        var body = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _ringHost.HorizontalAlignment = HorizontalAlignment.Left;
        _ringHost.VerticalAlignment = VerticalAlignment.Top;
        body.Children.Add(Card(_ringHost, new Thickness(0)));

        var side = new Grid { Margin = new Thickness(16, 0, 0, 0) };
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new StackPanel();
        header.Children.Add(_heading);
        header.Children.Add(_sub);
        side.Children.Add(header);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _pool };
        SmoothScroll.Attach(scroll);
        Grid.SetRow(scroll, 1);
        side.Children.Add(scroll);
        Grid.SetColumn(side, 1);
        body.Children.Add(side);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        Style(_left, "", T("Move sector counter-clockwise", "Сдвинуть сектор против часовой"), () => Move(-1));
        Style(_right, "", T("Move sector clockwise", "Сдвинуть сектор по часовой"), () => Move(1));
        Style(_remove, "", T("Remove sector", "Убрать сектор"), Remove);
        Style(_add, "", T("Add a sector", "Добавить сектор"), Add);
        _reset.Content = T("Reset this ring", "Сбросить это кольцо");
        _reset.Padding = new Thickness(14, 4, 14, 4);
        _reset.Click += (_, _) => Reset();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var button in new[] { _left, _right, _remove, _add })
            buttons.Children.Add(button);
        buttons.Children.Add(new Border { Width = 16 });
        buttons.Children.Add(_reset);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        return new Border { Background = Background, Child = root };
    }

    private void Style(Button button, string glyph, string tip, Action click)
    {
        button.Content = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 15 };
        button.ToolTip = tip;
        button.Width = 40;
        button.Height = 32;
        button.Margin = new Thickness(0, 0, 6, 0);
        button.Click += (_, _) => click();
    }

    private Border Card(UIElement child, Thickness padding) => new()
    {
        Background = Brush(Skin.Card(_dark)),
        BorderBrush = Brush(Skin.Line(_dark)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = padding,
        Child = child,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private void SelectContext(string key)
    {
        _key = key;
        foreach (RadioButton chip in _contextChips.Children)
            chip.IsChecked = (string)chip.Tag == key;
        var (detected, kind) = SampleFor(key);
        _plan = Plan(detected, kind, sample: true);
        _ring = [.. _plan.Ring];
        _selected = 0;
        Refresh();
    }

    private bool ExtensionsSelected => _selected >= _ring.Count;

    private void Refresh()
    {
        var title = Contexts.First(c => c.Key == _key);
        var items = new List<RadialItem>(_ring) { Extensions() };
        _selected = Math.Clamp(_selected, 0, items.Count - 1);
        var view = new RadialView(items, _theme, T(title.En, title.Ru), new ViewOptions(false, null));
        view.SetHover(_selected);
        view.MouseMove += (_, e) =>
        {
            var hover = view.HitTest(e.GetPosition(view));
            view.SetHover(hover >= 0 ? hover : _selected);
        };
        view.MouseLeave += (_, _) => view.SetHover(_selected);
        view.MouseLeftButtonUp += (_, e) =>
        {
            var index = view.HitTest(e.GetPosition(view));
            if (index < 0)
                return;
            _selected = index;
            view.SetHover(index);
            RefreshPool();
        };
        _view = view;
        _ringHost.Children.Clear();
        _ringHost.Children.Add(view);
        RefreshPool();
    }

    private void RefreshPool()
    {
        var count = _ring.Count;
        _left.IsEnabled = !ExtensionsSelected && _selected > 0;
        _right.IsEnabled = !ExtensionsSelected && _selected < count - 1;
        _remove.IsEnabled = !ExtensionsSelected && count > 2;
        _add.IsEnabled = count < MaxSectors - 1 && _plan.Pool.Any(p => _ring.All(r => r.Id != p.Id));
        _pool.Children.Clear();

        if (ExtensionsSelected)
        {
            _heading.Text = T("Extensions", "Расширения");
            _sub.Text = T("This sector is always last: it opens the list of the shell's own commands (7-Zip, Git, Open with, Properties...).", "Этот сектор всегда последний: он открывает список команд оболочки (7-Zip, Git, «Открыть с помощью», «Свойства»…).");
            return;
        }

        _heading.Text = T($"Sector {_selected + 1}: ", $"Сектор {_selected + 1}: ") + _ring[_selected].Label;
        _sub.Text = T("Pick what this sector does. Everything is searchable by typing while the ring is open, even what is not on the ring.", "Выбери, что делает этот сектор. Всё, чего нет на кольце, находится поиском: просто печатай при открытом кольце.");
        foreach (var item in _plan.Pool)
            _pool.Children.Add(PoolRow(item));
    }

    private UIElement PoolRow(RadialItem item)
    {
        var position = _ring.FindIndex(r => r.Id == item.Id);
        var current = position == _selected;
        var grid = new Grid { Height = 38 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var textual = item.Glyph.Length != 1 || char.IsAsciiLetterOrDigit(item.Glyph[0]);
        grid.Children.Add(new TextBlock
        {
            Text = item.Glyph,
            FontFamily = textual ? FontFamily : IconFont,
            FontSize = textual ? 12 : 17,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var label = new TextBlock { Text = item.Label, VerticalAlignment = VerticalAlignment.Center, Foreground = item.Danger ? Brush("#E5604F") : Foreground };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        var state = new TextBlock
        {
            Text = current ? T("here", "здесь") : position >= 0 ? T($"sector {position + 1}", $"сектор {position + 1}") : T("not on the ring", "вне кольца"),
            Foreground = Muted,
            FontSize = 12,
            Margin = new Thickness(8, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(state, 2);
        grid.Children.Add(state);

        var accent = new SolidColorBrush(Skin.Accent(_dark)) { Opacity = 0.28 };
        var rest = current ? accent : Brushes.Transparent;
        var hover = Brush(_dark ? "#1AFFFFFF" : "#12000000");
        var row = new Border { CornerRadius = new CornerRadius(6), Background = rest, Child = grid, Margin = new Thickness(0, 0, 8, 2), Cursor = Cursors.Hand };
        row.MouseEnter += (_, _) => row.Background = current ? accent : hover;
        row.MouseLeave += (_, _) => row.Background = rest;
        row.MouseLeftButtonUp += (_, _) => Assign(item);
        return row;
    }

    /// <summary>Puts the action into the selected sector; one that is already elsewhere on the ring swaps places with it.</summary>
    private void Assign(RadialItem item)
    {
        if (ExtensionsSelected)
            return;
        var existing = _ring.FindIndex(r => r.Id == item.Id);
        if (existing == _selected)
            return;
        if (existing >= 0)
            (_ring[existing], _ring[_selected]) = (_ring[_selected], _ring[existing]);
        else
            _ring[_selected] = item;
        Save();
    }

    private void Move(int step)
    {
        var target = _selected + step;
        if (ExtensionsSelected || target < 0 || target >= _ring.Count)
            return;
        (_ring[target], _ring[_selected]) = (_ring[_selected], _ring[target]);
        _selected = target;
        Save();
    }

    private void Remove()
    {
        if (ExtensionsSelected || _ring.Count <= 2)
            return;
        _ring.RemoveAt(_selected);
        _selected = Math.Min(_selected, _ring.Count - 1);
        Save();
    }

    private void Add()
    {
        var next = _plan.Pool.FirstOrDefault(p => _ring.All(r => r.Id != p.Id));
        if (next is null || _ring.Count >= MaxSectors - 1)
            return;
        _ring.Add(next);
        _selected = _ring.Count - 1;
        Save();
    }

    private void Reset()
    {
        Settings.Current.Layouts.Remove(_key);
        Settings.Notify();
        SelectContext(_key);
    }

    private void Save()
    {
        Settings.Current.Layouts[_key] = _ring.Select(r => r.Id).ToList();
        Settings.Notify();
        Refresh();
    }
}
