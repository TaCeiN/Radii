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
        _scroll.PreviewMouseWheel += OnWheel;
        Unloaded += (_, _) => StopSmoothing();
        Child = _scroll;
    }

    private readonly ScrollViewer _scroll;
    private double _target;
    private bool _smoothing;

    // Smaller step per wheel notch (36 DIP instead of 48 * 3), eased towards the target so touchpad
    // gestures (many tiny deltas) scroll fluidly instead of jumping.
    private void OnWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (_scroll.ScrollableHeight <= 0)
            return;
        e.Handled = true;
        if (!_smoothing)
            _target = _scroll.VerticalOffset;
        _target = Math.Clamp(_target - e.Delta * 0.3, 0, _scroll.ScrollableHeight);
        if (!_smoothing)
        {
            _smoothing = true;
            CompositionTarget.Rendering += Step;
        }
    }

    private void Step(object? sender, EventArgs e)
    {
        var current = _scroll.VerticalOffset;
        var next = current + (_target - current) * 0.22;
        if (Math.Abs(_target - next) < 0.4)
        {
            next = _target;
            StopSmoothing();
        }
        _scroll.ScrollToVerticalOffset(next);
    }

    private void StopSmoothing()
    {
        if (!_smoothing)
            return;
        _smoothing = false;
        CompositionTarget.Rendering -= Step;
    }

    public void Populate(IReadOnlyList<ShellEntry> entries)
    {
        _rows.Children.Clear();
        foreach (var entry in entries)
            _rows.Children.Add(Row(entry));
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
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (!entry.Disabled && !entry.HasChildren)
                _activate(entry);
        };
        return row;
    }
}
