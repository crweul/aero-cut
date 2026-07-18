using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AeroCut;

public partial class TrimTimeline : UserControl
{
    const double MinGap = 0.05;

    double _start;
    double _end;
    double _position;
    bool _draggingPlayhead;

    public double Duration { get; private set; }

    public event Action<double>? StartChanged;
    public event Action<double>? EndChanged;
    public event Action<double>? Seek;

    public double Start
    {
        get => _start;
        set { _start = value; Layout(); }
    }

    public double End
    {
        get => _end;
        set { _end = value; Layout(); }
    }

    public double Position
    {
        get => _position;
        set { if (_draggingPlayhead) return; _position = value; Layout(); }
    }

    public TrimTimeline()
    {
        InitializeComponent();
        StartThumb.DragDelta += (_, e) => Drag(true, e.HorizontalChange);
        EndThumb.DragDelta += (_, e) => Drag(false, e.HorizontalChange);
        PlayThumb.DragStarted += (_, _) => _draggingPlayhead = true;
        PlayThumb.DragCompleted += (_, _) => _draggingPlayhead = false;
        PlayThumb.DragDelta += (_, e) => DragPlayhead(e.HorizontalChange);
    }

    public void Setup(double duration, double start, double end)
    {
        Duration = duration;
        _start = start;
        _end = end;
        _position = start;
        Layout();
    }

    void OnSizeChanged(object sender, SizeChangedEventArgs e) => Layout();

    void Layout()
    {
        double w = Board.ActualWidth;
        double h = Board.ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        double d = Duration <= 0 ? 1 : Duration;
        double xs = Math.Clamp(_start / d, 0, 1) * w;
        double xe = Math.Clamp(_end / d, 0, 1) * w;
        double xp = Math.Clamp(_position / d, 0, 1) * w;

        double trackY = (h - Track.Height) / 2;
        Canvas.SetLeft(Track, 0);
        Canvas.SetTop(Track, trackY);
        Track.Width = w;

        Canvas.SetLeft(Selection, xs);
        Canvas.SetTop(Selection, trackY);
        Selection.Width = Math.Max(0, xe - xs);

        double thumbH = 28;
        double thumbY = (h - thumbH) / 2;
        Canvas.SetLeft(StartThumb, xs - StartThumb.Width / 2);
        Canvas.SetTop(StartThumb, thumbY);
        StartThumb.Height = thumbH;
        Canvas.SetLeft(EndThumb, xe - EndThumb.Width / 2);
        Canvas.SetTop(EndThumb, thumbY);
        EndThumb.Height = thumbH;

        Canvas.SetLeft(PlayThumb, xp - PlayThumb.Width / 2);
        Canvas.SetTop(PlayThumb, 0);
        PlayThumb.Height = h;
    }

    void DragPlayhead(double dx)
    {
        double w = Board.ActualWidth;
        if (w <= 0 || Duration <= 0)
            return;

        double dt = dx / w * Duration;
        _position = Math.Clamp(_position + dt, 0, Duration);
        Layout();
        Seek?.Invoke(_position);
    }

    void Drag(bool isStart, double dx)
    {
        double w = Board.ActualWidth;
        if (w <= 0 || Duration <= 0)
            return;

        double dt = dx / w * Duration;
        if (isStart)
        {
            _start = Math.Clamp(_start + dt, 0, _end - MinGap);
            _position = _start;
            Layout();
            StartChanged?.Invoke(_start);
        }
        else
        {
            _end = Math.Clamp(_end + dt, _start + MinGap, Duration);
            _position = _end;
            Layout();
            EndChanged?.Invoke(_end);
        }
    }

    void OnTrackClick(object sender, MouseButtonEventArgs e)
    {
        double w = Board.ActualWidth;
        if (w <= 0 || Duration <= 0)
            return;

        double x = e.GetPosition(Board).X;
        double t = Math.Clamp(x / w * Duration, 0, Duration);
        _position = t;
        Layout();
        Seek?.Invoke(t);
    }
}
