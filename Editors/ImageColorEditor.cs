using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Radii.Converters;
using static Radii.Menus;
using Path = System.IO.Path;
using WPath = System.Windows.Shapes.Path;

namespace Radii.Editors;

/// <summary>Colour grading for a photo, laid out like the iPhone editor: round tool icons, one slider for the chosen tool, a strip of looks.</summary>
internal sealed class ImageColorEditor : IEditor
{
    public string Caption => T("Colour grade", "Цвет и свет");

    public string Glyph => "";

    public bool Accepts(IReadOnlyList<string> paths) =>
        paths.Count == 1 && ImageCropEditor.Inputs.Contains(Path.GetExtension(paths[0]).TrimStart('.'));

    public EditorWindow Create(IReadOnlyList<string> paths) => new ColorWindow(paths[0]);
}

internal sealed class ColorWindow : EditorWindow
{
    /// <summary>One adjustment. <paramref name="Icon"/> is a list of 24x24 path pieces; a piece starting with "#" is filled.</summary>
    private sealed record Param(string Key, string En, string Ru, double Min, double Max, double Step, string[] Icon);

    private static string Circle(double x, double y, double r) =>
        FormattableString.Invariant($"M{x + r},{y} A{r},{r} 0 1 1 {x - r},{y} A{r},{r} 0 1 1 {x + r},{y} Z");

    private static readonly Param[] Params =
    [
        new("exp", "Exposure", "Экспозиция", -3, 3, 0.05, ["M6,4 H18 A2,2 0 0 1 20,6 V18 A2,2 0 0 1 18,20 H6 A2,2 0 0 1 4,18 V6 A2,2 0 0 1 6,4 Z", "M8,10 H14 M11,7 V13 M9,16.5 H15"]),
        new("bright", "Brightness", "Яркость", -100, 100, 1, [Circle(12, 12, 3.8), "M12,2.8 V5.2 M12,18.8 V21.2 M2.8,12 H5.2 M18.8,12 H21.2 M5.5,5.5 L7.2,7.2 M16.8,16.8 L18.5,18.5 M5.5,18.5 L7.2,16.8 M16.8,7.2 L18.5,5.5"]),
        new("contrast", "Contrast", "Контраст", -100, 100, 1, [Circle(12, 12, 8), "#M12,4 A8,8 0 0 1 12,20 Z"]),
        new("high", "Highlights", "Света", -100, 100, 1, [Circle(12, 12, 8), "#M12,12 L12,4 A8,8 0 0 1 20,12 Z"]),
        new("shadow", "Shadows", "Тени", -100, 100, 1, [Circle(12, 12, 8), "#M12,12 L12,20 A8,8 0 0 1 4,12 Z"]),
        new("black", "Black point", "Чёрная точка", 0, 100, 1, ["#" + Circle(12, 12, 8)]),
        new("white", "White point", "Белая точка", 0, 100, 1, [Circle(12, 12, 8), Circle(12, 12, 3.5)]),
        new("temp", "Warmth", "Температура", -100, 100, 1, ["M10,14.4 V5.5 A2,2 0 0 1 14,5.5 V14.4 A4,4 0 1 1 10,14.4 Z", "M12,9 V16.5"]),
        new("tint", "Tint", "Оттенок", -100, 100, 1, ["M12,3 C12,3 5.5,10.5 5.5,14.5 A6.5,6.5 0 0 0 18.5,14.5 C18.5,10.5 12,3 12,3 Z"]),
        new("sat", "Saturation", "Насыщенность", -100, 100, 1, [Circle(9.5, 9.5, 5), Circle(14.5, 9.5, 5), Circle(12, 14.5, 5)]),
        new("vib", "Vibrance", "Сочность", -100, 100, 1, ["M12,3 C12.8,8 14,10.2 21,12 C14,13.8 12.8,16 12,21 C11.2,16 10,13.8 3,12 C10,10.2 11.2,8 12,3 Z"]),
        new("teal", "Teal and orange", "Teal и orange", -100, 100, 1, ["#" + Circle(9, 12, 6), Circle(15, 12, 6)]),
        new("fade", "Matte", "Матовость", 0, 100, 1, ["M4,6 H20 V18 H4 Z", "M8,10 H16 M8,14 H14"]),
        new("vignette", "Vignette", "Виньетка", -100, 100, 1, ["M3,5 H21 V19 H3 Z", "M18.5,12 A6.5,4.8 0 1 1 5.5,12 A6.5,4.8 0 1 1 18.5,12 Z"]),
        new("grain", "Grain", "Зерно", 0, 100, 1, ["#" + Circle(6, 7, 1.3), "#" + Circle(12, 6, 1.3), "#" + Circle(18, 8, 1.3), "#" + Circle(8, 12, 1.3), "#" + Circle(15, 12, 1.3), "#" + Circle(5.5, 17, 1.3), "#" + Circle(11, 17.5, 1.3), "#" + Circle(18, 16.5, 1.3)]),
        new("sharp", "Sharpness", "Резкость", 0, 100, 1, ["M12,4 L21,19 H3 Z"]),
    ];

    private const string AutoIcon = "M4.5,19.5 L14,10 M17,3 L18,6 L21,7 L18,8 L17,11 L16,8 L13,7 L16,6 Z";
    private const string CompareIcon = "M3,5 H21 V19 H3 Z M12,5 V19";
    private const string ResetIcon = "M5.5,10 A7.5,7.5 0 1 1 6.5,16.5 M4,4.5 V10 H9.5";

    private static readonly (string En, string Ru, Dictionary<string, double> Values)[] Looks =
    [
        ("Original", "Оригинал", []),
        ("Vivid", "Яркий", new() { ["contrast"] = 15, ["sat"] = 18, ["vib"] = 25, ["sharp"] = 20 }),
        ("Warm", "Тёплый", new() { ["temp"] = 35, ["tint"] = 5, ["sat"] = 5 }),
        ("Cool", "Холодный", new() { ["temp"] = -35, ["tint"] = -5 }),
        ("Matte", "Матовый", new() { ["fade"] = 45, ["contrast"] = -10, ["sat"] = -10 }),
        ("Teal and orange", "Teal и orange", new() { ["teal"] = 70, ["contrast"] = 12, ["sat"] = 8 }),
        ("Moody", "Мрачный", new() { ["exp"] = -0.3, ["contrast"] = 20, ["shadow"] = -20, ["sat"] = -15, ["vignette"] = 35, ["teal"] = 30 }),
        ("Airy", "Воздушный", new() { ["exp"] = 0.4, ["shadow"] = 30, ["contrast"] = -10, ["vib"] = 15, ["fade"] = 15 }),
        ("Black and white", "Ч/Б", new() { ["sat"] = -100, ["contrast"] = 15 }),
        ("Sepia", "Сепия", new() { ["sat"] = -75, ["temp"] = 55, ["tint"] = 10, ["fade"] = 20 }),
    ];

    private const double ToolSize = 36;

    private readonly string _path;
    private readonly string _ext;
    private readonly BitmapSource _frame;
    private readonly Dictionary<string, Slider> _sliders = [];
    private readonly Dictionary<string, Border> _tools = [];
    private readonly Dictionary<string, WPath> _rings = [];
    private readonly Dictionary<string, WPath[]> _icons = [];
    private readonly Image _view = new() { Stretch = Stretch.Uniform };
    private readonly ContentPresenter _sliderHost = new();
    private readonly TextBlock _title = new() { HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _valueText = new() { HorizontalAlignment = HorizontalAlignment.Center, Cursor = Cursors.Hand };
    private readonly StackPanel _adjustPanel = new();
    private readonly ScrollViewer _toolStrip = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden };
    private readonly ScrollViewer _lookStrip = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, Visibility = Visibility.Collapsed };
    private readonly Button _adjustTab = new() { MinWidth = 96 };
    private readonly Button _looksTab = new() { MinWidth = 96 };
    private readonly List<Border> _lookBorders = [];
    private byte[] _previewPixels = [];
    private byte[] _thumbPixels = [];
    private int _previewWidth, _previewHeight, _thumbWidth, _thumbHeight;
    private BitmapSource? _original;
    private BitmapSource? _graded;
    private string _current = "exp";
    private bool _showOriginal;
    private bool _suspend;
    private int _token;

    public ColorWindow(string path) : base(T("Colour grade", "Цвет и свет") + " · " + Path.GetFileName(path), 820, 640, T("Save a copy", "Сохранить копию"))
    {
        _path = path;
        _ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant() switch { "jpeg" => "jpg", "tif" => "tiff", var e => e };
        try
        {
            _frame = Wic.Load(path);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or InvalidOperationException)
        {
            throw new IOException(T("Could not read the picture", "Не удалось прочитать картинку"), ex);
        }

        // The preview works on a reduced copy turned the right way up; the saved file is made from the full picture.
        var oriented = ImageTools.Oriented(_frame);
        var longest = Math.Max(oriented.PixelWidth, oriented.PixelHeight);
        var scale = Math.Min(1.0, 1100.0 / longest);
        var small = scale < 1 ? ImageTools.Scaled(oriented, Math.Max(1, (int)(oriented.PixelWidth * scale)), Math.Max(1, (int)(oriented.PixelHeight * scale))) : oriented;
        (_previewPixels, _previewWidth, _previewHeight) = ImageTools.ToBgra(small);
        _original = ImageTools.FromBgra(_previewPixels, _previewWidth, _previewHeight);
        _view.Source = _original;
        var thumbScale = Math.Min(1.0, 180.0 / Math.Max(small.PixelWidth, small.PixelHeight));
        var thumb = thumbScale < 1 ? ImageTools.Scaled(small, Math.Max(1, (int)(small.PixelWidth * thumbScale)), Math.Max(1, (int)(small.PixelHeight * thumbScale))) : small;
        (_thumbPixels, _thumbWidth, _thumbHeight) = ImageTools.ToBgra(thumb);

        foreach (var param in Params)
            _sliders[param.Key] = MakeSlider(param);

        // Preview with two small icon buttons in its corner: compare with the original, reset everything.
        var compare = IconButton(CompareIcon, T("Hold to see the original", "Удерживай, чтобы увидеть оригинал"));
        compare.PreviewMouseLeftButtonDown += (_, _) => ShowOriginal(true);
        compare.PreviewMouseLeftButtonUp += (_, _) => ShowOriginal(false);
        compare.MouseLeave += (_, _) => ShowOriginal(false);
        var reset = IconButton(ResetIcon, T("Reset all", "Сбросить всё"));
        reset.MouseLeftButtonUp += (_, _) => ApplyLook([]);
        var corner = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 12, 12), Children = { reset, compare } };
        var previewHost = new Grid();
        previewHost.Children.Add(PreviewBox(_view));
        previewHost.Children.Add(corner);

        // Tool strip: Auto, then one round icon per adjustment.
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        tools.Children.Add(MakeAuto());
        foreach (var param in Params)
            tools.Children.Add(MakeTool(param));
        _toolStrip.Content = tools;
        WheelToHorizontal(_toolStrip);

        var looks = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        _lookStrip.Content = looks;
        WheelToHorizontal(_lookStrip);
        Loaded += (_, _) => BuildLooks(looks);

        _adjustPanel.Children.Add(_title);
        _adjustPanel.Children.Add(_valueText);
        _adjustPanel.Children.Add(_sliderHost);
        _valueText.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
                _sliders[_current].Value = 0;
        };

        _adjustTab.Content = T("Adjust", "Правка");
        _looksTab.Content = T("Looks", "Фильтры");
        _adjustTab.Click += (_, _) => ShowTab(adjust: true);
        _looksTab.Click += (_, _) => ShowTab(adjust: false);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), Children = { _adjustTab, new Border { Width = 8 }, _looksTab } };

        var dock = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                new Grid { Height = 62, Children = { _adjustPanel } },
                new Grid { Margin = new Thickness(0, 2, 0, 0), MinHeight = 92, Children = { _toolStrip, _lookStrip } },
                tabs,
            },
        };
        var grid = new Grid { Margin = new Thickness(Gap) };
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(previewHost);
        Grid.SetRow(dock, 1);
        grid.Children.Add(dock);
        Body = grid;

        Select("exp");
        ShowTab(adjust: true);
        SetHint("→ " + Path.GetFileName(ConvertContext.NewPath(path, _ext)));
        Loaded += (_, _) => Render();
    }

    protected override string DoneTitle => T("Colour applied", "Цвет применён");

    protected override bool SnapshotReady => _graded is not null && _lookBorders.Count > 0;

    /// <summary>"look=Moody" or "exp=0.5,contrast=20,temp=-10".</summary>
    protected override void ConfigureForTest(string spec)
    {
        _suspend = true;
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=');
            if (pair.Length < 2)
                continue;
            if (pair[0] == "tab")
            {
                ShowTab(adjust: pair[1] != "looks");
            }
            else if (pair[0] == "look")
            {
                var look = Looks.FirstOrDefault(l => l.En.Equals(pair[1], StringComparison.OrdinalIgnoreCase));
                if (look.Values is not null)
                    foreach (var (key, value) in look.Values)
                        _sliders[key].Value = value;
            }
            else if (_sliders.TryGetValue(pair[0], out var slider) && double.TryParse(pair[1], CultureInfo.InvariantCulture, out var v))
            {
                slider.Value = v;
            }
        }
        _suspend = false;
    }

    private Slider MakeSlider(Param param)
    {
        var slider = new Slider { Minimum = param.Min, Maximum = param.Max, Value = 0, SmallChange = param.Step, LargeChange = param.Step * 10, TickFrequency = param.Step, IsSnapToTickEnabled = true, Margin = new Thickness(8, 6, 8, 0) };
        slider.ValueChanged += (_, _) =>
        {
            UpdateTool(param);
            if (param.Key == _current)
                UpdateValueText();
            if (!_suspend)
            {
                ClearLookSelection();
                Render();
            }
        };
        return slider;
    }

    private Param CurrentParam => Params.First(p => p.Key == _current);

    private void Select(string key)
    {
        var previous = _current;
        _current = key;
        _sliderHost.Content = _sliders[key];
        var param = CurrentParam;
        _title.Text = T(param.En, param.Ru);
        UpdateValueText();
        if (_tools.ContainsKey(previous))
            UpdateTool(Params.First(p => p.Key == previous));
        UpdateTool(param);
    }

    private void UpdateValueText()
    {
        var param = CurrentParam;
        var value = _sliders[param.Key].Value;
        _valueText.Text = param.Step < 1 ? value.ToString("0.00", CultureInfo.InvariantCulture) : ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        _valueText.Foreground = Math.Abs(value) < 1e-9 ? Muted : AccentBrush;
    }

    private void ShowTab(bool adjust)
    {
        _adjustPanel.Visibility = adjust ? Visibility.Visible : Visibility.Hidden;
        _toolStrip.Visibility = adjust ? Visibility.Visible : Visibility.Collapsed;
        _lookStrip.Visibility = adjust ? Visibility.Collapsed : Visibility.Visible;
        var accent = TryFindResource("AccentButtonStyle") as Style;
        var plain = TryFindResource(typeof(Button)) as Style;
        _adjustTab.Style = adjust ? accent : plain;
        _looksTab.Style = adjust ? plain : accent;
    }

    private static void WheelToHorizontal(ScrollViewer viewer) =>
        viewer.PreviewMouseWheel += (_, e) =>
        {
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
            e.Handled = true;
        };

    // ---- Tool icons ----

    private WPath[] DrawIcon(string[] pieces, Brush stroke, double size)
    {
        var paths = new List<WPath>();
        foreach (var piece in pieces)
        {
            var filled = piece.StartsWith('#');
            var data = filled ? piece[1..] : piece;
            paths.Add(new WPath
            {
                Data = Geometry.Parse(data),
                Stroke = stroke,
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Fill = filled ? stroke : null,
                Width = 24,
                Height = 24,
                Stretch = Stretch.None,
            });
        }
        return [.. paths];
    }

    private FrameworkElement IconHost(string[] pieces, Brush stroke, double size, out WPath[] paths)
    {
        paths = DrawIcon(pieces, stroke, size);
        var canvas = new Canvas { Width = 24, Height = 24 };
        foreach (var path in paths)
            canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }

    private Border IconButton(string icon, string tip)
    {
        var host = IconHost([icon], Foreground, 18, out _);
        var border = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Margin = new Thickness(6, 0, 0, 0),
            Background = Hex(Dark ? "#99202020" : "#99FFFFFF"),
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Child = host,
        };
        return border;
    }

    private Border MakeAuto()
    {
        var border = BuildToolShell(AutoIcon, out _, out _, T("Auto", "Авто"));
        border.MouseLeftButtonUp += (_, _) => AutoLevels();
        return border;
    }

    private Border MakeTool(Param param)
    {
        var border = BuildToolShell(param.Icon, out var ring, out var icons, T(param.En, param.Ru));
        border.MouseLeftButtonUp += (_, _) => Select(param.Key);
        _tools[param.Key] = border;
        _rings[param.Key] = ring;
        _icons[param.Key] = icons;
        return border;
    }

    private Border BuildToolShell(string icon, out WPath ring, out WPath[] icons, string tip) => BuildToolShell([icon], out ring, out icons, tip);

    private Border BuildToolShell(string[] pieces, out WPath ring, out WPath[] icons, string tip)
    {
        var grid = new Grid { Width = ToolSize, Height = ToolSize };
        grid.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = LineBrush, StrokeThickness = 1.5, Margin = new Thickness(1) });
        ring = new WPath { Stroke = AccentBrush, StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        grid.Children.Add(ring);
        var host = IconHost(pieces, Foreground, 20, out icons);
        host.HorizontalAlignment = HorizontalAlignment.Center;
        host.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(host);
        var border = new Border
        {
            Width = ToolSize + 4,
            Height = ToolSize + 4,
            CornerRadius = new CornerRadius((ToolSize + 4) / 2),
            Margin = new Thickness(1.5, 0, 1.5, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Child = grid,
        };
        var hover = Hex(Dark ? "#1FFFFFFF" : "#14000000");
        border.MouseEnter += (_, _) => border.Background = hover;
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        return border;
    }

    /// <summary>Redraws the progress ring (how far the slider is from zero) and the selected / changed colours of a tool.</summary>
    private void UpdateTool(Param param)
    {
        if (!_rings.TryGetValue(param.Key, out var ring))
            return;
        var value = _sliders[param.Key].Value;
        var span = Math.Max(Math.Abs(param.Min), Math.Abs(param.Max));
        var fraction = Math.Min(0.999, Math.Abs(value) / span);
        var selected = param.Key == _current;
        var changed = Math.Abs(value) > 1e-9;
        if (!changed)
        {
            ring.Data = null;
        }
        else
        {
            const double r = ToolSize / 2 - 2;
            const double c = ToolSize / 2;
            var angle = fraction * 2 * Math.PI;
            var sign = value > 0 ? 1 : -1;
            var end = new Point(c + r * Math.Sin(sign * angle), c - r * Math.Cos(sign * angle));
            var figure = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
            figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, sign > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true));
            ring.Data = new PathGeometry([figure]);
        }
        var brush = selected || changed ? AccentBrush : Foreground;
        foreach (var piece in _icons[param.Key])
        {
            piece.Stroke = brush;
            if (piece.Fill is not null)
                piece.Fill = brush;
        }
        _tools[param.Key].Background = selected ? Hex(Dark ? "#26FFFFFF" : "#1A000000") : Brushes.Transparent;
    }

    // ---- Looks ----

    private ColorParams Parameters(Func<string, double> get) => new(
        Exposure: get("exp"),
        Brightness: get("bright") / 100,
        Contrast: get("contrast") / 100,
        Highlights: get("high") / 100,
        Shadows: get("shadow") / 100,
        Temperature: get("temp") / 100,
        Tint: get("tint") / 100,
        Saturation: get("sat") / 100,
        Vibrance: get("vib") / 100,
        TealOrange: get("teal") / 100,
        Fade: get("fade") / 100,
        Sharpness: get("sharp") / 100,
        Vignette: get("vignette") / 100,
        Grain: get("grain") / 100,
        BlackPoint: get("black") / 100,
        WhitePoint: get("white") / 100);

    private void BuildLooks(StackPanel host)
    {
        if (_lookBorders.Count > 0)
            return;
        var pixels = _thumbPixels;
        int width = _thumbWidth, height = _thumbHeight;
        foreach (var look in Looks)
        {
            var values = look.Values;
            var graded = ColorPipeline.Apply(pixels, width, height, Parameters(key => values.GetValueOrDefault(key)));
            var image = new Image { Source = ImageTools.FromBgra(graded, width, height), Stretch = Stretch.UniformToFill, Width = 62, Height = 62 };
            var tile = new Border { Width = 62, Height = 62, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = image };
            var frame = new Border
            {
                Padding = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                Child = new StackPanel
                {
                    Children = { tile, new TextBlock { Text = T(look.En, look.Ru), FontSize = 11, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) } },
                },
            };
            var index = _lookBorders.Count;
            frame.MouseLeftButtonUp += (_, _) =>
            {
                ApplyLook(values);
                SelectLook(index);
            };
            _lookBorders.Add(frame);
            host.Children.Add(frame);
        }
        SelectLook(0);
    }

    private void SelectLook(int index)
    {
        for (var i = 0; i < _lookBorders.Count; i++)
            _lookBorders[i].BorderBrush = i == index ? AccentBrush : Brushes.Transparent;
    }

    private void ClearLookSelection()
    {
        foreach (var frame in _lookBorders)
            frame.BorderBrush = Brushes.Transparent;
    }

    private void ApplyLook(Dictionary<string, double> values)
    {
        _suspend = true;
        foreach (var (key, slider) in _sliders)
            slider.Value = values.GetValueOrDefault(key);
        _suspend = false;
        Render();
    }

    private void AutoLevels()
    {
        var (black, white) = ColorPipeline.AutoLevels(_previewPixels);
        _suspend = true;
        _sliders["black"].Value = Math.Round(black * 100);
        _sliders["white"].Value = Math.Round(white * 100);
        _suspend = false;
        ClearLookSelection();
        Render();
    }

    private void ShowOriginal(bool on)
    {
        if (_showOriginal == on)
            return;
        _showOriginal = on;
        _view.Source = on ? _original : _graded ?? _original;
    }

    private ColorParams Parameters() => Parameters(key => _sliders[key].Value);

    private async void Render()
    {
        var token = ++_token;
        var parameters = Parameters();
        var pixels = _previewPixels;
        int width = _previewWidth, height = _previewHeight;
        try
        {
            var bitmap = await Task.Run(() => ImageTools.FromBgra(ColorPipeline.Apply(pixels, width, height, parameters), width, height));
            if (token != _token)
                return;
            _graded = bitmap;
            if (!_showOriginal)
                _view.Source = bitmap;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.LogCrash(ex);
        }
    }

    protected override async Task<string> SaveAsync(ConvertContext context)
    {
        var parameters = Parameters();
        if (parameters.IsIdentity)
            throw new ConvertException(T("Nothing is changed yet", "Пока ничего не изменено"));
        var output = ConvertContext.NewPath(_path, _ext, T(" (edited)", " (цвет)"));
        var path = _path;
        var ext = _ext;
        context.Report(T("Developing the picture…", "Обрабатываю картинку…"), null);
        await Sta.Run(() =>
        {
            var frame = Wic.Load(path);
            var (pixels, width, height) = ImageTools.ToBgra(frame);
            var graded = ImageTools.FromBgra(ColorPipeline.Apply(pixels, width, height, parameters), width, height, frame.DpiX, frame.DpiY);
            if (ext == "jpg")
            {
                byte[] bytes;
                try
                {
                    // Same pixels, same orientation tag, the rest of the EXIF carried over.
                    bytes = ImageTools.Jpeg(ImageTools.OnWhite(graded), 95, frame.Metadata?.Clone() as BitmapMetadata);
                }
                catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    // Some cameras write tags the encoder refuses: save without them, turned the right way up.
                    bytes = ImageTools.Jpeg(ImageTools.OnWhite(ImageTools.Oriented(graded, CropWindow.StateFromExif(frame))), 95);
                }
                File.WriteAllBytes(output, bytes);
            }
            else
            {
                Wic.Save(ImageTools.Oriented(graded, CropWindow.StateFromExif(frame)), ext, output);
            }
            return 0;
        });
        return output;
    }
}

