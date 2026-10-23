using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Radii;

/// <summary>Windows 11 style flyout list used for shell extensions and their submenus.</summary>
internal sealed class FlyoutView : Border
{
    public const double ShadowMargin = 12;
    private const double RowHeight = 34;
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");

    private readonly Theme _theme;
    private readonly Action<ShellEntry, FlyoutView, FrameworkElement> _hover;
    private readonly Action<ShellEntry> _activate;
    private readonly StackPanel _rows = new();

    public FlyoutView(Theme theme, double maxHeight, Action<ShellEntry, FlyoutView, FrameworkElement> hover, Action<ShellEntry> activate)
    {
        _theme = theme;
        _hover = hover;
        _activate = activate;
        // The margin leaves room for the shadow inside the (transparent) window.
        Margin = new Thickness(ShadowMargin);
        Padding = new Thickness(4);
        MinWidth = 230;
        MaxWidth = 360;
        Background = theme.FlyoutBackground;
        BorderBrush = theme.Border;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = theme.Dark ? 0.5 : 0.25, Color = Colors.Black };
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = maxHeight,
            Content = _rows,
        };
        SmoothScroll.Attach(_scroll);
        Child = _scroll;
    }

    private readonly ScrollViewer _scroll;

    private readonly List<Border> _selectable = [];
    private Border? _selectedRow;

    /// <summary>Draw the entries as a grid of icon tiles (the extensions list) instead of rows. Set before <see cref="Populate"/>.</summary>
    public bool Tiles { get; set; }

    private const double TileWidth = 86;
    private const double TileHeight = 92;

    // Phrases that only say "this program opens it": the tile shows the program name.
    private static readonly string[] Lead =
    [
        "Открыть с помощью ", "Открыть в ", "Открыть как ", "Переименовать с помощью ", "Изменить с помощью ", "Open with ", "Open in ", "Open as ", "Edit with ", "Rename with ",
    ];

    private static string ShortLabel(string label)
    {
        foreach (var lead in Lead)
        {
            if (label.StartsWith(lead, StringComparison.OrdinalIgnoreCase) && label.Length > lead.Length)
                return label[lead.Length..];
        }
        return label;
    }

    // Handlers of the shell itself give the menu no picture; a Fluent glyph chosen by the words of the label is better than a letter.
    private static readonly (string[] Words, string Glyph)[] GlyphGuesses =
    [
        (["свойства", "properties"], ""),
        (["ярлык", "shortcut"], ""),
        (["закрепить", "открепить", "pin to", "unpin"], ""),
        (["поделиться", "share", "доступ"], ""),
        (["восстановить", "restore", "предыдущ"], ""),
        (["библиотек", "library"], ""),
        (["отправить", "send to"], ""),
        (["zip", "rar", "архив", "archive", "распаков", "extract"], ""),
        (["терминал", "terminal", "powershell", "bash", "cmd"], ""),
        (["создать", "new"], ""),
        (["переимен", "rename"], ""),
        (["печать", "print"], ""),
        (["открыть", "open"], ""),
        (["удалить", "delete"], ""),
        (["копир", "copy"], ""),
        (["сканир", "scan", "defender", "защитник"], ""),
        (["вставить", "paste"], ""),
    ];

    private static string? GuessGlyph(string label)
    {
        foreach (var (words, glyph) in GlyphGuesses)
        {
            foreach (var word in words)
            {
                if (label.Contains(word, StringComparison.OrdinalIgnoreCase))
                    return glyph;
            }
        }
        return null;
    }

    private void PopulateTiles(IReadOnlyList<ShellEntry> entries)
    {
        var leaves = entries.Where(e => !e.IsSeparator).ToList();
        var columns = leaves.Count <= 4 ? Math.Max(2, leaves.Count) : leaves.Count <= 9 ? 3 : 4;
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = columns };
        foreach (var entry in leaves)
            grid.Children.Add(Tile(entry));
        var width = columns * TileWidth + 10;
        MinWidth = width;
        MaxWidth = width;
        _scroll.Content = grid;
    }

    private FrameworkElement Tile(ShellEntry entry)
    {
        var plate = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(10), Background = _theme.RowHover, HorizontalAlignment = HorizontalAlignment.Center };
        var source = entry.BigIcon ?? entry.Icon;
        if (source is not null)
        {
            var size = entry.BigIcon is not null ? 26 : 20;
            var image = new Image { Source = source, Width = size, Height = size, Opacity = entry.Disabled ? 0.45 : 1 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            plate.Child = image;
        }
        else if (!string.IsNullOrEmpty(entry.Glyph) || GuessGlyph(entry.Label) is not null)
        {
            plate.Child = new TextBlock { Text = entry.Glyph ?? GuessGlyph(entry.Label), FontFamily = IconFont, FontSize = 18, Foreground = _theme.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        else
        {
            // No icon at all: the first letter on the plate.
            var letter = entry.Label.Trim().Length > 0 ? entry.Label.Trim()[..1].ToUpperInvariant() : "?";
            plate.Child = new TextBlock { Text = letter, FontFamily = TextFont, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = _theme.TextMuted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        var label = new TextBlock
        {
            Text = ShortLabel(entry.Label),
            FontFamily = TextFont,
            FontSize = 11.5,
            Foreground = entry.Disabled ? _theme.TextMuted : _theme.Text,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34,
            Margin = new Thickness(4, 5, 4, 0),
        };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        stack.Children.Add(plate);
        stack.Children.Add(label);
        if (entry.HasChildren)
        {
            // A submenu parent: a small chevron in the corner of the plate; hovering opens the list beside the grid.
            var chevron = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 9, Foreground = _theme.TextMuted, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 6, 0) };
            var host = new Grid { Height = TileHeight };
            host.Children.Add(stack);
            host.Children.Add(chevron);
            return TileShell(entry, host);
        }
        return TileShell(entry, stack);
    }

    private FrameworkElement TileShell(ShellEntry entry, UIElement content)
    {
        var tile = new Border
        {
            Width = TileWidth,
            Height = TileHeight,
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 8, 0, 0),
            Child = content,
            ToolTip = entry.Label,
            Opacity = entry.Disabled ? 0.6 : 1,
        };
        tile.MouseEnter += (_, _) =>
        {
            if (!entry.Disabled)
                tile.Background = _theme.RowHover;
            _hover(entry, this, tile);
        };
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        tile.MouseLeftButtonUp += (_, _) =>
        {
            if (!entry.Disabled && !entry.HasChildren)
                _activate(entry);
        };
        return tile;
    }

    public void Populate(IReadOnlyList<ShellEntry> entries)
    {
        if (Tiles)
        {
            PopulateTiles(entries);
            return;
        }
        _rows.Children.Clear();
        _selectable.Clear();
        _selectedRow = null;
        foreach (var entry in entries)
        {
            var row = Row(entry);
            _rows.Children.Add(row);
            if (row is Border { Child: Grid } border)
                _selectable.Add(border);
        }
    }

    /// <summary>Highlights the n-th row (separators not counted) for keyboard use; -1 clears it.</summary>
    public void SetSelected(int index)
    {
        if (_selectedRow is not null)
            _selectedRow.Background = Brushes.Transparent;
        _selectedRow = index >= 0 && index < _selectable.Count ? _selectable[index] : null;
        if (_selectedRow is null)
            return;
        _selectedRow.Background = _theme.RowHover;
        _selectedRow.BringIntoView();
    }

    private FrameworkElement Row(ShellEntry entry)
    {
        if (entry.IsSeparator)
            return new Border { Height = 1, Background = _theme.Border, Margin = new Thickness(8, 4, 8, 4) };

        var grid = new Grid { Height = RowHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (entry.Icon is not null)
        {
            grid.Children.Add(new Image { Source = entry.Icon, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = entry.Disabled ? 0.45 : 1 });
        }
        else if (!string.IsNullOrEmpty(entry.Glyph))
        {
            grid.Children.Add(new TextBlock { Text = entry.Glyph, FontFamily = IconFont, FontSize = 15, Foreground = entry.Disabled ? _theme.TextMuted : _theme.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        }
        else if (entry.Checked)
        {
            grid.Children.Add(new TextBlock { Text = "", FontFamily = IconFont, FontSize = 14, Foreground = _theme.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        }

        var label = new TextBlock
        {
            Text = entry.Label,
            FontFamily = TextFont,
            FontSize = 13,
            Foreground = entry.Disabled ? _theme.TextMuted : _theme.Text,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (entry.HasChildren)
        {
            var chevron = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 10, Foreground = _theme.TextMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) };
            Grid.SetColumn(chevron, 2);
            grid.Children.Add(chevron);
        }
        else if (!string.IsNullOrEmpty(entry.Hint))
        {
            var hint = new TextBlock { Text = entry.Hint, FontFamily = TextFont, FontSize = 12, Foreground = _theme.TextMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) };
            Grid.SetColumn(hint, 2);
            grid.Children.Add(hint);
        }

        var row = new Border { CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Child = grid, Opacity = entry.Disabled ? 0.6 : 1 };
        row.MouseEnter += (_, _) =>
        {
            if (!entry.Disabled)
                row.Background = _theme.RowHover;
            _hover(entry, this, row);
        };
        row.MouseLeave += (_, _) => row.Background = ReferenceEquals(row, _selectedRow) ? _theme.RowHover : Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (!entry.Disabled && !entry.HasChildren)
                _activate(entry);
        };
        return row;
    }
}
