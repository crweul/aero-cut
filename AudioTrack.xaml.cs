using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AeroCut;

public partial class AudioTrack : UserControl
{
    const double MaxVol = 300;

    class Key
    {
        public double Time;
        public double Volume;
        public Thumb Thumb = null!;
    }

    readonly List<Key> _keys = new();
    Key? _selected;
    bool _draggingPlayhead;
    double _position;
    bool _keyframed;
    double _globalVolume = 100;

    public double Duration { get; private set; }

    public event Action<double>? Seek;
    public event Action? KeyframesChanged;
    public event Action? SelectionChanged;
    public event Action<double>? GlobalVolumeChanged;

    public bool Keyframed => _keyframed;
    public double GlobalVolume => _globalVolume;

    public bool HasSelection => _selected != null;
    public double SelectedTime => _selected?.Time ?? 0;
    public double SelectedVolume => _selected?.Volume ?? 100;

    public IReadOnlyList<(double Time, double Volume)> Keyframes
        => _keys.OrderBy(k => k.Time).Select(k => (k.Time, k.Volume)).ToList();

    public double Position
    {
        get => _position;
        set { if (_draggingPlayhead) return; _position = value; Layout(); }
    }

    public AudioTrack()
    {
        InitializeComponent();
        PlayThumb.DragStarted += (_, _) => _draggingPlayhead = true;
        PlayThumb.DragCompleted += (_, _) => _draggingPlayhead = false;
        PlayThumb.DragDelta += (_, e) => DragPlayhead(e.HorizontalChange);
        GlobalBar.DragDelta += (_, e) => DragGlobal(e.VerticalChange);
        Panel.SetZIndex(GlobalBar, 10);
        Panel.SetZIndex(PlayThumb, 30);
    }

    public void Setup(double duration)
    {
        Duration = duration;
        Layout();
    }

    public void SetGlobalMode(double volume)
    {
        _keyframed = false;
        _globalVolume = Math.Clamp(volume, 0, MaxVol);
        _selected = null;
        Layout();
    }

    public void SetKeyframeMode()
    {
        _keyframed = true;
        Layout();
    }

    public void SetGlobalVolume(double volume)
    {
        _globalVolume = Math.Clamp(volume, 0, MaxVol);
        Layout();
        GlobalVolumeChanged?.Invoke(_globalVolume);
    }

    public void SetWaveform(BitmapSource image) => Wave.Source = image;

    public void Clear()
    {
        foreach (var k in _keys)
            Layer.Children.Remove(k.Thumb);
        _keys.Clear();
        _selected = null;
        Layout();
        SelectionChanged?.Invoke();
        KeyframesChanged?.Invoke();
    }

    public void AddKeyframe(double time, double volume)
    {
        var key = new Key
        {
            Time = Math.Clamp(time, 0, Duration),
            Volume = Math.Clamp(volume, 0, MaxVol),
            Thumb = new Thumb { Style = (Style)Resources["KfThumb"] }
        };
        key.Thumb.DragStarted += (_, _) => Select(key);
        key.Thumb.DragDelta += (_, e) => DragKey(key, e.HorizontalChange, e.VerticalChange);
        Layer.Children.Add(key.Thumb);
        _keys.Add(key);
        Select(key);
        Layout();
        KeyframesChanged?.Invoke();
    }

    public void RemoveSelected()
    {
        if (_selected == null)
            return;
        Layer.Children.Remove(_selected.Thumb);
        _keys.Remove(_selected);
        _selected = null;
        Layout();
        SelectionChanged?.Invoke();
        KeyframesChanged?.Invoke();
    }

    public void UpdateSelected(double time, double volume)
    {
        if (_selected == null)
            return;
        _selected.Time = Math.Clamp(time, 0, Duration);
        _selected.Volume = Math.Clamp(volume, 0, MaxVol);
        Layout();
        SelectionChanged?.Invoke();
        KeyframesChanged?.Invoke();
    }

    void Select(Key? key)
    {
        if (_selected != null)
            _selected.Thumb.Style = (Style)Resources["KfThumb"];
        _selected = key;
        if (_selected != null)
            _selected.Thumb.Style = (Style)Resources["KfThumbSelected"];
        SelectionChanged?.Invoke();
    }

    void OnSizeChanged(object sender, SizeChangedEventArgs e) => Layout();

    double XOf(double time) => Duration <= 0 ? 0 : Math.Clamp(time / Duration, 0, 1) * Board.ActualWidth;

    double YOf(double vol) => Board.ActualHeight * (1 - Math.Clamp(vol, 0, MaxVol) / MaxVol);

    void Layout()
    {
        double w = Board.ActualWidth;
        double h = Board.ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        if (_keyframed)
        {
            GlobalBar.Visibility = Visibility.Collapsed;
            var sorted = _keys.OrderBy(k => k.Time).ToList();
            if (sorted.Count > 0)
            {
                var stepped = new PointCollection { new Point(0, YOf(sorted[0].Volume)) };
                double prevY = YOf(sorted[0].Volume);
                foreach (var k in sorted)
                {
                    double x = XOf(k.Time);
                    double y = YOf(k.Volume);
                    stepped.Add(new Point(x, prevY));
                    stepped.Add(new Point(x, y));
                    prevY = y;
                }
                stepped.Add(new Point(w, prevY));
                StepLine.Points = stepped;
            }
            else
            {
                StepLine.Points = new PointCollection();
            }

            foreach (var k in _keys)
            {
                k.Thumb.Visibility = Visibility.Visible;
                Canvas.SetLeft(k.Thumb, XOf(k.Time) - k.Thumb.Width / 2);
                Canvas.SetTop(k.Thumb, YOf(k.Volume) - k.Thumb.Height / 2);
            }
        }
        else
        {
            foreach (var k in _keys)
                k.Thumb.Visibility = Visibility.Collapsed;

            double y = YOf(_globalVolume);
            StepLine.Points = new PointCollection { new Point(0, y), new Point(w, y) };
            GlobalBar.Visibility = Visibility.Visible;
            GlobalBar.Width = w;
            Canvas.SetLeft(GlobalBar, 0);
            Canvas.SetTop(GlobalBar, y - GlobalBar.Height / 2);
        }

        Canvas.SetLeft(PlayThumb, XOf(_position) - PlayThumb.Width / 2);
        Canvas.SetTop(PlayThumb, 0);
        PlayThumb.Height = h;
    }

    void DragGlobal(double dy)
    {
        double h = Board.ActualHeight;
        if (h <= 0)
            return;
        _globalVolume = Math.Clamp(_globalVolume - dy / h * MaxVol, 0, MaxVol);
        Layout();
        GlobalVolumeChanged?.Invoke(_globalVolume);
    }

    void DragPlayhead(double dx)
    {
        double w = Board.ActualWidth;
        if (w <= 0 || Duration <= 0)
            return;
        _position = Math.Clamp(_position + dx / w * Duration, 0, Duration);
        Layout();
        Seek?.Invoke(_position);
    }

    void DragKey(Key key, double dx, double dy)
    {
        double w = Board.ActualWidth;
        double h = Board.ActualHeight;
        if (w <= 0 || h <= 0 || Duration <= 0)
            return;

        var sorted = _keys.OrderBy(k => k.Time).ToList();
        int idx = sorted.IndexOf(key);
        double lo = idx > 0 ? sorted[idx - 1].Time + 0.01 : 0;
        double hi = idx < sorted.Count - 1 ? sorted[idx + 1].Time - 0.01 : Duration;

        key.Time = Math.Clamp(key.Time + dx / w * Duration, lo, hi);
        key.Volume = Math.Clamp(key.Volume - dy / h * MaxVol, 0, MaxVol);
        Layout();
        SelectionChanged?.Invoke();
        KeyframesChanged?.Invoke();
    }

    void OnBoardDown(object sender, MouseButtonEventArgs e)
    {
        if (!_keyframed || SourceIsThumb(e.OriginalSource as DependencyObject))
            return;

        if (e.ClickCount == 2 && Duration > 0)
        {
            var p = e.GetPosition(Board);
            double time = Math.Clamp(p.X / Board.ActualWidth * Duration, 0, Duration);
            double vol = Math.Clamp((1 - p.Y / Board.ActualHeight) * MaxVol, 0, MaxVol);
            AddKeyframe(time, vol);
        }
        else
        {
            Select(null);
        }
    }

    static bool SourceIsThumb(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Thumb)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }
}
