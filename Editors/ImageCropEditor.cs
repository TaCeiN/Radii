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

/// <summary>Crop, rotate and flip a picture. The result is a new file next to the original (EXIF is dropped, orientation is baked in).</summary>
internal sealed class ImageCropEditor : IEditor
{
    internal static readonly HashSet<string> Inputs = new(["png", "jpg", "jpeg", "bmp", "tif", "tiff"], StringComparer.OrdinalIgnoreCase);

    public string Caption => T("Crop photo", "Обрезка фото");

    public string Glyph => "";

    public bool Accepts(IReadOnlyList<string> paths) =>
        paths.Count == 1 && Inputs.Contains(Path.GetExtension(paths[0]).TrimStart('.'));

    public EditorWindow Create(IReadOnlyList<string> paths) => new CropWindow(paths[0]);
}

internal sealed class CropWindow : EditorWindow
{
    private readonly string _path;
    private readonly string _ext;
    private readonly BitmapSource _original;
    private readonly CropSurface _surface = new();
    private readonly TextBox _width = Field();
    private readonly TextBox _height = Field();
    private readonly TextBlock _qualityValue = new() { Text = "92" };
    private readonly Slider _quality = new() { Minimum = 40, Maximum = 100, Value = 92, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly List<(ToggleButton Button, Func<double?> Ratio)> _chips = [];
    private bool _flip;
    private int _rotation;
    private bool _updating;
    private Func<double?> _ratio = () => null;

    public CropWindow(string path) : base(T("Crop photo", "Обрезка фото") + " · " + Path.GetFileName(path), 760, 500, T("Save a copy", "Сохранить копию"))
    {
        _path = path;
        _ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant() switch { "jpeg" => "jpg", "tif" => "tiff", var e => e };
        try
        {
            _original = Wic.Load(path);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or InvalidOperationException)
        {
            throw new IOException(T("Could not read the picture", "Не удалось прочитать картинку"), ex);
        }
        (_flip, _rotation) = StateFromExif(_original);

        _surface.CropChanged += OnCropChanged;
        _width.LostFocus += (_, _) => ApplySize(width: true);
        _height.LostFocus += (_, _) => ApplySize(width: false);
        _width.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplySize(width: true); };
        _height.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplySize(width: false); };

        Body = PreviewLayout(PreviewBox(_surface), BuildPanel());

        Rebuild();
        SetHint("→ " + Path.GetFileName(ConvertContext.NewPath(path, _ext)));
    }

    protected override string DoneTitle => T("Photo cropped", "Фото обрезано");

    /// <summary>"rot=90,flip,w=800,h=500" (turn first, then size the frame).</summary>
    protected override void ConfigureForTest(string spec)
    {
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=');
            var number = pair.Length > 1 && int.TryParse(pair[1], out var n) ? n : 0;
            switch (pair[0])
            {
                case "rot":
                    Rotate(number);
                    break;
                case "flip":
                    Flip();
                    break;
                case "w":
                    _surface.Resize(number, null);
                    break;
                case "h":
                    _surface.Resize(null, number);
                    break;
            }
        }
    }

    private static TextBox Field() => new() { Width = 84, TextAlignment = TextAlignment.Center };

    internal static (bool Flip, int Rotation) StateFromExif(BitmapSource frame)
    {
        var orientation = 1;
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort value)
                orientation = value;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
        return orientation switch
        {
            2 => (true, 0),
            3 => (false, 180),
            4 => (true, 180),
            5 => (true, 270),
            6 => (false, 90),
            7 => (true, 90),
            8 => (false, 270),
            _ => (false, 0),
        };
    }

    private UIElement BuildPanel()
    {
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, -6) };
        AddChip(chips, T("Free", "Свободно"), () => null, checkedAtStart: true);
        AddChip(chips, "1:1", () => 1.0);
        AddChip(chips, "4:3", () => 4.0 / 3);
        AddChip(chips, "3:2", () => 3.0 / 2);
        AddChip(chips, "16:9", () => 16.0 / 9);
        AddChip(chips, "9:16", () => 9.0 / 16);

        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(Tool("↺", T("Rotate left", "Повернуть влево"), () => Rotate(270)));
        tools.Children.Add(Tool("↻", T("Rotate right", "Повернуть вправо"), () => Rotate(90)));
        tools.Children.Add(Tool("⇆", T("Flip horizontally", "Отразить по горизонтали"), Flip));
        ((FrameworkElement)tools.Children[^1]).Margin = new Thickness(0);

        _width.Width = 70;
        _height.Width = 70;
        var size = new StackPanel { Orientation = Orientation.Horizontal };
        size.Children.Add(_width);
        size.Children.Add(new TextBlock { Text = "×", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) });
        size.Children.Add(_height);

        var blocks = new List<UIElement> { Row(T("Size", "Размер"), size), Row(T("Turn", "Поворот"), tools, top: 8) };
        if (_ext == "jpg")
        {
            _quality.ValueChanged += (_, _) => _qualityValue.Text = ((int)_quality.Value).ToString(CultureInfo.InvariantCulture);
            blocks.Add(SliderRow(T("Quality", "Качество"), _quality, _qualityValue, top: 10));
        }
        return Column(Section("", chips), Section("", [.. blocks]));
    }

    private void AddChip(Panel host, string text, Func<double?> ratio, bool checkedAtStart = false)
    {
        var button = new ToggleButton { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(11, 4, 11, 4), MinWidth = 44, IsChecked = checkedAtStart };
        button.Click += (_, _) =>
        {
            foreach (var chip in _chips)
                chip.Button.IsChecked = chip.Button == button;
            _ratio = ratio;
            _surface.SetRatio(ratio());
        };
        _chips.Add((button, ratio));
        host.Children.Add(button);
    }

    private Button Tool(string glyph, string tip, Action action)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI"), FontSize = 17 },
            ToolTip = tip,
            Width = 36,
            Margin = new Thickness(0, 0, 6, 0),
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void Rotate(int degrees)
    {
        _rotation = (_rotation + degrees) % 360;
        Rebuild();
    }

    private void Flip()
    {
        // A flip of the displayed picture after the rotation equals the opposite rotation before it.
        _flip = !_flip;
        _rotation = (360 - _rotation) % 360;
        Rebuild();
    }

    private BitmapSource Build()
    {
        var group = new TransformGroup();
        if (_flip)
            group.Children.Add(new ScaleTransform(-1, 1));
        if (_rotation != 0)
            group.Children.Add(new RotateTransform(_rotation));
        if (group.Children.Count == 0)
            return _original;
        var transformed = new TransformedBitmap(_original, group);
        transformed.Freeze();
        return transformed;
    }

    private void Rebuild()
    {
        _surface.SetSource(Build(), _ratio());
    }

    private void OnCropChanged()
    {
        _updating = true;
        _width.Text = ((int)Math.Round(_surface.Crop.Width)).ToString(CultureInfo.InvariantCulture);
        _height.Text = ((int)Math.Round(_surface.Crop.Height)).ToString(CultureInfo.InvariantCulture);
        _updating = false;
    }

    private void ApplySize(bool width)
    {
        if (_updating)
            return;
        var box = width ? _width : _height;
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            OnCropChanged();
            return;
        }
        _surface.Resize(width ? value : null, width ? null : value);
    }

    protected override async Task<string> SaveAsync(ConvertContext context)
    {
        var source = Build();
        var rect = _surface.PixelRect;
        var cropped = new CroppedBitmap(source, rect);
        cropped.Freeze();
        var quality = (int)_quality.Value;
        var output = ConvertContext.NewPath(_path, _ext);
        context.Report(T("Saving…", "Сохраняю…"), null);
        await Sta.Run(() =>
        {
            if (_ext == "jpg")
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = quality };
                encoder.Frames.Add(BitmapFrame.Create(cropped));
                using var file = File.Create(output);
                encoder.Save(file);
            }
            else
            {
                Wic.Save(cropped, _ext, output);
            }
            return 0;
        });
        return output;
    }
}

/// <summary>The picture with a crop frame: eight handles, move, draw a new frame, optional aspect ratio.</summary>
internal sealed class CropSurface : FrameworkElement
{
    private const double Pad = 16, HandleSize = 9, Grab = 11, MinSize = 8;

    private static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)));
    private static readonly Pen Frame = Frozen(new Pen(Brushes.White, 1.5));
    private static readonly Pen Thirds = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 255, 255, 255)), 0.5));
    private static readonly Pen HandleLine = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), 1));

    private BitmapSource? _source;
    private Rect _crop;
    private double? _ratio;

    // drag state
    private (int X, int Y)? _handle;
    private bool _moving, _drawing;
    private Point _anchor;
    private Point _grabPoint;
    private Rect _startRect;

    public event Action? CropChanged;

    public Rect Crop => _crop;

    public int ImageWidth => _source?.PixelWidth ?? 1;

    public int ImageHeight => _source?.PixelHeight ?? 1;

    public Int32Rect PixelRect
    {
        get
        {
            var x = (int)Math.Round(_crop.X);
            var y = (int)Math.Round(_crop.Y);
            var w = Math.Clamp((int)Math.Round(_crop.Width), 1, ImageWidth - x);
            var h = Math.Clamp((int)Math.Round(_crop.Height), 1, ImageHeight - y);
            return new Int32Rect(x, y, w, h);
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    public void SetSource(BitmapSource source, double? ratio)
    {
        _source = source;
        _ratio = ratio;
        var full = new Rect(0, 0, ImageWidth, ImageHeight);
        _crop = ratio is { } r ? FitRatio(full, r) : full;
        Changed();
    }

    public void SetRatio(double? ratio)
    {
        _ratio = ratio;
        if (ratio is { } r)
        {
            // Same area and centre as the current frame, new shape: 1:1 then 16:9 reshapes the selection instead of
            // resetting it or shrinking it. Only a frame that would leave the picture is scaled down.
            var area = _crop.Width * _crop.Height;
            var w = Math.Sqrt(area * r);
            var h = w / r;
            var scale = Math.Min(1, Math.Min(ImageWidth / w, ImageHeight / h));
            w *= scale;
            h *= scale;
            var cx = _crop.X + _crop.Width / 2;
            var cy = _crop.Y + _crop.Height / 2;
            _crop = new Rect(Math.Clamp(cx - w / 2, 0, ImageWidth - w), Math.Clamp(cy - h / 2, 0, ImageHeight - h), w, h);
        }
        Changed();
    }

    /// <summary>Sets the size of the frame in picture pixels (the other side follows when an aspect ratio is fixed).</summary>
    public void Resize(int? width, int? height)
    {
        if (_source is null)
            return;
        double w = width ?? _crop.Width, h = height ?? _crop.Height;
        if (_ratio is { } q)
        {
            if (width is not null)
                h = w / q;
            else
                w = h * q;
            if (w > ImageWidth)
            {
                w = ImageWidth;
                h = w / q;
            }
            if (h > ImageHeight)
            {
                h = ImageHeight;
                w = h * q;
            }
        }
        w = Math.Clamp(w, Math.Min(MinSize, ImageWidth), ImageWidth);
        h = Math.Clamp(h, Math.Min(MinSize, ImageHeight), ImageHeight);
        _crop = new Rect(Math.Clamp(_crop.X, 0, ImageWidth - w), Math.Clamp(_crop.Y, 0, ImageHeight - h), w, h);
        Changed();
    }

    private static Rect FitRatio(Rect area, double ratio)
    {
        var w = area.Width;
        var h = w / ratio;
        if (h > area.Height)
        {
            h = area.Height;
            w = h * ratio;
        }
        return new Rect(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    private void Changed()
    {
        InvalidateVisual();
        CropChanged?.Invoke();
    }

    // --- geometry ---

    private double Scale => _source is null ? 1 : Math.Min(Math.Min((ActualWidth - 2 * Pad) / ImageWidth, (ActualHeight - 2 * Pad) / ImageHeight), 4);

    private Point Origin => new((ActualWidth - ImageWidth * Scale) / 2, (ActualHeight - ImageHeight * Scale) / 2);

    private Rect ToView(Rect r)
    {
        var s = Scale;
        var o = Origin;
        return new Rect(o.X + r.X * s, o.Y + r.Y * s, r.Width * s, r.Height * s);
    }

    private Point ToImage(Point p)
    {
        var s = Scale;
        var o = Origin;
        return new Point((p.X - o.X) / s, (p.Y - o.Y) / s);
    }

    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, ImageWidth), Math.Clamp(p.Y, 0, ImageHeight));

    // --- drawing ---

    protected override void OnRender(DrawingContext dc)
    {
        if (_source is null || ActualWidth < 1)
            return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // hit-testable everywhere
        var image = ToView(new Rect(0, 0, ImageWidth, ImageHeight));
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        dc.DrawImage(_source, image);
        var crop = ToView(_crop);
        dc.DrawGeometry(Dim, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(image), new RectangleGeometry(crop)));
        for (var i = 1; i < 3; i++)
        {
            dc.DrawLine(Thirds, new Point(crop.Left + crop.Width * i / 3, crop.Top), new Point(crop.Left + crop.Width * i / 3, crop.Bottom));
            dc.DrawLine(Thirds, new Point(crop.Left, crop.Top + crop.Height * i / 3), new Point(crop.Right, crop.Top + crop.Height * i / 3));
        }
        dc.DrawRectangle(null, Frame, crop);
        foreach (var (hx, hy) in Handles)
        {
            var c = HandlePoint(crop, hx, hy);
            dc.DrawRectangle(Brushes.White, HandleLine, new Rect(c.X - HandleSize / 2, c.Y - HandleSize / 2, HandleSize, HandleSize));
        }
    }

    private static readonly (int, int)[] Handles = [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)];

    private static Point HandlePoint(Rect r, int hx, int hy) =>
        new(hx < 0 ? r.Left : hx > 0 ? r.Right : r.Left + r.Width / 2, hy < 0 ? r.Top : hy > 0 ? r.Bottom : r.Top + r.Height / 2);

    // --- mouse ---

    private (int X, int Y)? HitHandle(Point view)
    {
        var crop = ToView(_crop);
        foreach (var (hx, hy) in Handles)
        {
            // Corners first (the array order), then edge midpoints; small frames keep corners reachable.
            var c = HandlePoint(crop, hx, hy);
            if (Math.Abs(view.X - c.X) <= Grab && Math.Abs(view.Y - c.Y) <= Grab)
                return (hx, hy);
        }
        // Along the edges, away from the corner handles.
        if (view.X >= crop.Left && view.X <= crop.Right)
        {
            if (Math.Abs(view.Y - crop.Top) <= 6)
                return (0, -1);
            if (Math.Abs(view.Y - crop.Bottom) <= 6)
                return (0, 1);
        }
        if (view.Y >= crop.Top && view.Y <= crop.Bottom)
        {
            if (Math.Abs(view.X - crop.Left) <= 6)
                return (-1, 0);
            if (Math.Abs(view.X - crop.Right) <= 6)
                return (1, 0);
        }
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_source is null)
            return;
        var view = e.GetPosition(this);
        var image = ToImage(view);
        _startRect = _crop;
        _grabPoint = image;
        _handle = HitHandle(view);
        _moving = _drawing = false;
        if (_handle is null)
        {
            if (ToView(_crop).Contains(view))
            {
                _moving = true;
            }
            else
            {
                _drawing = true;
                _anchor = Clamp(image);
            }
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_source is null)
            return;
        var view = e.GetPosition(this);
        if (!IsMouseCaptured)
        {
            Cursor = CursorFor(HitHandle(view), ToView(_crop).Contains(view));
            return;
        }
        var p = Clamp(ToImage(view));
        if (_moving)
        {
            var image = ToImage(view);
            _crop = new Rect(
                Math.Clamp(_startRect.X + image.X - _grabPoint.X, 0, ImageWidth - _startRect.Width),
                Math.Clamp(_startRect.Y + image.Y - _grabPoint.Y, 0, ImageHeight - _startRect.Height),
                _startRect.Width,
                _startRect.Height);
        }
        else if (_drawing)
        {
            var sx = p.X >= _anchor.X ? 1 : -1;
            var sy = p.Y >= _anchor.Y ? 1 : -1;
            _crop = FromAnchor(_anchor, sx, sy, p);
        }
        else if (_handle is { } h)
        {
            _crop = Resize(h, p);
        }
        else
        {
            return;
        }
        Changed();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        _handle = null;
        _moving = _drawing = false;
    }

    private static Cursor CursorFor((int X, int Y)? handle, bool inside) => handle switch
    {
        (-1, -1) or (1, 1) => Cursors.SizeNWSE,
        (1, -1) or (-1, 1) => Cursors.SizeNESW,
        (0, _) => Cursors.SizeNS,
        (_, 0) => Cursors.SizeWE,
        _ => inside ? Cursors.SizeAll : Cursors.Cross,
    };

    // --- resize maths (picture pixels) ---

    private double Min(double limit) => Math.Min(MinSize, limit);

    /// <summary>The frame spanning from a fixed corner towards <paramref name="p"/> in direction (sx, sy), keeping the ratio if set.</summary>
    private Rect FromAnchor(Point a, int sx, int sy, Point p)
    {
        var dx = Math.Max(0, (p.X - a.X) * sx);
        var dy = Math.Max(0, (p.Y - a.Y) * sy);
        var maxW = sx > 0 ? ImageWidth - a.X : a.X;
        var maxH = sy > 0 ? ImageHeight - a.Y : a.Y;
        double w, h;
        if (_ratio is { } r)
        {
            w = Math.Max(Math.Max(dx, dy * r), Min(maxW));
            w = Math.Min(w, Math.Min(maxW, maxH * r));
            h = w / r;
        }
        else
        {
            w = Math.Min(Math.Max(dx, Min(maxW)), maxW);
            h = Math.Min(Math.Max(dy, Min(maxH)), maxH);
        }
        return new Rect(sx > 0 ? a.X : a.X - w, sy > 0 ? a.Y : a.Y - h, w, h);
    }

    private Rect Resize((int X, int Y) handle, Point p)
    {
        var (hx, hy) = handle;
        var r = _startRect;
        if (hx != 0 && hy != 0)
        {
            var anchor = new Point(hx < 0 ? r.Right : r.Left, hy < 0 ? r.Bottom : r.Top);
            return FromAnchor(anchor, hx, hy, p);
        }
        if (_ratio is not { } q)
        {
            double l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
            if (hx < 0)
                l = Math.Clamp(p.X, 0, rt - Min(r.Width));
            if (hx > 0)
                rt = Math.Clamp(p.X, l + Min(r.Width), ImageWidth);
            if (hy < 0)
                t = Math.Clamp(p.Y, 0, b - Min(r.Height));
            if (hy > 0)
                b = Math.Clamp(p.Y, t + Min(r.Height), ImageHeight);
            return new Rect(l, t, rt - l, b - t);
        }
        if (hx != 0)
        {
            var x0 = hx < 0 ? r.Right : r.Left;
            var w = hx < 0 ? x0 - p.X : p.X - x0;
            var maxW = Math.Min(hx < 0 ? x0 : ImageWidth - x0, ImageHeight * q);
            w = Math.Clamp(w, Math.Min(MinSize, maxW), maxW);
            var h = w / q;
            var cy = r.Top + r.Height / 2;
            return new Rect(hx < 0 ? x0 - w : x0, Math.Clamp(cy - h / 2, 0, ImageHeight - h), w, h);
        }
        else
        {
            var y0 = hy < 0 ? r.Bottom : r.Top;
            var h = hy < 0 ? y0 - p.Y : p.Y - y0;
            var maxH = Math.Min(hy < 0 ? y0 : ImageHeight - y0, ImageWidth / q);
            h = Math.Clamp(h, Math.Min(MinSize, maxH), maxH);
            var w = h * q;
            var cx = r.Left + r.Width / 2;
            return new Rect(Math.Clamp(cx - w / 2, 0, ImageWidth - w), hy < 0 ? y0 - h : y0, w, h);
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
}
