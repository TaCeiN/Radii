using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Radii.Converters;
using static Radii.Menus;

namespace Radii.Editors;

/// <summary>Change the pixel size of a picture: exact size, percent or the longest side. New file next to the original.</summary>
internal sealed class ImageResizeEditor : IEditor
{
    public string Caption => T("Resize photo", "Размер фото");

    public string Glyph => "";

    public bool Accepts(IReadOnlyList<string> paths) =>
        paths.Count == 1 && ImageCropEditor.Inputs.Contains(Path.GetExtension(paths[0]).TrimStart('.'));

    public EditorWindow Create(IReadOnlyList<string> paths) => new ResizeWindow(paths[0]);
}

internal sealed class ResizeWindow : EditorWindow
{
    private const int MaxPixels = 200_000_000;

    private readonly string _path;
    private readonly string _ext;
    private readonly BitmapSource _source;
    private readonly TextBox _width = Field();
    private readonly TextBox _height = Field();
    private readonly ToggleButton _lock = new() { IsChecked = true, Content = "🔗", ToolTip = "", Width = 36, Margin = new Thickness(8, 0, 8, 0) };
    private readonly Slider _quality = new() { Minimum = 40, Maximum = 100, Value = 92, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _result = new() { FontWeight = FontWeights.SemiBold };
    private bool _updating;

    public ResizeWindow(string path) : base(T("Resize photo", "Размер фото") + " · " + Path.GetFileName(path), 560, 360, T("Save a copy", "Сохранить копию"))
    {
        _path = path;
        _ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant() switch { "jpeg" => "jpg", "tif" => "tiff", var e => e };
        try
        {
            _source = Oriented(Wic.Load(path));
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or InvalidOperationException)
        {
            throw new IOException(T("Could not read the picture", "Не удалось прочитать картинку"), ex);
        }
        _lock.ToolTip = T("Keep the proportions", "Сохранять пропорции");

        _width.LostFocus += (_, _) => Apply(width: true);
        _height.LostFocus += (_, _) => Apply(width: false);
        _width.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(true); };
        _height.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(false); };

        var percents = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var percent in new[] { 25, 50, 75, 150, 200 })
            percents.Children.Add(Chip(percent + "%", () => SetSize((int)Math.Round(_source.PixelWidth * percent / 100.0), null, force: true)));
        var sides = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var side in new[] { 1920, 1280, 800, 512 })
            sides.Children.Add(Chip(side.ToString(CultureInfo.InvariantCulture), () => LongestSide(side)));

        // One small form: the size boxes, two rows of presets, quality. No preview, the picture does not matter here.
        _lock.Width = 40;
        _lock.Margin = new Thickness(10, 0, 10, 0);
        var sizeLine = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { _width, _lock, _height } };
        var original = new TextBlock { Text = $"{_source.PixelWidth} × {_source.PixelHeight}", Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        var rows = new List<UIElement>
        {
            sizeLine,
            original,
            Row(T("Percent", "Процент"), percents, top: 14),
            Row(T("Long side", "Длинная сторона"), sides, top: 4),
        };
        if (_ext == "jpg")
        {
            var value = new TextBlock { Text = "92" };
            _quality.ValueChanged += (_, _) => value.Text = ((int)_quality.Value).ToString(CultureInfo.InvariantCulture);
            rows.Add(SliderRow(T("JPG quality", "Качество JPG"), _quality, value, 14));
        }
        var form = new StackPanel();
        foreach (var row in rows)
            form.Children.Add(row);
        Body = new StackPanel { Margin = new Thickness(Gap), Children = { Card(form, new Thickness(16)) } };
        MinHeight = 0;
        FitHeight(640);

        SetSize(_source.PixelWidth, null, force: true);
        SetHint("→ " + Path.GetFileName(ConvertContext.NewPath(path, _ext)));
    }

    protected override string DoneTitle => T("Photo resized", "Размер фото изменён");

    /// <summary>"w=1920" or "h=600" or "p=50" (percent).</summary>
    protected override void ConfigureForTest(string spec)
    {
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=');
            var number = pair.Length > 1 && int.TryParse(pair[1], out var n) ? n : 0;
            switch (pair[0])
            {
                case "w":
                    SetSize(number, null, force: true);
                    break;
                case "h":
                    SetSize(null, number, force: true);
                    break;
                case "p":
                    SetSize((int)Math.Round(_source.PixelWidth * number / 100.0), null, force: true);
                    break;
            }
        }
    }

    private static TextBox Field() => new() { Width = 84, TextAlignment = TextAlignment.Center };

    private static Button Tight(Button chip)
    {
        chip.MinWidth = 0;
        chip.Padding = new Thickness(0, 4, 0, 4);
        chip.HorizontalAlignment = HorizontalAlignment.Stretch;
        return chip;
    }

    private static BitmapSource Oriented(BitmapSource frame)
    {
        var (flip, rotation) = CropWindow.StateFromExif(frame);
        var group = new TransformGroup();
        if (flip)
            group.Children.Add(new ScaleTransform(-1, 1));
        if (rotation != 0)
            group.Children.Add(new RotateTransform(rotation));
        if (group.Children.Count == 0)
            return frame;
        var transformed = new TransformedBitmap(frame, group);
        transformed.Freeze();
        return transformed;
    }

    private int CurrentWidth => int.TryParse(_width.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ? w : _source.PixelWidth;

    private int CurrentHeight => int.TryParse(_height.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ? h : _source.PixelHeight;

    private void LongestSide(int side)
    {
        if (_source.PixelWidth >= _source.PixelHeight)
            SetSize(side, null, force: true);
        else
            SetSize(null, side, force: true);
    }

    /// <summary>Sets one side; the other follows while the proportions are kept (or when <paramref name="force"/>d, as the presets do).</summary>
    private void SetSize(int? width, int? height, bool force)
    {
        double ratio = (double)_source.PixelHeight / _source.PixelWidth;
        var keep = force || _lock.IsChecked == true;
        int w, h;
        if (width is { } nw)
        {
            w = nw;
            h = keep ? (int)Math.Round(nw * ratio) : CurrentHeight;
        }
        else if (height is { } nh)
        {
            h = nh;
            w = keep ? (int)Math.Round(nh / ratio) : CurrentWidth;
        }
        else
        {
            return;
        }
        w = Math.Clamp(w, 1, 30000);
        h = Math.Clamp(h, 1, 30000);
        _updating = true;
        _width.Text = w.ToString(CultureInfo.InvariantCulture);
        _height.Text = h.ToString(CultureInfo.InvariantCulture);
        _updating = false;
        _result.Text = T($"Result: {w} × {h}", $"Получится: {w} × {h}");
    }

    private void Apply(bool width)
    {
        if (_updating)
            return;
        var box = width ? _width : _height;
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            SetSize(_source.PixelWidth, null, force: true);
            return;
        }
        if (width)
            SetSize(value, null, force: false);
        else
            SetSize(null, value, force: false);
    }

    protected override async Task<string> SaveAsync(ConvertContext context)
    {
        int w = CurrentWidth, h = CurrentHeight;
        if (w < 1 || h < 1 || (long)w * h > MaxPixels)
            throw new ConvertException(T("That size is too large", "Слишком большой размер"));
        var quality = (int)_quality.Value;
        var source = _source;
        var output = ConvertContext.NewPath(_path, _ext, $" ({w}x{h})");
        context.Report(T("Saving…", "Сохраняю…"), null);
        await Sta.Run(() =>
        {
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var dc = visual.RenderOpen())
                dc.DrawImage(source, new Rect(0, 0, w, h));
            var scaled = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            scaled.Render(visual);
            scaled.Freeze();
            if (_ext == "jpg")
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = quality };
                encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(OnWhite(scaled), PixelFormats.Bgr24, null, 0)));
                using var file = File.Create(output);
                encoder.Save(file);
            }
            else
            {
                Wic.Save(scaled, _ext, output);
            }
            return 0;
        });
        return output;
    }

    /// <summary>JPEG has no alpha: composite on white like an image viewer would.</summary>
    private static BitmapSource OnWhite(BitmapSource source)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }
        var target = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
