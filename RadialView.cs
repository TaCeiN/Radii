using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Radii;

/// <param name="Backdrop">Screenshot of what is behind the ring; blurred inside the sectors when set.</param>
internal sealed record ViewOptions(bool ShowHints = true, bool Animations = true, ImageSource? Backdrop = null);

/// <summary>The ring itself: annular sectors with a Fluent glyph each and a label disc in the middle.</summary>
internal sealed class RadialView : Canvas
{
    public const double Outer = 130;
    public const double Inner = 48;
    public const double Pad = 30;
    public const double Size = 2 * (Outer + Pad);

    private const double GapDegrees = 2.2;
    private static readonly Point Center = new(Size / 2, Size / 2);
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");

    private readonly IReadOnlyList<RadialItem> _items;
    private readonly Theme _theme;
    private readonly ViewOptions _options;
    private readonly string _summary;
    private readonly Path[] _sectors;
    private readonly ScaleTransform[] _sectorScale;
    private readonly TextBlock[] _glyphs;
    private readonly ScaleTransform[] _glyphScale;
    private readonly TextBlock _label;
    private int _hover = -1;

    public RadialView(IReadOnlyList<RadialItem> items, Theme theme, string summary, ViewOptions? options = null)
    {
        _items = items;
        _theme = theme;
        _summary = summary;
        _options = options ?? new ViewOptions();
        Width = Height = Size;
        // Nearly transparent but not fully: clicks in the gaps and padding must hit this window, not the one below.
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

        var ring = new Canvas
        {
            Width = Size,
            Height = Size,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = theme.Dark ? 0.55 : 0.28, Color = Colors.Black },
        };
        Children.Add(ring);

        var step = 360.0 / items.Count;
        var geometries = Enumerable.Range(0, items.Count).Select(i => Sector(i, step)).ToArray();
        var discRadius = Inner - 5;
        var discGeometry = new EllipseGeometry(Center, discRadius, discRadius);

        if (_options.Backdrop is not null)
        {
            // The blurred screenshot is clipped by the container: a clip on the blurred element itself would feather the edges.
            var glass = new Canvas { Width = Size, Height = Size, Clip = new GeometryGroup { Children = new GeometryCollection(geometries.Append(discGeometry)) } };
            glass.Children.Add(new Image
            {
                Source = _options.Backdrop,
                Width = Size,
                Height = Size,
                Stretch = Stretch.Fill,
                Effect = new BlurEffect { Radius = 26, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
            });
            ring.Children.Add(glass);
        }

        var disc = new Ellipse { Width = discRadius * 2, Height = discRadius * 2, Fill = theme.Sector, Stroke = theme.Border, StrokeThickness = 1 };
        SetLeft(disc, Center.X - discRadius);
        SetTop(disc, Center.Y - discRadius);
        ring.Children.Add(disc);

        _sectors = new Path[items.Count];
        _sectorScale = new ScaleTransform[items.Count];
        _glyphs = new TextBlock[items.Count];
        _glyphScale = new ScaleTransform[items.Count];
        var glyphRadius = (Inner + Outer) / 2;
        for (var i = 0; i < items.Count; i++)
        {
            _sectorScale[i] = new ScaleTransform(1, 1, Center.X, Center.Y);
            _sectors[i] = new Path
            {
                Data = geometries[i],
                Fill = theme.Sector,
                Stroke = theme.Border,
                StrokeThickness = 1,
                StrokeLineJoin = PenLineJoin.Round,
                RenderTransform = _sectorScale[i],
            };
            ring.Children.Add(_sectors[i]);

            _glyphScale[i] = new ScaleTransform(1, 1);
            // Fluent icons are single private-use characters; anything else (a format name such as "PNG") is drawn as text.
            var textual = items[i].Glyph.Length > 1 || (items[i].Glyph.Length == 1 && char.IsAsciiLetterOrDigit(items[i].Glyph[0]));
            var glyph = new TextBlock
            {
                Text = items[i].Glyph,
                FontFamily = textual ? TextFont : IconFont,
                FontSize = textual ? 14 : 22,
                FontWeight = textual ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = theme.Text,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _glyphScale[i],
            };
            glyph.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var middle = PointAt(glyphRadius, i * step);
            SetLeft(glyph, middle.X - glyph.DesiredSize.Width / 2);
            SetTop(glyph, middle.Y - glyph.DesiredSize.Height / 2);
            Children.Add(glyph);
            _glyphs[i] = glyph;

            if (_options.ShowHints && i < 9)
            {
                // Number keys 1-9 pick a sector; the digit sits on the outer rim.
                var hint = new TextBlock { Text = (i + 1).ToString(), FontFamily = TextFont, FontSize = 9, Foreground = theme.TextMuted, IsHitTestVisible = false };
                hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var rim = PointAt(Outer - 11, i * step);
                SetLeft(hint, rim.X - hint.DesiredSize.Width / 2);
                SetTop(hint, rim.Y - hint.DesiredSize.Height / 2);
                Children.Add(hint);
            }
        }

        _label = new TextBlock
        {
            FontFamily = TextFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = theme.Text,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = (Inner - 11) * 2,
            MaxHeight = 52,
            IsHitTestVisible = false,
        };
        SetLeft(_label, Center.X - _label.Width / 2);
        Children.Add(_label);
        ShowLabel(_summary, muted: true);
    }

    public int Count => _items.Count;

    public int Hovered => _hover;

    /// <summary>Centre of the ring in view coordinates.</summary>
    public static Point CenterPoint => Center;

    public int HitTest(Point p)
    {
        double dx = p.X - Center.X, dy = p.Y - Center.Y;
        var radius = Math.Sqrt(dx * dx + dy * dy);
        if (radius < Inner || radius > Outer)
            return -1;
        var degrees = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
        var step = 360.0 / _items.Count;
        return (int)((degrees + step / 2) % 360 / step) % _items.Count;
    }

    public RadialItem? ItemAt(int index) => index >= 0 && index < _items.Count ? _items[index] : null;

    public void SetHover(int index)
    {
        if (index == _hover)
            return;
        if (_hover >= 0)
            Paint(_hover, hovered: false);
        _hover = index;
        if (index >= 0)
        {
            Paint(index, hovered: true);
            ShowLabel(_items[index].Label, muted: false);
        }
        else
        {
            ShowLabel(_summary, muted: true);
        }
    }

    private void Paint(int index, bool hovered)
    {
        _sectors[index].Fill = hovered ? (_items[index].Danger ? _theme.DangerHover : _theme.Hover) : _theme.Sector;
        _glyphs[index].Foreground = hovered ? _theme.OnAccent : _theme.Text;
        Scale(_sectorScale[index], hovered ? 1.05 : 1.0);
        Scale(_glyphScale[index], hovered ? 1.16 : 1.0);
        Panel.SetZIndex(_sectors[index], hovered ? 1 : 0);
    }

    private void Scale(ScaleTransform transform, double target)
    {
        if (!_options.Animations)
        {
            transform.ScaleX = transform.ScaleY = target;
            return;
        }
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(110)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void ShowLabel(string text, bool muted)
    {
        _label.Text = text;
        _label.Foreground = muted ? _theme.TextMuted : _theme.Text;
        _label.Measure(new Size(_label.Width, double.PositiveInfinity));
        SetTop(_label, Center.Y - _label.DesiredSize.Height / 2);
    }

    private static Point PointAt(double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(Center.X + radius * Math.Sin(radians), Center.Y - radius * Math.Cos(radians));
    }

    /// <summary>Sector <paramref name="index"/> is centred on <c>index * step</c> degrees clockwise from the top.</summary>
    private static Geometry Sector(int index, double step)
    {
        var from = index * step - step / 2 + GapDegrees / 2;
        var to = index * step + step / 2 - GapDegrees / 2;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(PointAt(Outer, from), isFilled: true, isClosed: true);
            context.ArcTo(PointAt(Outer, to), new Size(Outer, Outer), 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(PointAt(Inner, to), true, false);
            context.ArcTo(PointAt(Inner, from), new Size(Inner, Inner), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
