using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;
using SkiaSharp;

namespace AmxEventsFormPoc.FloorPlan;

// A DWG floor plan, flattened for drawing. ACadSharp reads the file; every entity
// (including the contents of nested blocks) is turned into straight-line polylines
// grouped by layer, so the plan can be redrawn quickly with any set of layers hidden
// and cropped to one floor of a multi-floor drawing.
//
// Coordinates are drawing units with Y up, stored relative to Origin: site drawings
// are often placed at large real-world coordinates, and floats lose precision out
// there, so everything is shifted near zero first.
public sealed class DwgPlan : IPlanSource
{
    public string Path { get; }
    public SKRect Extents { get; private set; }      // where most of the drawing is (stray far-off objects ignored)
    public IReadOnlyList<PlanLayer> Layers => _layers;
    public TimeSpan ReadTime { get; private set; }
    public TimeSpan FlattenTime { get; private set; }
    public int EntityCount { get; private set; }
    public long SegmentCount => _layers.Sum(l => (long)l.SegmentCount);
    public string Description =>
        $"read {ReadTime.TotalSeconds:F1} s, prepared {FlattenTime.TotalSeconds:F1} s, {SegmentCount / 1000:N0}k line segments";

    // Plan coordinates are stored relative to this point; add it back to get true
    // drawing coordinates (what gets saved, so positions survive a reload).
    public double OriginX => _ox;
    public double OriginY => _oy;

    private readonly List<PlanLayer> _layers = new();
    private readonly Dictionary<string, PlanLayer> _byName = new(StringComparer.OrdinalIgnoreCase);
    private double _ox, _oy;
    private bool _originSet;

    private DwgPlan(string path) => Path = path;

    public static DwgPlan Load(string path)
    {
        var plan = new DwgPlan(path);
        var sw = Stopwatch.StartNew();
        var doc = DwgReader.Read(path);
        plan.ReadTime = sw.Elapsed;

        sw.Restart();
        foreach (var layer in doc.Layers)
        {
            var pl = plan.Layer(layer.Name);
            pl.Color = ToSk(layer.Color);
            // Respect the drawing's own state: layers switched off or frozen in
            // AutoCAD start hidden here too.
            pl.InitiallyHidden = !layer.IsOn || layer.Flags.HasFlag(ACadSharp.Tables.LayerFlags.Frozen);
        }

        var entities = doc.Entities.ToList();
        plan.EntityCount = entities.Count;
        plan.Walk(entities, Affine.Identity, null, 0);
        plan._layers.RemoveAll(l => l.SegmentCount == 0 && l.Texts.Count == 0);
        plan.Extents = plan.ComputeExtents();
        plan.FlattenTime = sw.Elapsed;
        return plan;
    }

    // Records the visible layers as a Skia picture in drawing coordinates (Y up).
    // Pass a crop to keep only geometry touching that area — one floor of a
    // multi-floor drawing — which also keeps the picture small.
    public SKPicture Record(ISet<string> hiddenLayers, SKRect? crop, SKColor ink)
    {
        var bounds = crop ?? Extents;
        var cull = SKRect.Inflate(bounds, bounds.Width * 0.02f, bounds.Height * 0.02f);
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(cull);
        if (crop.HasValue) canvas.ClipRect(crop.Value);

        // Hairline stroke (width 0) stays one pixel wide at any zoom, which is what a
        // plan wants: walls never turn into thick bars when zoomed in.
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 0, IsAntialias = true, Color = ink };
        foreach (var layer in _layers)
        {
            if (hiddenLayers.Contains(layer.Name) || layer.SegmentCount == 0) continue;
            using var path = layer.BuildPath(crop.HasValue ? cull : null);
            canvas.DrawPath(path, stroke);
        }

        // Text is drawn with its baseline flipped back upright (the view flips Y).
        using var font = new SKFont { Typeface = SKTypeface.FromFamilyName("Arial") };
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = ink };
        foreach (var layer in _layers)
        {
            if (hiddenLayers.Contains(layer.Name)) continue;
            foreach (var t in layer.Texts)
            {
                if (crop.HasValue && !cull.Contains(t.X, t.Y)) continue;
                if (t.Height <= 0 || string.IsNullOrWhiteSpace(t.Text)) continue;
                canvas.Save();
                canvas.Translate(t.X, t.Y);
                canvas.RotateRadians(t.Rotation);
                canvas.Scale(1, -1);
                font.Size = t.Height;
                canvas.DrawText(t.Text, 0, 0, SKTextAlign.Left, font, fill);
                canvas.Restore();
            }
        }

        return recorder.EndRecording();
    }

    // ---------------------------------------------------------------- flattening

    private PlanLayer Layer(string name)
    {
        if (!_byName.TryGetValue(name, out var l))
        {
            l = new PlanLayer(name);
            _byName[name] = l;
            _layers.Add(l);
        }
        return l;
    }

    private void Walk(IEnumerable<Entity> entities, Affine m, string? blockLayer, int depth)
    {
        if (depth > 16) return; // self-referencing or absurdly deep blocks
        foreach (var e in entities)
        {
            if (e.IsInvisible) continue;
            // Entities on layer 0 inside a block take the layer of the block insert,
            // as in AutoCAD.
            string layerName = e.Layer?.Name ?? "0";
            if (blockLayer != null && layerName == "0") layerName = blockLayer;
            try
            {
                Add(e, m, layerName, depth);
            }
            catch
            {
                // One malformed entity must not cost the whole plan.
            }
        }
    }

    private void Add(Entity e, Affine m, string layerName, int depth)
    {
        var layer = Layer(layerName);
        switch (e)
        {
            case Insert ins when ins.Block != null:
                {
                    var bp = ins.Block.BlockEntity?.BasePoint ?? XYZ.Zero;
                    var local = Affine.Translate(-bp.X, -bp.Y)
                        .Then(Affine.Scale(ins.XScale, ins.YScale))
                        .Then(Affine.Rotate(ins.Rotation))
                        .Then(Affine.Translate(ins.InsertPoint.X, ins.InsertPoint.Y));
                    Walk(ins.Block.Entities, local.Then(m), layerName, depth + 1);
                    break;
                }
            case Line l:
                Poly(layer, m, new[] { (l.StartPoint.X, l.StartPoint.Y), (l.EndPoint.X, l.EndPoint.Y) }, false);
                break;
            case Arc a:
                Poly(layer, m, ArcPoints(a.Center.X, a.Center.Y, a.Radius, a.StartAngle, a.EndAngle), false);
                break;
            case Circle c:
                Poly(layer, m, ArcPoints(c.Center.X, c.Center.Y, c.Radius, 0, Math.PI * 2), true);
                break;
            case LwPolyline lw:
                Poly(layer, m, BulgePoints(lw.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList(), lw.IsClosed), lw.IsClosed);
                break;
            case IPolyline pl:
                Poly(layer, m, BulgePoints(pl.Vertices.Select(v => (v.Location[0], v.Location[1], v.Bulge)).ToList(), pl.IsClosed), pl.IsClosed);
                break;
            case Ellipse el:
                Poly(layer, m, el.PolygonalVertexes(48).Select(p => (p.X, p.Y)).ToList(), el.IsFullEllipse);
                break;
            case Spline sp:
                if (sp.TryPolygonalVertexes(64, out var pts))
                    Poly(layer, m, pts.Select(p => (p.X, p.Y)).ToList(), false);
                break;
            case Solid so:
                Poly(layer, m, new[] { (so.FirstCorner.X, so.FirstCorner.Y), (so.SecondCorner.X, so.SecondCorner.Y),
                                        (so.FourthCorner.X, so.FourthCorner.Y), (so.ThirdCorner.X, so.ThirdCorner.Y) }, true);
                break;
            case Hatch h:
                // Outlines only: solid fills and patterns are left out of the
                // proof of concept (they are mostly shading, and costly to draw).
                foreach (var path in h.Paths)
                    Poly(layer, m, path.GetPoints(24).Select(p => (p.X, p.Y)).ToList(), true);
                break;
            case TextEntity t:
                Text(layer, m, t.InsertPoint.X, t.InsertPoint.Y, t.Height, t.Rotation, t.Value);
                break;
            case MText mt:
                Text(layer, m, mt.InsertPoint.X, mt.InsertPoint.Y - mt.Height, mt.Height, mt.Rotation, PlainMText(mt.Value));
                break;
        }
    }

    private void Poly(PlanLayer layer, Affine m, IReadOnlyList<(double X, double Y)> points, bool closed)
    {
        if (points.Count < 2) return;
        int count = points.Count + (closed ? 1 : 0);
        var pts = new float[count * 2];
        int i = 0;
        for (int k = 0; k < count; k++)
        {
            var p = points[k % points.Count];
            var (x, y) = m.Apply(p.X, p.Y);
            if (!_originSet) { _ox = x; _oy = y; _originSet = true; }
            pts[i++] = (float)(x - _ox);
            pts[i++] = (float)(y - _oy);
        }
        layer.AddPolyline(pts);
    }

    private void Text(PlanLayer layer, Affine m, double x, double y, double height, double rotation, string text)
    {
        var (tx, ty) = m.Apply(x, y);
        if (!_originSet) { _ox = tx; _oy = ty; _originSet = true; }
        var scale = m.ScaleFactor;
        layer.Texts.Add(new PlanText((float)(tx - _ox), (float)(ty - _oy), (float)(height * scale),
                                     (float)(rotation + m.RotationAngle), text));
    }

    // MText carries formatting codes ({\fArial|b0;Text}, \P for new line...). Keep
    // the first line of plain text, which is the room label in a plan.
    private static string PlainMText(string value)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(value ?? "", @"\\[A-Za-z][^;\\{}]*;|[{}]", "");
        s = s.Replace("\\P", "\n").Replace("\\~", " ");
        return s.Split('\n')[0].Trim();
    }

    private static List<(double, double)> ArcPoints(double cx, double cy, double r, double start, double end)
    {
        if (end <= start) end += Math.PI * 2;
        var sweep = end - start;
        int n = Math.Clamp((int)Math.Ceiling(sweep / (Math.PI / 36)), 4, 144); // ~5 degree steps
        var list = new List<(double, double)>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            var a = start + sweep * i / n;
            list.Add((cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }
        return list;
    }

    // Polyline vertices with bulges (arc segments): bulge = tan(sweep / 4).
    private static List<(double, double)> BulgePoints(IReadOnlyList<(double X, double Y, double Bulge)> v, bool closed)
    {
        var list = new List<(double, double)>();
        int count = v.Count;
        for (int i = 0; i < count; i++)
        {
            var a = v[i];
            list.Add((a.X, a.Y));
            bool last = i == count - 1;
            if (last && !closed) break;
            var b = v[last ? 0 : i + 1];
            if (Math.Abs(a.Bulge) < 1e-9) continue;

            double dx = b.X - a.X, dy = b.Y - a.Y, chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 1e-12) continue;
            double bulge = a.Bulge;
            double sweep = 4 * Math.Atan(bulge);                       // signed: + is anticlockwise
            double r = chord * (1 + bulge * bulge) / (4 * Math.Abs(bulge));
            // Centre: from the chord midpoint along its left normal by
            // chord (1 - b^2) / 4b. The sign of b and of (1 - b^2) put it on the
            // correct side for either direction and for arcs over a half circle.
            double off = chord * (1 - bulge * bulge) / (4 * bulge);
            double mx = (a.X + b.X) / 2, my = (a.Y + b.Y) / 2;
            double cx = mx - dy / chord * off, cy = my + dx / chord * off;
            double sa = Math.Atan2(a.Y - cy, a.X - cx);
            int n = Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 36)), 2, 144);
            for (int k = 1; k < n; k++)
            {
                double ang = sa + sweep * k / n;
                list.Add((cx + r * Math.Cos(ang), cy + r * Math.Sin(ang)));
            }
        }
        return list;
    }

    // Finds separate plans laid out on one sheet (a multi-floor drawing often has every
    // floor side by side, or stacked). The drawing is split wherever there is a clear
    // empty band across it, first left to right, then each part top to bottom.  Small
    // islands (title blocks, stray symbols) are dropped.  Returns one area per plan in
    // reading order (left to right, top to bottom); a single plan gives one area.
    public List<SKRect> FindFloorAreas()
    {
        var xs = new List<float>();
        var ys = new List<float>();
        foreach (var l in _layers.Where(l => !l.InitiallyHidden)) l.SampleVertices(xs, ys, 400_000);
        var pts = xs.Select((x, i) => new SKPoint(x, ys[i])).ToList();
        if (pts.Count < 100) return new List<SKRect> { Extents };

        var areas = new List<List<SKPoint>>();
        foreach (var column in Split(pts, p => p.X))
            areas.AddRange(Split(column, p => p.Y));

        // Clutter, not a floor: under 2.5% of the drawing, or well under the size of the
        // biggest plan (floors on one sheet are similar sizes; a stray cluster of door
        // swings or symbols isn't).  A genuinely small plan can still be added by hand
        // with "New floor from an area".
        int biggest = areas.Max(a => a.Count);
        int minPoints = Math.Max(Math.Max(50, pts.Count / 40), biggest * 15 / 100);
        var result = areas
            .Where(a => a.Count >= minPoints)
            .Select(a =>
            {
                // Percentile bounds per area, so a stray line doesn't stretch the crop.
                var ax = a.Select(p => p.X).OrderBy(v => v).ToList();
                var ay = a.Select(p => p.Y).OrderBy(v => v).ToList();
                float Q(List<float> v, double q) => v[(int)Math.Clamp(q * (v.Count - 1), 0, v.Count - 1)];
                var r = new SKRect(Q(ax, 0.002), Q(ay, 0.002), Q(ax, 0.998), Q(ay, 0.998));
                return SKRect.Inflate(r, Math.Max(r.Width, r.Height) * 0.04f, Math.Max(r.Width, r.Height) * 0.04f);
            })
            .OrderBy(r => r.Left).ThenByDescending(r => r.Bottom)
            .ToList();
        return result.Count > 0 ? result : new List<SKRect> { Extents };
    }

    // Splits points into groups separated by an empty band at least 2% of the span
    // wide along one axis.
    private static List<List<SKPoint>> Split(List<SKPoint> pts, Func<SKPoint, float> axis)
    {
        const int bins = 1000;
        float min = pts.Min(axis), max = pts.Max(axis);
        if (max - min <= 0) return new List<List<SKPoint>> { pts };
        var counts = new int[bins];
        foreach (var p in pts) counts[Math.Min(bins - 1, (int)((axis(p) - min) / (max - min) * bins))]++;

        // A bin counts as empty if it holds almost nothing (a stray line crossing a gap
        // shouldn't join two floors).
        int noise = Math.Max(1, pts.Count / 20000);
        var cuts = new List<float>();
        int run = 0;
        for (int i = 0; i < bins; i++)
        {
            if (counts[i] <= noise) { run++; continue; }
            if (run >= bins / 50 && i - run > 0) cuts.Add(min + (i - run / 2f) / bins * (max - min));
            run = 0;
        }
        if (cuts.Count == 0) return new List<List<SKPoint>> { pts };
        return pts.GroupBy(p => cuts.Count(c => axis(p) > c)).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
    }

    private SKRect ComputeExtents()
    {
        // Percentile extents: drawings often carry a stray object miles from the
        // building, which would make "fit to window" useless.
        var xs = new List<float>();
        var ys = new List<float>();
        foreach (var l in _layers) l.SampleVertices(xs, ys, 200_000);
        if (xs.Count == 0) return new SKRect(0, 0, 1000, 1000);
        xs.Sort(); ys.Sort();
        float P(List<float> v, double q) => v[(int)Math.Clamp(q * (v.Count - 1), 0, v.Count - 1)];
        var r = new SKRect(P(xs, 0.005), P(ys, 0.005), P(xs, 0.995), P(ys, 0.995));
        if (r.Width <= 0 || r.Height <= 0) r = new SKRect(xs[0], ys[0], xs[^1] + 1, ys[^1] + 1);
        return SKRect.Inflate(r, r.Width * 0.03f, r.Height * 0.03f);
    }

    private static SKColor ToSk(ACadSharp.Color c)
    {
        try
        {
            var rgb = c.GetRgb();
            return new SKColor(rgb[0], rgb[1], rgb[2]);
        }
        catch
        {
            return SKColors.Gray;
        }
    }

    // A 2D affine transform in doubles: x' = A x + C y + E, y' = B x + D y + F.
    private readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        public static Affine Identity => new(1, 0, 0, 1, 0, 0);
        public static Affine Translate(double x, double y) => new(1, 0, 0, 1, x, y);
        public static Affine Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);
        public static Affine Rotate(double r) => new(Math.Cos(r), Math.Sin(r), -Math.Sin(r), Math.Cos(r), 0, 0);

        // This transform first, then `next`.
        public Affine Then(Affine n) => new(
            n.A * A + n.C * B, n.B * A + n.D * B,
            n.A * C + n.C * D, n.B * C + n.D * D,
            n.A * E + n.C * F + n.E, n.B * E + n.D * F + n.F);

        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
        public double ScaleFactor => Math.Sqrt(Math.Abs(A * D - B * C));
        public double RotationAngle => Math.Atan2(B, A);
    }
}

public sealed record PlanText(float X, float Y, float Height, float Rotation, string Text);

public sealed class PlanLayer
{
    public string Name { get; }
    public SKColor Color { get; set; } = SKColors.Gray;
    public bool InitiallyHidden { get; set; }
    public List<PlanText> Texts { get; } = new();
    public int SegmentCount { get; private set; }

    // All polylines of the layer in one flat float array (x, y, x, y...), with the
    // start index of each polyline, so a big plan doesn't become a million objects.
    private readonly List<float> _coords = new();
    private readonly List<int> _starts = new();

    public PlanLayer(string name) => Name = name;

    public void AddPolyline(float[] pts)
    {
        _starts.Add(_coords.Count);
        _coords.AddRange(pts);
        SegmentCount += pts.Length / 2 - 1;
    }

    public SKPath BuildPath(SKRect? cull)
    {
        using var path = new SKPathBuilder();
        for (int s = 0; s < _starts.Count; s++)
        {
            int from = _starts[s];
            int to = s + 1 < _starts.Count ? _starts[s + 1] : _coords.Count;
            if (cull.HasValue && !Touches(from, to, cull.Value)) continue;
            path.MoveTo(_coords[from], _coords[from + 1]);
            for (int i = from + 2; i < to; i += 2) path.LineTo(_coords[i], _coords[i + 1]);
        }
        return path.Detach();
    }

    private bool Touches(int from, int to, SKRect r)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = from; i < to; i += 2)
        {
            minX = Math.Min(minX, _coords[i]); maxX = Math.Max(maxX, _coords[i]);
            minY = Math.Min(minY, _coords[i + 1]); maxY = Math.Max(maxY, _coords[i + 1]);
        }
        return maxX >= r.Left && minX <= r.Right && maxY >= r.Top && minY <= r.Bottom;
    }

    public void SampleVertices(List<float> xs, List<float> ys, int max)
    {
        int step = Math.Max(1, (_coords.Count / 2) / Math.Max(1, max / 8)) * 2;
        for (int i = 0; i + 1 < _coords.Count; i += step)
        {
            xs.Add(_coords[i]);
            ys.Add(_coords[i + 1]);
        }
    }
}
