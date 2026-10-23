using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Radii;

/// <summary>
/// Smooth wheel scrolling for a <see cref="ScrollViewer"/>: a smaller step per notch (36 DIP instead of 48 * 3), eased towards
/// the target so touchpad gestures (many tiny deltas) scroll fluidly instead of jumping.
/// </summary>
internal sealed class SmoothScroll
{
    private readonly ScrollViewer _scroll;
    private double _target;
    private bool _running;

    private SmoothScroll(ScrollViewer scroll)
    {
        _scroll = scroll;
        scroll.PreviewMouseWheel += OnWheel;
        scroll.Unloaded += (_, _) => Stop();
    }

    public static void Attach(ScrollViewer scroll) => _ = new SmoothScroll(scroll);

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_scroll.ScrollableHeight <= 0)
            return;
        e.Handled = true;
        if (!_running)
            _target = _scroll.VerticalOffset;
        _target = Math.Clamp(_target - e.Delta * 0.3, 0, _scroll.ScrollableHeight);
        if (!_running)
        {
            _running = true;
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
            Stop();
        }
        _scroll.ScrollToVerticalOffset(next);
    }

    private void Stop()
    {
        if (!_running)
            return;
        _running = false;
        CompositionTarget.Rendering -= Step;
    }
}
