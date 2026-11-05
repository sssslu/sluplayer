using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SluPlayer.Core.Player;

namespace SluPlayer.UI.Controls;

public sealed class SeekRequestEventArgs(double time, bool exact) : EventArgs
{
    public double Time { get; } = time;
    /// <summary>false while dragging (fast keyframe seek), true on release.</summary>
    public bool Exact { get; } = exact;
}

public sealed class SeekHoverEventArgs(double time, double x, bool isOver) : EventArgs
{
    public double Time { get; } = time;
    /// <summary>X position in SeekBar coordinates.</summary>
    public double X { get; } = x;
    public bool IsOver { get; } = isOver;
}

/// <summary>
/// Video seek bar drawn in OnRender: track, buffered ranges, progress, chapter marks, thumb.
/// The hit area is taller than the visible track for easy grabbing.
/// </summary>
public sealed class SeekBar : FrameworkElement
{
    private const double TrackThin = 4, TrackThick = 6, ThumbSize = 14;
    private bool _dragging;
    private double _dragValue;
    private bool _hover;
    private double _hoverProgress; // 0..1 animated track thickness
    private long _lastDragSeekTicks;

    public event EventHandler<SeekRequestEventArgs>? SeekRequested;
    public event EventHandler<SeekHoverEventArgs>? HoverChanged;
    public event EventHandler? DragStarted;
    public event EventHandler? DragCompleted;

    static SeekBar()
    {
        FocusableProperty.OverrideMetadata(typeof(SeekBar), new FrameworkPropertyMetadata(false));
        CursorProperty.OverrideMetadata(typeof(SeekBar), new FrameworkPropertyMetadata(Cursors.Hand));
    }

    public SeekBar()
    {
        Height = 22;
        SnapsToDevicePixels = true;
    }

    // ---- dependency properties ------------------------------------------

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(SeekBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SeekBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Playback position (seconds). Ignored visually while the user drags.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public static readonly DependencyProperty BufferedRangesProperty = DependencyProperty.Register(
        nameof(BufferedRanges), typeof(IReadOnlyList<TimeRange>), typeof(SeekBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<TimeRange>? BufferedRanges
    {
        get => (IReadOnlyList<TimeRange>?)GetValue(BufferedRangesProperty);
        set => SetValue(BufferedRangesProperty, value);
    }

    public static readonly DependencyProperty ChaptersProperty = DependencyProperty.Register(
        nameof(Chapters), typeof(IReadOnlyList<ChapterInfo>), typeof(SeekBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChapterInfo>? Chapters
    {
        get => (IReadOnlyList<ChapterInfo>?)GetValue(ChaptersProperty);
        set => SetValue(ChaptersProperty, value);
    }

    private static readonly DependencyProperty HoverAmountProperty = DependencyProperty.Register(
        "HoverAmount", typeof(double), typeof(SeekBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, e) => ((SeekBar)d)._hoverProgress = (double)e.NewValue));

    public bool IsDragging => _dragging;

    // ---- geometry -----------------------------------------------------------

    private double Inset => ThumbSize / 2;
    private double TrackWidth => Math.Max(1, ActualWidth - 2 * Inset);

    private double XToTime(double x)
    {
        var max = Maximum;
        if (max <= 0) return 0;
        var p = Math.Clamp((x - Inset) / TrackWidth, 0, 1);
        return p * max;
    }

    private double TimeToX(double t)
    {
        var max = Maximum;
        if (max <= 0) return Inset;
        return Inset + Math.Clamp(t / max, 0, 1) * TrackWidth;
    }

    // ---- rendering ----------------------------------------------------------

    private static Brush Res(FrameworkElement fe, string key, Color fallback)
        => fe.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Transparent full-size hit target.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var accent = Res(this, "Brush.Accent", Color.FromRgb(0xFF, 0x6A, 0x33));
        var track = Res(this, "Brush.SeekTrack", Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
        var buffered = Res(this, "Brush.SeekBuffered", Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

        double thickness = TrackThin + (TrackThick - TrackThin) * _hoverProgress;
        double cy = Math.Round(h / 2);
        double top = cy - thickness / 2;
        double r = thickness / 2;
        double x0 = Inset, x1 = Inset + TrackWidth;

        bool hasMedia = Maximum > 0;
        dc.DrawRoundedRectangle(track, null, new Rect(x0, top, x1 - x0, thickness), r, r);
        if (!hasMedia) return;

        if (BufferedRanges is { Count: > 0 } ranges)
        {
            foreach (var range in ranges)
            {
                double bx0 = TimeToX(range.Start), bx1 = TimeToX(range.End);
                if (bx1 - bx0 > 0.5)
                    dc.DrawRoundedRectangle(buffered, null, new Rect(bx0, top, bx1 - bx0, thickness), r, r);
            }
        }

        double value = _dragging ? _dragValue : Value;
        double px = TimeToX(value);
        if (px > x0)
            dc.DrawRoundedRectangle(accent, null, new Rect(x0, top, px - x0, thickness), r, r);

        // Chapter separators: small gaps cut into the track.
        if (Chapters is { Count: > 1 } chapters)
        {
            var gap = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0));
            foreach (var ch in chapters)
            {
                if (ch.Time <= 0.5 || ch.Time >= Maximum - 0.5) continue;
                double cx = Math.Round(TimeToX(ch.Time));
                dc.DrawRectangle(gap, null, new Rect(cx - 1, top - 0.5, 2, thickness + 1));
            }
        }

        if (_hover || _dragging)
        {
            double size = ThumbSize * (0.6 + 0.4 * Math.Max(_hoverProgress, _dragging ? 1 : 0));
            dc.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), 1),
                new Point(px, cy), size / 2, size / 2);
        }
    }

    // ---- input --------------------------------------------------------------

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        AnimateHover(1);
        RaiseHover(e.GetPosition(this).X, true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        if (!_dragging) AnimateHover(0);
        HoverChanged?.Invoke(this, new SeekHoverEventArgs(0, 0, false));
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        double x = e.GetPosition(this).X;
        if (_dragging)
        {
            _dragValue = XToTime(x);
            InvalidateVisual();
            // Live preview: keyframe seeks at most ~12 per second while dragging.
            long now = Environment.TickCount64;
            if (now - _lastDragSeekTicks > 80)
            {
                _lastDragSeekTicks = now;
                SeekRequested?.Invoke(this, new SeekRequestEventArgs(_dragValue, exact: false));
            }
        }
        RaiseHover(x, true);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Maximum <= 0) return;
        _dragging = true;
        _dragValue = XToTime(e.GetPosition(this).X);
        CaptureMouse();
        _lastDragSeekTicks = Environment.TickCount64;
        DragStarted?.Invoke(this, EventArgs.Empty);
        SeekRequested?.Invoke(this, new SeekRequestEventArgs(_dragValue, exact: false));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragValue = XToTime(e.GetPosition(this).X);
        EndDrag(commit: true);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) EndDrag(commit: true);
    }

    private void EndDrag(bool commit)
    {
        if (!_dragging) return;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (commit)
        {
            // Keep showing the drag target until mpv reports the new position.
            SetCurrentValue(ValueProperty, _dragValue);
            SeekRequested?.Invoke(this, new SeekRequestEventArgs(_dragValue, exact: true));
        }
        if (!_hover) AnimateHover(0);
        DragCompleted?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void RaiseHover(double x, bool over)
    {
        if (Maximum <= 0) return;
        double clampedX = Math.Clamp(x, Inset, Inset + TrackWidth);
        HoverChanged?.Invoke(this, new SeekHoverEventArgs(XToTime(x), clampedX, over));
    }

    private void AnimateHover(double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase() };
        BeginAnimation(HoverAmountProperty, anim);
    }
}
