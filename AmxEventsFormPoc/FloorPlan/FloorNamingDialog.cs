using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AmxEventsFormPoc.FloorPlan;

// A PDF chosen in Open floor plan: its page count; if the whole file is a duplicate
// of another one picked, why; and any pages already set up as floors (page -> why),
// so a file opened again in a later stage offers only the pages not yet added.
public sealed record PdfChoice(string Path, int Pages, string? Duplicate,
                               IReadOnlyDictionary<int, string>? PagesDone = null);

// Shown when PDFs are opened.  A site may send one PDF per floor, or one PDF with a
// page per floor, and a PDF (unlike a DWG) doesn't say which floor it is.  So before
// anything is created the user names each page that is a floor and unticks the ones
// that aren't (cover sheets, legends, schedules).  No name is assumed from the
// drawing or the file name: the person setting up the site types every one.  Nothing
// is created until OK, every included page has a name, and no name clashes with
// another page or an existing floor.  Duplicate files, and pages already set up,
// are listed unticked with the reason and can't be included.  Cancel creates nothing.
public sealed class FloorNamingDialog : Form
{
    private const int ThumbW = 200, ThumbH = 142;

    private readonly HashSet<string> _existingNames;
    private readonly List<Row> _rows = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Label _problem = new() { Dock = DockStyle.Fill, ForeColor = Color.Firebrick, TextAlign = ContentAlignment.MiddleLeft };
    private readonly List<PdfChoice> _files;

    // The floors to create, in the order shown, once OK has passed the checks.
    public List<(string Path, int Page, string Name)> Floors { get; } = new();

    private sealed class Row
    {
        public string Path = "";
        public int Page;
        public bool Duplicate;
        public CheckBox Include = null!;
        public PictureBox Thumb = null!;
        public TextBox Name = null!;
        public Label Hint = null!;
        public Panel Panel = null!;
    }

    public FloorNamingDialog(IReadOnlyList<PdfChoice> files, IEnumerable<string> existingFloorNames)
    {
        _files = files.ToList();
        _existingNames = new HashSet<string>(existingFloorNames.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);

        Text = files.Count == 1 ? "Name the floors in " + Path.GetFileName(files[0].Path) : $"Name the floors in {files.Count} PDFs";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(840, 740);
        MinimumSize = new Size(660, 440);
        TopMost = true;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 9f);

        var intro = new Label
        {
            Dock = DockStyle.Top,
            Height = 64,
            Padding = new Padding(10, 10, 10, 0),
            Text = "A PDF doesn’t say which floor it is.  Give each page that is a floor plan its floor name, " +
                   "and untick pages that aren’t (cover sheets, legends, schedules).",
        };

        var list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(10, 0, 10, 0),
        };
        foreach (var f in _files)
        {
            if (f.Duplicate != null)
            {
                // One row for the whole file: it isn't added.
                var dup = BuildRow(f.Path, 0, f.Pages, f.Duplicate, wholeFile: true);
                _rows.Add(dup);
                list.Controls.Add(dup.Panel);
                continue;
            }
            for (int page = 0; page < f.Pages; page++)
            {
                string? done = null;
                f.PagesDone?.TryGetValue(page, out done);
                var row = BuildRow(f.Path, page, f.Pages, done, wholeFile: false);
                _rows.Add(row);
                list.Controls.Add(row.Panel);
            }
        }

        var buttons = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 48, ColumnCount = 3, Padding = new Padding(10, 6, 10, 6) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        var ok = new Button { Text = "Add floors", Dock = DockStyle.Fill };
        var cancel = new Button { Text = "Cancel", Dock = DockStyle.Fill, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => TryAccept();
        buttons.Controls.Add(_problem, 0, 0);
        buttons.Controls.Add(ok, 1, 0);
        buttons.Controls.Add(cancel, 2, 0);
        CancelButton = cancel;

        Controls.Add(list);
        Controls.Add(buttons);
        Controls.Add(intro);

        Shown += (_, _) => LoadPreviewsAsync();
        FormClosed += (_, _) => _cts.Cancel();
    }

    private Row BuildRow(string path, int page, int pages, string? duplicate, bool wholeFile)
    {
        var row = new Row { Path = path, Page = page, Duplicate = duplicate != null };
        row.Panel = new Panel { Width = 780, Height = ThumbH + 12, Margin = new Padding(0, 0, 0, 6), BorderStyle = BorderStyle.FixedSingle };
        row.Include = new CheckBox { Checked = duplicate == null, Enabled = duplicate == null, Location = new Point(8, (ThumbH + 12) / 2 - 10), AutoSize = true };
        row.Thumb = new PictureBox
        {
            Location = new Point(32, 5), Size = new Size(ThumbW, ThumbH), SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
        };
        int x = ThumbW + 48;
        var fileLabel = new Label
        {
            Text = Path.GetFileName(path) + (pages > 1 && !wholeFile ? $"  ·  page {page + 1} of {pages}" : ""),
            Location = new Point(x, 14), Width = 500, AutoEllipsis = true, Font = new Font(Font, FontStyle.Bold),
        };
        var nameLabel = new Label { Text = "Floor name", Location = new Point(x, 50), AutoSize = true };
        row.Name = new TextBox { Location = new Point(x, 70), Width = 300, PlaceholderText = "e.g. Level 1", Enabled = duplicate == null };
        row.Hint = new Label { Location = new Point(x, 98), Width = 500, ForeColor = Color.DimGray };

        if (duplicate != null)
        {
            row.Panel.BackColor = Color.Gainsboro;
            nameLabel.Visible = false;
            row.Name.Visible = false;
            row.Hint.Location = new Point(x, 50);
            row.Hint.Height = 40;
            row.Hint.ForeColor = Color.DimGray;
            row.Hint.Text = duplicate + "  Not added again.";
        }

        row.Name.TextChanged += (_, _) => row.Name.BackColor = SystemColors.Window;
        row.Include.CheckedChanged += (_, _) =>
        {
            row.Name.Enabled = row.Include.Checked;
            row.Panel.BackColor = row.Include.Checked ? SystemColors.Control : Color.Gainsboro;
            row.Name.BackColor = SystemColors.Window;
        };
        // Clicking the preview toggles the page too.
        row.Thumb.Click += (_, _) => { if (!row.Duplicate) row.Include.Checked = !row.Include.Checked; };

        row.Panel.Controls.AddRange(new Control[] { row.Include, row.Thumb, fileLabel, nameLabel, row.Name, row.Hint });
        return row;
    }

    // Previews fill in one page at a time in the background, so a long drawing set
    // opens straight away.
    private async void LoadPreviewsAsync()
    {
        var token = _cts.Token;
        try
        {
            await Task.Run(() =>
            {
                foreach (var f in _files)
                {
                    // One file failing to render (moved since it was picked, damaged)
                    // costs only its previews: its rows stay, blank, and can still be named.
                    try
                    {
                        foreach (var (page, thumb) in PdfPlan.ReadPagePreviews(f.Path, ThumbW * 2, ThumbH * 2, token))
                        {
                            if (token.IsCancellationRequested || IsDisposed) { thumb.Dispose(); return; }
                            var path = f.Path;
                            BeginInvoke(() => ShowPreview(path, page, thumb));
                            if (f.Duplicate != null) break; // one preview is enough for a skipped file
                        }
                    }
                    catch (Exception) when (!token.IsCancellationRequested && !IsDisposed) { }
                }
            }, token);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { } // closed while a preview was on its way
        catch (Exception) { }                 // never let a preview failure reach the UI thread
    }

    private void ShowPreview(string path, int page, Bitmap thumb)
    {
        var row = _rows.FirstOrDefault(r => r.Path == path && r.Page == page);
        if (row == null || IsDisposed) { thumb.Dispose(); return; }
        row.Thumb.Image = thumb;
    }

    private void TryAccept()
    {
        foreach (var r in _rows) r.Name.BackColor = SystemColors.Window;
        var included = _rows.Where(r => !r.Duplicate && r.Include.Checked).ToList();
        Row? firstBad = null;
        string? problem = null;

        void Flag(Row r, string message)
        {
            r.Name.BackColor = Color.MistyRose;
            if (firstBad == null) { firstBad = r; problem = message; }
        }

        if (included.Count == 0) problem = "Nothing to add: tick at least one page, or Cancel.";
        foreach (var r in included.Where(r => r.Name.Text.Trim().Length == 0))
            Flag(r, $"{Path.GetFileName(r.Path)} page {r.Page + 1} needs a floor name, or untick it.");
        foreach (var g in included.Where(r => r.Name.Text.Trim().Length > 0)
                                  .GroupBy(r => r.Name.Text.Trim(), StringComparer.OrdinalIgnoreCase)
                                  .Where(g => g.Count() > 1))
            foreach (var r in g) Flag(r, $"\"{g.Key}\" is given to more than one page.");
        foreach (var r in included.Where(r => _existingNames.Contains(r.Name.Text.Trim())))
            Flag(r, $"There is already a floor called \"{r.Name.Text.Trim()}\".");

        if (problem != null)
        {
            _problem.Text = problem;
            if (firstBad != null)
            {
                (firstBad.Panel.Parent as ScrollableControl)?.ScrollControlIntoView(firstBad.Panel);
                firstBad.Name.Focus();
                firstBad.Name.SelectAll();
            }
            return;
        }

        Floors.Clear();
        Floors.AddRange(included.Select(r => (r.Path, r.Page, r.Name.Text.Trim())));
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
            foreach (var r in _rows) r.Thumb.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
