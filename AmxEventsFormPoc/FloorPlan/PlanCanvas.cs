using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;
using SkiaSharp;

namespace AmxEventsFormPoc.FloorPlan;

// A device as the canvas sees it: where it is (plan coordinates), how it looks.
public sealed class PlotItem
{
    public string Key { get; init; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public AmxSymbol Symbol { get; set; }
    public DeviceState State { get; set; }
}

// The floor plan view: draws the plan with Skia into an off-screen bitmap and puts
// that on the control, with the devices drawn on top at a fixed pixel size.
//  - mouse wheel zooms at the cursor, left-drag on empty space pans
//  - left-drag on a device moves it (when AllowEdit), a click selects it
//  - devices dragged in from the device list are dropped where the mouse is
//  - DefineArea() turns the next drag into a rubber band (to cut one floor out of
//    a multi-floor drawing)
// Plan coordinates are drawing units, Y up; the screen is Y down.
public sealed class PlanCanvas : Control
{
    public const string DragFormat = "DraxPlanDevice";

    public event Action<string>? DeviceClicked;
    public event Action? BackgroundClicked;
    public event Action<string, float, float>? DeviceMoved;
    public event Action<string, float, float>? DeviceDropped;
    public event Action<SKRect>? AreaDefined;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowEdit { get; set; } = true;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? SelectedKey { get => _selected; set { _selected = value; Invalidate(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string StatusText { get; set; } = "";

    private SKPicture? _picture;
    private SKRect _content = new(0, 0, 1000, 1000);
    private List<PlotItem> _items = new();

    // View: pixels per drawing unit, and the plan point at the centre of the control.
    private float _scale = 1;
    private float _cx, _cy;

    // The plan drawn at a given view, reused while panning/zooming and redrawn
    // sharply once the view settles: a big drawing takes a moment to draw in full.
    private SKImage? _planCache;
    private (float Scale, float Cx, float Cy, int W, int H) _cacheView;
    private readonly System.Windows.Forms.Timer _settle = new() { Interval = 120 };
    private readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 15 };
    private bool _flash;
    private string? _selected;

    private Bitmap? _buffer;

    private enum Drag { None, Pan, Device, Area }
    private Drag _drag;
    private Point _down, _last;
    private bool _moved;
    private string? _dragKey;
    private Rectangle _band;
    private bool _areaMode;

    private (float S0, float X0, float Y0, float S1, float X1, float Y1, DateTime Start)? _anim;

    public PlanCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AllowDrop = true;
        BackColor = Color.White;
        _settle.Tick += (_, _) => { _settle.Stop(); RenderPlanCache(); Invalidate(); };
        _flashTimer.Tick += (_, _) => { _flash = !_flash; if (_items.Any(i => i.State != DeviceState.Normal)) Invalidate(); };
        _flashTimer.Start();
        _animTimer.Tick += (_, _) => StepAnimation();
    }

    // ------------------------------------------------------------------ public

    public void SetPlan(SKPicture? picture, SKRect content)
    {
        _picture?.Dispose();
        _picture = picture;
        _content = content;
        FitToContent();
    }

    public void SetItems(IEnumerable<PlotItem> items)
    {
        _items = items.ToList();
        Invalidate();
    }

    // A fit asked for while the control is hidden or tiny (the page starts hidden)
    // is held until it has a real size, otherwise the plan is fitted into a few pixels.
    private bool _fitPending;

    public void FitToContent()
    {
        if (_content.Width <= 0 || _content.Height <= 0) return;
        if (!Visible || Width < 200 || Height < 150) { _fitPending = true; return; }
        _fitPending = false;
        _scale = Math.Min(Width / _content.Width, Height / _content.Height) * 0.95f;
        _cx = _content.MidX;
        _cy = _content.MidY;
        ViewChanged(immediate: true);
    }

    // Smoothly zooms to a point, used when a device goes into alarm. Zooms so roughly
    // a room-sized area is on screen, but never further out than the current view.
    public void ZoomTo(float x, float y)
    {
        float target = Math.Max(_scale, Math.Min(Width, Height) / (Math.Max(_content.Width, _content.Height) / 6f));
        _anim = (_scale, _cx, _cy, target, x, y, DateTime.Now);
        _animTimer.Start();
    }

    public void DefineArea()
    {
        _areaMode = true;
        Cursor = Cursors.Cross;
    }

    public SKPoint ScreenToPlan(Point p) =>
        new((p.X - Width / 2f) / _scale + _cx, -(p.Y - Height / 2f) / _scale + _cy);

    private SKPoint PlanToScreen(float x, float y) =>
        new((x - _cx) * _scale + Width / 2f, -(y - _cy) * _scale + Height / 2f);

    private float SymbolRadius => Math.Max(6f, 9f * DeviceDpi / 96f);

    // ----------------------------------------------------------------- drawing

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        if (_buffer == null || _buffer.Width != Width || _buffer.Height != Height)
        {
            _buffer?.Dispose();
            _buffer = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        }

        var data = _buffer.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul), data.Scan0, data.Stride);
            Draw(surface.Canvas);
        }
        finally
        {
            _buffer.UnlockBits(data);
        }
        e.Graphics.DrawImageUnscaled(_buffer, 0, 0);
    }

    private void Draw(SKCanvas canvas)
    {
        canvas.Clear(SKColors.White);

        if (_picture == null)
        {
            using var p = new SKPaint { Color = SKColors.Gray, IsAntialias = true };
            using var f = new SKFont { Size = 16 };
            canvas.DrawText("Open a drawing to start: Plan > Open drawing", Width / 2f, Height / 2f, SKTextAlign.Center, f, p);
            return;
        }

        if (_planCache == null || _cacheView.W != Width || _cacheView.H != Height)
            RenderPlanCache();

        // Draw the cached plan, shifted/scaled to the current view if it has moved
        // since it was rendered (a sharp re-render follows when the view settles).
        if (_planCache != null)
        {
            var cv = _cacheView;
            float k = _scale / cv.Scale;
            canvas.Save();
            canvas.Translate(Width / 2f, Height / 2f);
            canvas.Scale(k);
            canvas.Translate((cv.Cx - _cx) * cv.Scale - cv.W / 2f, -(cv.Cy - _cy) * cv.Scale - cv.H / 2f);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawImage(_planCache, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);
            canvas.Restore();
        }

        float r = SymbolRadius;
        foreach (var item in _items)
        {
            var s = PlanToScreen(item.X, item.Y);
            if (s.X < -40 || s.Y < -40 || s.X > Width + 40 || s.Y > Height + 40) continue;
            AmxSymbols.Draw(canvas, item.Symbol, s.X, s.Y, r, item.State, _flash, item.Key == _selected);
        }

        if (_drag == Drag.Area)
        {
            using var band = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(0x1E, 0x6F, 0xD9), StrokeWidth = 2, PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0) };
            canvas.DrawRect(_band.X, _band.Y, _band.Width, _band.Height, band);
        }

        if (!string.IsNullOrEmpty(StatusText))
        {
            using var p = new SKPaint { Color = new SKColor(90, 90, 90), IsAntialias = true };
            using var f = new SKFont { Size = 12 };
            using var back = new SKPaint { Color = SKColors.White.WithAlpha(230) };
            float w = f.MeasureText(StatusText);
            canvas.DrawRect(0, Height - 24, w + 16, 24, back);
            canvas.DrawText(StatusText, 8, Height - 8, SKTextAlign.Left, f, p);
        }
    }

    private void RenderPlanCache()
    {
        if (_picture == null || Width <= 0 || Height <= 0) return;
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(SKColors.White);
        c.Translate(Width / 2f, Height / 2f);
        c.Scale(_scale, -_scale);
        c.Translate(-_cx, -_cy);
        c.DrawPicture(_picture);
        _planCache?.Dispose();
        _planCache = surface.Snapshot();
        _cacheView = (_scale, _cx, _cy, Width, Height);
    }

    private void ViewChanged(bool immediate = false)
    {
        if (immediate)
        {
            _settle.Stop();
            RenderPlanCache();
        }
        else
        {
            _settle.Stop();
            _settle.Start();
        }
        Invalidate();
    }

    private void StepAnimation()
    {
        if (_anim is not { } a) { _animTimer.Stop(); return; }
        double t = Math.Min(1, (DateTime.Now - a.Start).TotalMilliseconds / 450);
        double ease = 1 - Math.Pow(1 - t, 3);
        // Interpolate zoom in log space so it feels even.
        _scale = (float)Math.Exp(Math.Log(a.S0) + (Math.Log(a.S1) - Math.Log(a.S0)) * ease);
        _cx = (float)(a.X0 + (a.X1 - a.X0) * ease);
        _cy = (float)(a.Y0 + (a.Y1 - a.Y0) * ease);
        if (t >= 1)
        {
            _anim = null;
            _animTimer.Stop();
            ViewChanged(immediate: true);
        }
        else
        {
            Invalidate();
        }
    }

    // ------------------------------------------------------------------- mouse

    private PlotItem? HitDevice(Point p)
    {
        float r = SymbolRadius + 3;
        PlotItem? best = null;
        float bestD = float.MaxValue;
        foreach (var item in _items)
        {
            var s = PlanToScreen(item.X, item.Y);
            float d = (s.X - p.X) * (s.X - p.X) + (s.Y - p.Y) * (s.Y - p.Y);
            if (d <= r * r && d < bestD) { best = item; bestD = d; }
        }
        return best;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!Focused) Focus(); // so the wheel reaches us
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _anim = null;
        _down = _last = e.Location;
        _moved = false;
        if (e.Button != MouseButtons.Left) return;

        if (_areaMode)
        {
            _drag = Drag.Area;
            _band = new Rectangle(e.Location, Size.Empty);
            return;
        }

        var hit = HitDevice(e.Location);
        if (hit != null)
        {
            _dragKey = hit.Key;
            _drag = AllowEdit ? Drag.Device : Drag.None;
        }
        else
        {
            _dragKey = null;
            _drag = Drag.Pan;
        }
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag == Drag.None)
        {
            Cursor = _areaMode ? Cursors.Cross : HitDevice(e.Location) != null ? Cursors.Hand : Cursors.Default;
            return;
        }
        if (Math.Abs(e.X - _down.X) + Math.Abs(e.Y - _down.Y) > 4) _moved = true;

        switch (_drag)
        {
            case Drag.Pan:
                _cx -= (e.X - _last.X) / _scale;
                _cy += (e.Y - _last.Y) / _scale;
                ViewChanged();
                break;
            case Drag.Device when _moved && _dragKey != null:
                var item = _items.FirstOrDefault(i => i.Key == _dragKey);
                if (item != null)
                {
                    var p = ScreenToPlan(e.Location);
                    item.X = p.X;
                    item.Y = p.Y;
                    Invalidate();
                }
                break;
            case Drag.Area:
                _band = Rectangle.FromLTRB(Math.Min(_down.X, e.X), Math.Min(_down.Y, e.Y), Math.Max(_down.X, e.X), Math.Max(_down.Y, e.Y));
                Invalidate();
                break;
        }
        _last = e.Location;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Capture = false;
        var drag = _drag;
        _drag = Drag.None;

        if (drag == Drag.Area)
        {
            _areaMode = false;
            Cursor = Cursors.Default;
            Invalidate();
            if (_band.Width > 10 && _band.Height > 10)
            {
                var a = ScreenToPlan(new Point(_band.Left, _band.Bottom));
                var b = ScreenToPlan(new Point(_band.Right, _band.Top));
                AreaDefined?.Invoke(new SKRect(a.X, a.Y, b.X, b.Y));
            }
            return;
        }

        if (!_moved)
        {
            if (_dragKey != null) DeviceClicked?.Invoke(_dragKey);
            else if (e.Button == MouseButtons.Left) BackgroundClicked?.Invoke();
        }
        else if (drag == Drag.Device && _dragKey != null)
        {
            var item = _items.FirstOrDefault(i => i.Key == _dragKey);
            if (item != null) DeviceMoved?.Invoke(item.Key, item.X, item.Y);
        }
        _dragKey = null;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _anim = null;
        var before = ScreenToPlan(e.Location);
        _scale *= (float)Math.Pow(1.2, e.Delta / 120.0);
        // Keep the point under the cursor where it is.
        var after = ScreenToPlan(e.Location);
        _cx += before.X - after.X;
        _cy += before.Y - after.Y;
        ViewChanged();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_fitPending) FitToContent();
        else ViewChanged();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && _fitPending) FitToContent();
    }

    // ------------------------------------------------------------- drag & drop

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effect = AllowEdit && _picture != null && e.Data?.GetDataPresent(DragFormat) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (e.Data?.GetData(DragFormat) is not string key) return;
        var p = ScreenToPlan(PointToClient(new Point(e.X, e.Y)));
        DeviceDropped?.Invoke(key, p.X, p.Y);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _settle.Dispose();
            _flashTimer.Dispose();
            _animTimer.Dispose();
            _planCache?.Dispose();
            _picture?.Dispose();
            _buffer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
