using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;

namespace AeroCut;

public partial class CropEditor : UserControl
{
    const int MinSize = 16;

    int _videoW;
    int _videoH;
    int _cx;
    int _cy;
    int _cw;
    int _ch;
    double _scale;
    double _offX;
    double _offY;

    public int CropX => _cx;
    public int CropY => _cy;
    public int CropW => _cw;
    public int CropH => _ch;

    public event Action? CropChanged;

    public CropEditor()
    {
        InitializeComponent();
        CropBody.DragDelta += (_, e) => Move(e.HorizontalChange, e.VerticalChange);
        TL.DragDelta += (_, e) => Resize(e.HorizontalChange, e.VerticalChange, true, true);
        TR.DragDelta += (_, e) => Resize(e.HorizontalChange, e.VerticalChange, false, true);
        BL.DragDelta += (_, e) => Resize(e.HorizontalChange, e.VerticalChange, true, false);
        BR.DragDelta += (_, e) => Resize(e.HorizontalChange, e.VerticalChange, false, false);
    }

    public void SetImage(BitmapSource image)
    {
        Frame.Source = image;
        _videoW = image.PixelWidth;
        _videoH = image.PixelHeight;
    }

    public void SetCrop(int x, int y, int w, int h)
    {
        _cw = Math.Clamp(w, MinSize, _videoW);
        _ch = Math.Clamp(h, MinSize, _videoH);
        _cx = Math.Clamp(x, 0, _videoW - _cw);
        _cy = Math.Clamp(y, 0, _videoH - _ch);
        Layout();
        CropChanged?.Invoke();
    }

    void OnSizeChanged(object sender, SizeChangedEventArgs e) => Layout();

    void Layout()
    {
        double cw = Board.ActualWidth;
        double ch = Board.ActualHeight;
        if (cw <= 0 || ch <= 0 || _videoW <= 0 || _videoH <= 0)
            return;

        _scale = Math.Min(cw / _videoW, ch / _videoH);
        double dispW = _videoW * _scale;
        double dispH = _videoH * _scale;
        _offX = (cw - dispW) / 2;
        _offY = (ch - dispH) / 2;

        Canvas.SetLeft(Frame, _offX);
        Canvas.SetTop(Frame, _offY);
        Frame.Width = dispW;
        Frame.Height = dispH;

        double rx = _offX + _cx * _scale;
        double ry = _offY + _cy * _scale;
        double rw = _cw * _scale;
        double rh = _ch * _scale;

        Canvas.SetLeft(CropBody, rx);
        Canvas.SetTop(CropBody, ry);
        CropBody.Width = rw;
        CropBody.Height = rh;

        Place(TL, rx, ry);
        Place(TR, rx + rw, ry);
        Place(BL, rx, ry + rh);
        Place(BR, rx + rw, ry + rh);
    }

    static void Place(Thumb thumb, double x, double y)
    {
        Canvas.SetLeft(thumb, x - thumb.Width / 2);
        Canvas.SetTop(thumb, y - thumb.Height / 2);
    }

    void Move(double dx, double dy)
    {
        if (_scale <= 0)
            return;
        _cx = Math.Clamp(_cx + (int)Math.Round(dx / _scale), 0, _videoW - _cw);
        _cy = Math.Clamp(_cy + (int)Math.Round(dy / _scale), 0, _videoH - _ch);
        Layout();
        CropChanged?.Invoke();
    }

    void Resize(double dx, double dy, bool left, bool top)
    {
        if (_scale <= 0)
            return;
        int sdx = (int)Math.Round(dx / _scale);
        int sdy = (int)Math.Round(dy / _scale);
        int rightEdge = _cx + _cw;
        int bottomEdge = _cy + _ch;

        if (left)
        {
            _cx = Math.Clamp(_cx + sdx, 0, rightEdge - MinSize);
            _cw = rightEdge - _cx;
        }
        else
        {
            _cw = Math.Clamp(_cw + sdx, MinSize, _videoW - _cx);
        }

        if (top)
        {
            _cy = Math.Clamp(_cy + sdy, 0, bottomEdge - MinSize);
            _ch = bottomEdge - _cy;
        }
        else
        {
            _ch = Math.Clamp(_ch + sdy, MinSize, _videoH - _cy);
        }

        Layout();
        CropChanged?.Invoke();
    }
}
