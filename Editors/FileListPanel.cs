using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static Radii.Menus;

namespace Radii.Editors;

/// <summary>One file in a <see cref="FileListPanel"/>; editors derive from it to carry their own facts (pages, duration).</summary>
internal class ListEntry(string path)
{
    public string Path { get; } = path;

    /// <summary>Second line of the row ("4 pages", "03:12").</summary>
    public string? Detail { get; set; }

    /// <summary>When set, the row is shown in red with this text and the editor refuses to save.</summary>
    public string? Error { get; set; }

    public ImageSource? Thumbnail { get; set; }

    public bool Ready { get; set; }
}

/// <summary>A bordered list of files that can be reordered by dragging the handle or with the arrows, and trimmed with the cross.</summary>
internal sealed class FileListPanel : Border
{
    private const string DragFormat = "radii-list-row";

    private readonly StackPanel _rows = new();
    private readonly Brush _muted;
    private readonly Brush _line;
    private readonly Brush _accent;
    private readonly Brush _danger;
    private readonly bool _thumbnails;

    public FileListPanel(Brush panel, Brush line, Brush muted, Brush accent, Brush danger, bool thumbnails)
    {
        _muted = muted;
        _line = line;
        _accent = accent;
        _danger = danger;
        _thumbnails = thumbnails;
        Background = panel;
        BorderBrush = line;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        MaxHeight = 150; // the card follows its rows; longer lists scroll
        Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _rows };
    }

    /// <summary>Top line of the list editors: the "Add files" button and a quiet hint beside it.</summary>
    public static Grid Toolbar(Button add, string hint, Brush muted)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        add.Padding = new Thickness(10, 3, 10, 3);
        add.FontSize = 13;
        add.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(add);
        // The long "drag here" hint is gone on purpose: the tooltip of the button carries it.
        if (hint.Length > 0)
            add.ToolTip = hint;
        return grid;
    }

    public List<ListEntry> Entries { get; } = [];

    /// <summary>The list changed (added, moved or removed) or an entry finished loading.</summary>
    public event Action? Changed;

    public void Add(ListEntry entry)
    {
        Entries.Add(entry);
        Refresh();
    }

    public void Refresh()
    {
        _rows.Children.Clear();
        for (var i = 0; i < Entries.Count; i++)
            _rows.Children.Add(Row(Entries[i], i));
        Changed?.Invoke();
    }

    private Border Row(ListEntry entry, int index)
    {
        var font = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        Button Tool(string glyph, string tip, Action action, bool enabled = true)
        {
            var button = new Button
            {
                Content = new TextBlock { Text = glyph, FontFamily = font, FontSize = 12 },
                ToolTip = tip,
                Width = 24,
                Height = 24,
                Margin = new Thickness(1, 0, 0, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                IsEnabled = enabled,
                Visibility = enabled ? Visibility.Visible : Visibility.Hidden,
            };
            button.Click += (_, _) => action();
            return button;
        }

        var grip = new TextBlock { Text = "⋮⋮", Foreground = _muted, FontSize = 13, Cursor = Cursors.SizeAll, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Background = Brushes.Transparent };
        grip.MouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragDrop.DoDragDrop(grip, new DataObject(DragFormat, index), DragDropEffects.Move);
        };

        var name = new TextBlock { Text = System.IO.Path.GetFileName(entry.Path), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = entry.Path, VerticalAlignment = VerticalAlignment.Center };
        var detailText = entry.Error ?? (entry.Ready ? entry.Detail ?? "" : T("Reading…", "Читаю…"));
        var detail = new TextBlock
        {
            Text = detailText,
            ToolTip = detailText,
            Foreground = entry.Error is null ? _muted : _danger,
            FontSize = 12,
            MaxWidth = 190,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 6, 0),
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Tool("", T("Move up", "Выше"), () => Move(index, index - 1), index > 0),
                Tool("", T("Move down", "Ниже"), () => Move(index, index + 1), index < Entries.Count - 1),
                Tool("", T("Remove", "Убрать"), () => Remove(index)),
            },
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(grip);
        if (_thumbnails)
        {
            var thumb = new Border
            {
                Width = 24,
                Height = 32,
                Background = Brushes.White,
                BorderBrush = _line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 10, 0),
                Child = entry.Thumbnail is null ? null : new Image { Source = entry.Thumbnail, Stretch = Stretch.Uniform },
            };
            Grid.SetColumn(thumb, 1);
            grid.Children.Add(thumb);
        }
        else
        {
            var tint = _accent.Clone();
            tint.Opacity = 0.16;
            var icon = new Border
            {
                Width = 24,
                Height = 24,
                Background = tint,
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock { Text = "", FontFamily = font, FontSize = 14, Foreground = entry.Error is null ? _accent : _danger, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Grid.SetColumn(icon, 1);
            grid.Children.Add(icon);
        }
        Grid.SetColumn(name, 2);
        grid.Children.Add(name);
        Grid.SetColumn(detail, 3);
        grid.Children.Add(detail);
        Grid.SetColumn(buttons, 4);
        grid.Children.Add(buttons);

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(10, 4, 6, 4),
            BorderBrush = _line,
            BorderThickness = new Thickness(0, 0, 0, index < Entries.Count - 1 ? 1 : 0),
            Background = Brushes.Transparent,
            AllowDrop = true,
        };
        var wash = _accent.Clone();
        wash.Opacity = 0.18;
        row.DragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DragFormat))
                return;
            e.Effects = DragDropEffects.Move;
            row.Background = wash;
            e.Handled = true;
        };
        row.DragLeave += (_, _) => row.Background = Brushes.Transparent;
        row.Drop += (_, e) =>
        {
            row.Background = Brushes.Transparent;
            if (e.Data.GetData(DragFormat) is int from)
            {
                Move(from, index);
                e.Handled = true;
            }
        };
        return row;
    }

    private void Move(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Entries.Count || to >= Entries.Count)
            return;
        var entry = Entries[from];
        Entries.RemoveAt(from);
        Entries.Insert(to, entry);
        Refresh();
    }

    private void Remove(int index)
    {
        if (index < 0 || index >= Entries.Count)
            return;
        Entries.RemoveAt(index);
        Refresh();
    }
}
