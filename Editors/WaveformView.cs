using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Radii.Editors;

/// <summary>Waveform with a selection (two draggable edges) and a play cursor.</summary>
internal sealed class WaveformView : FrameworkElement
{
    private const double LabelBand = 20, GripWidth = 9, Grab = 8;

    private float[] _peaks = [];
    private double _duration;
    private (bool Start, bool End, bool New, bool Click)? _drag;
    private double _downX, _anchorTime;

    public Brush Accent { get; set; } = Brushes.DodgerBlue;
    public Brush Idle { get; set; } = Brushes.Gray;
    public Brush LabelBrush { get; set; } = Brushes.Gray;

    public double Start { get; private set; }
    public double End { get; private set; }

    public double Position
    {
        get => _position;
        set
        {
            _position = value;
            InvalidateVisual();
        }
    }

    private double _position;

    public event Action? SelectionChanged;
    public event Action<double>? SeekRequested;

    public void SetData(float[] peaks, double duration)
    {
        _peaks = peaks;
        _duration = Math.Max(0.1, duration);
        Start = 0;
        End = _duration;
        _position = 0;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public void SetSelection(double start, double end)
    {
        start = Math.Clamp(start, 0, _duration);
        end = Math.Clamp(end, 0, _duration);
        if (end - start < 0.05)
        {
            // Keep a usable piece: move whichever edge was edited away from the other.
            if (start >= _duration - 0.05)
                start = Math.Max(0, _duration - 0.05);
            end = Math.Min(_duration, start + 0.05);
        }
        Start = start;
        End = end;
        if (_position < Start || _position > End)
            _position = Start;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    private double TimeAt(double x) => Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * _duration;

    private double XAt(double time) => time / _duration * ActualWidth;

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (_peaks.Length == 0 || width < 4)
            return;

        var area = height - LabelBand;
        var middle = area / 2;
        var from = XAt(Start);
        var to = XAt(End);
        var pitch = 3.0;
        var bar = 2.0;
        for (double x = 0; x < width; x += pitch)
        {
            var first = (int)(x / width * _peaks.Length);
            var last = Math.Max(first + 1, (int)((x + pitch) / width * _peaks.Length));
            float peak = 0;
            for (var i = first; i < last && i < _peaks.Length; i++)
                peak = Math.Max(peak, _peaks[i]);
            var h = Math.Max(2, peak * (area - 14));
            dc.DrawRectangle(x + bar / 2 >= from && x <= to ? Accent : Idle, null, new Rect(x, middle - h / 2, bar, h));
        }

        // selection wash and edges
        var wash = Accent.CloneCurrentValue();
        wash.Opacity = 0.14;
        dc.DrawRectangle(wash, null, new Rect(from, 0, Math.Max(0, to - from), area));
        var edge = new Pen(Accent, 2);
        dc.DrawLine(edge, new Point(from, 0), new Point(from, area));
        dc.DrawLine(edge, new Point(to, 0), new Point(to, area));
        dc.DrawRoundedRectangle(Accent, null, new Rect(from - GripWidth, area / 2 - 14, GripWidth, 28), 3, 3);
        dc.DrawRoundedRectangle(Accent, null, new Rect(to, area / 2 - 14, GripWidth, 28), 3, 3);

        // play cursor
        var cursor = XAt(Math.Clamp(_position, 0, _duration));
        dc.DrawLine(new Pen(Brushes.White, 1.5), new Point(cursor, 0), new Point(cursor, area));

        // time labels
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var i = 0; i <= 4; i++)
        {
            var text = new FormattedText(Label(_duration * i / 4), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, LabelBrush, dpi);
            var x = Math.Clamp(width * i / 4 - text.Width / 2, 2, width - text.Width - 2);
            dc.DrawText(text, new Point(x, area + 3));
        }
    }

    private static string Label(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Round(seconds));
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_peaks.Length == 0)
            return;
        var x = e.GetPosition(this).X;
        _downX = x;
        var nearStart = Math.Abs(x - XAt(Start)) <= Grab + GripWidth / 2;
        var nearEnd = Math.Abs(x - XAt(End)) <= Grab + GripWidth / 2;
        if (nearStart && nearEnd)
            nearStart = x < (XAt(Start) + XAt(End)) / 2;
        _drag = nearStart ? (true, false, false, false) : nearEnd ? (false, true, false, false) : (false, false, false, true);
        _anchorTime = TimeAt(x);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_peaks.Length == 0)
            return;
        var x = e.GetPosition(this).X;
        if (_drag is not { } drag)
        {
            var near = Math.Abs(x - XAt(Start)) <= Grab + GripWidth / 2 || Math.Abs(x - XAt(End)) <= Grab + GripWidth / 2;
            Cursor = near ? Cursors.SizeWE : Cursors.Arrow;
            return;
        }
        var time = TimeAt(x);
        if (drag.Start)
        {
            SetSelection(Math.Min(time, End - 0.05), End);
        }
        else if (drag.End)
        {
            SetSelection(Start, Math.Max(time, Start + 0.05));
        }
        else if (drag.Click && Math.Abs(x - _downX) > 4)
        {
            _drag = (false, false, true, false);
            SetSelection(Math.Min(_anchorTime, time), Math.Max(_anchorTime, time));
        }
        else if (drag.New)
        {
            SetSelection(Math.Min(_anchorTime, time), Math.Max(_anchorTime, time));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag is { Click: true })
            SeekRequested?.Invoke(Math.Clamp(TimeAt(e.GetPosition(this).X), Start, End));
        _drag = null;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
}
