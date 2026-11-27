using System;
using System.Collections.Generic;
using System.Diagnostics;
using Docnet.Core;
using Docnet.Core.Models;
using SkiaSharp;

namespace AmxEventsFormPoc.FloorPlan;

// What a floor's background is drawn from: a DWG drawing (DwgPlan) or a PDF page
// (PdfPlan). Coordinates are the source's own units with Y up, relative to Origin,
// so placed devices are stored against the drawing and never against the screen.
public interface IPlanSource : IDisposable
{
    string Path { get; }
    SKRect Extents { get; }
    double OriginX { get; }
    double OriginY { get; }
    IReadOnlyList<PlanLayer> Layers { get; }   // empty for a PDF
    string Description { get; }                // load figures for the status line
    SKPicture Record(ISet<string> hiddenLayers, SKRect? crop, SKColor ink);
    List<SKRect> FindFloorAreas();
}

// One page of a PDF floor plan, rendered once by PDFium (through Docnet.Core) to an
// image and drawn in the page's own units (points, 1/72 inch).  Scanned PDFs work
// the same, being images already.  Being an image it softens if zoomed in a long
// way; 4,500 pixels on the long side keeps a plan clear at normal zooms.
public sealed class PdfPlan : IPlanSource
{
    // The rendered page is the big allocation (tens of MB); free it when the plan
    // leaves the cache.  A picture already recorded from it keeps its own reference.
    public void Dispose()
    {
        _image?.Dispose();
        _image = null;
    }

    private const int LongSide = 4500;
    private const int ShortSide = 4500;
    private static readonly object PdfiumLock = new(); // PDFium isn't thread-safe

    public string Path { get; }
    public int Page { get; }             // 0-based
    public int Rotation { get; }         // degrees clockwise: 0, 90, 180 or 270
    public int PageCount { get; private set; }
    public SKRect Extents { get; private set; }
    public double OriginX => 0;
    public double OriginY => 0;
    public IReadOnlyList<PlanLayer> Layers => Array.Empty<PlanLayer>();
    public string Description { get; private set; } = "";

    private SKImage? _image;

    private PdfPlan(string path, int page, int rotation)
    {
        Path = path;
        Page = page;
        Rotation = rotation;
    }

    // 0, 90, 180 or 270 for any whole number of degrees (-90 is 270).
    public static int NormaliseRotation(int degrees) => ((degrees / 90 % 4) + 4) % 4 * 90;

    public static int CountPages(string path)
    {
        lock (PdfiumLock)
        {
            using var doc = DocLib.Instance.GetDocReader(path, new PageDimensions(1.0));
            return doc.GetPageCount();
        }
    }

    // rotation turns the page clockwise (0, 90, 180 or 270): the plan's extents and
    // coordinates are then those of the turned page.
    public static PdfPlan Load(string path, int page, int rotation = 0)
    {
        rotation = NormaliseRotation(rotation);
        bool sideways = rotation == 90 || rotation == 270;
        var plan = new PdfPlan(path, page, rotation);
        var sw = Stopwatch.StartNew();
        lock (PdfiumLock)
        {
            // Page size in points (scaling 1 = one pixel per point).
            float w, h;
            using (var doc = DocLib.Instance.GetDocReader(path, new PageDimensions(1.0)))
            {
                plan.PageCount = doc.GetPageCount();
                if (page < 0 || page >= plan.PageCount)
                    throw new ArgumentOutOfRangeException(nameof(page), $"The PDF has {plan.PageCount} page(s).");
                using var pr = doc.GetPageReader(page);
                w = pr.GetPageWidth();
                h = pr.GetPageHeight();
            }
            plan.Extents = sideways ? new SKRect(0, 0, h, w) : new SKRect(0, 0, w, h);

            // Rendered to fit LongSide x ShortSide, aspect kept.
            using var big = DocLib.Instance.GetDocReader(path, new PageDimensions(ShortSide, LongSide));
            using var reader = big.GetPageReader(page);
            int pw = reader.GetPageWidth(), ph = reader.GetPageHeight();
            var bgra = reader.GetImage();

            // PDFium leaves unpainted areas transparent: put the page on white.
            var info = new SKImageInfo(pw, ph, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var raw = SKImage.FromPixelCopy(info, bgra);
            // Turned in the same pass: a quarter turn of the pixels loses nothing.
            int iw = sideways ? ph : pw, ih = sideways ? pw : ph;
            using var surface = SKSurface.Create(new SKImageInfo(iw, ih, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.White);
            switch (rotation)
            {
                case 90: surface.Canvas.Translate(iw, 0); break;
                case 180: surface.Canvas.Translate(iw, ih); break;
                case 270: surface.Canvas.Translate(0, ih); break;
            }
            surface.Canvas.RotateDegrees(rotation);
            surface.Canvas.DrawImage(raw, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
            plan._image = surface.Snapshot();
            plan.Description = $"PDF page {page + 1} of {plan.PageCount}, rendered {pw:N0} x {ph:N0} px in {sw.Elapsed.TotalSeconds:F1} s" +
                               (rotation != 0 ? $", turned {rotation}°" : "");
        }
        return plan;
    }

    public SKPicture Record(ISet<string> hiddenLayers, SKRect? crop, SKColor ink)
    {
        var bounds = crop ?? Extents;
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(bounds);
        if (crop.HasValue) canvas.ClipRect(crop.Value);
        if (_image != null)
        {
            // Plan Y is up and the view flips it, so the image goes in flipped and
            // comes out upright.
            canvas.Save();
            canvas.Translate(0, Extents.Height);
            canvas.Scale(1, -1);
            canvas.DrawImage(_image, new SKRect(0, 0, Extents.Width, Extents.Height),
                             new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            canvas.Restore();
        }
        return recorder.EndRecording();
    }

    // Plans on one PDF page aren't split automatically; "New floor from an area"
    // cuts one out by hand.
    public List<SKRect> FindFloorAreas() => new() { Extents };

    // A small preview of each page, for naming the floors before any are created. Each page opens the document inside the PDFium lock rather than holding
    // the lock across the whole enumeration; stop early with the token (the dialog
    // closing).
    public static IEnumerable<(int Page, System.Drawing.Bitmap Thumbnail)> ReadPagePreviews(
        string path, int width, int height, System.Threading.CancellationToken token)
    {
        int count = CountPages(path);
        for (int page = 0; page < count && !token.IsCancellationRequested; page++)
        {
            System.Drawing.Bitmap thumb;
            lock (PdfiumLock)
            {
                using var doc = DocLib.Instance.GetDocReader(path, new PageDimensions(Math.Min(width, height), Math.Max(width, height)));
                using var reader = doc.GetPageReader(page);
                int w = reader.GetPageWidth(), h = reader.GetPageHeight();
                var bgra = reader.GetImage();
                thumb = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var data = thumb.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                System.Runtime.InteropServices.Marshal.Copy(bgra, 0, data.Scan0, Math.Min(bgra.Length, data.Stride * h));
                thumb.UnlockBits(data);
            }
            yield return (page, thumb);
        }
    }
}
