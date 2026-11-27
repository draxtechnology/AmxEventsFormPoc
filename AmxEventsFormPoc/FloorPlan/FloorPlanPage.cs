using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AmxEventsFormPoc.Models;
using SkiaSharp;

namespace AmxEventsFormPoc.FloorPlan;

// The floor plan page: device list (from the panel's SKF or NCF file) on the left, the
// plan in the middle, and a slide-out on the right with the selected device's
// details and recent events. Lives inside MainForm like the History page, and can
// be undocked into its own window (UndockRequested) for a second screen.
public sealed class FloorPlanPage : UserControl
{
    public event Action? UndockRequested;
    public event Action? AlarmShown; // the page zoomed to an alarm; the host may want to bring it to the front

    private static readonly SKColor Ink = new(70, 70, 70);

    private readonly FloorPlanRepository _repo;
    private PanelConfig? _config;
    private Dictionary<string, PanelDevice> _devices = new();
    private Dictionary<(int, int, int), PanelDevice> _byAmxPoint = new();
    private Dictionary<string, PlacedDevice> _placed = new();
    private Dictionary<string, AmxSymbol> _symbolMap = new();
    private readonly Dictionary<string, DeviceState> _states = new();
    private readonly Dictionary<string, List<DraxEventRow>> _recent = new();
    private List<PlanFloor> _floors = new();
    private PlanFloor? _floor;
    private IPlanSource? _plan;
    // Keyed on file and page: a PDF's pages are separate plans.
    private readonly Dictionary<string, IPlanSource> _planCache = new(StringComparer.OrdinalIgnoreCase);
    // Most recently used first.  A PDF page held in memory is tens of MB, so only the
    // last few are kept (the one on screen is never dropped).
    private readonly List<string> _planCacheOrder = new();
    private const int PlanCacheSize = 4;
    private int _nodeOffset;
    private List<PanelDevice> _listRows = new();
    private string? _slideKey;

    // --- controls ---
    private readonly ToolStrip _tools = new() { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top, Padding = new Padding(4, 2, 4, 2) };
    private readonly ToolStripComboBox _floorBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 600 };
    private readonly ToolStripButton _editButton = new("Edit") { CheckOnClick = true, Checked = true, ToolTipText = "Drag devices from the list onto the plan, and move them" };
    private readonly ToolStripButton _gridButton = new("Grid on") { CheckOnClick = true, Checked = true, Enabled = false, ToolTipText = "Show or hide the drawing’s grid lines" };
    private readonly ToolStripButton _followButton = new("Follow alarms") { CheckOnClick = true, Checked = true, ToolTipText = "Zoom to a device and open its details when it goes into alarm" };
    private readonly ToolStripButton _undockButton = new("Undock") { ToolTipText = "Open the plan in its own window, for a second screen" };
    private readonly ToolStripLabel _status = new() { ForeColor = Color.DimGray };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 5 };
    private readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "Search devices (location, zone, N1 L2 D3)" };
    private readonly CheckBox _unplacedOnly = new() { Dock = DockStyle.Top, Text = "Not yet on a plan", Height = 24, Padding = new Padding(4, 0, 0, 0) };
    private readonly Label _listInfo = new() { Dock = DockStyle.Bottom, Height = 46, ForeColor = Color.DimGray, Padding = new Padding(4) };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, VirtualMode = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable, HideSelection = false,
    };
    private readonly PlanCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly Panel _slide = new() { Dock = DockStyle.Right, Width = 0, BackColor = Color.FromArgb(248, 248, 246), Padding = new Padding(12) };
    private readonly System.Windows.Forms.Timer _slideTimer = new() { Interval = 15 };
    private int _slideTarget;

    private readonly Label _sTitle = new() { Dock = DockStyle.Top, Height = 48, Font = new Font("Segoe UI", 12f, FontStyle.Bold) };
    private readonly Label _sState = new() { Dock = DockStyle.Top, Height = 30, Font = new Font("Segoe UI", 10f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
    private readonly Label _sDetails = new() { Dock = DockStyle.Top, Height = 132, Font = new Font("Consolas", 9f) };
    private readonly Label _sSymbolLabel = new() { Dock = DockStyle.Top, Height = 22, Text = "Symbol" };
    private readonly ComboBox _sSymbol = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _sSymbolNote = new() { Dock = DockStyle.Top, Height = 38, ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8f) };
    private readonly Label _sEventsLabel = new() { Dock = DockStyle.Top, Height = 26, Text = "Recent events", TextAlign = ContentAlignment.BottomLeft };
    private readonly ListBox _sEvents = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly FlowLayoutPanel _sButtons = new() { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };
    private readonly Button _sClose = new() { Text = "Close", Width = 80 };
    private readonly Button _sRemove = new() { Text = "Remove from plan", Width = 130 };

    public FloorPlanPage(FloorPlanRepository repo)
    {
        _repo = repo;
        Dock = DockStyle.Fill;
        BuildToolbar();
        BuildList();
        BuildSlideOut();

        _split.Panel2.Controls.Add(_canvas);
        _split.Panel2.Controls.Add(_slide);
        Controls.Add(_split);
        Controls.Add(_tools);

        _canvas.DeviceDropped += OnDeviceDropped;
        _canvas.DeviceMoved += OnDeviceMoved;
        _canvas.DeviceClicked += key => ShowSlideOut(key);
        _canvas.BackgroundClicked += () => HideSlideOut();
        _canvas.AreaDefined += OnAreaDefined;

        // The page starts hidden at a token size, so the list width is set the first
        // time it is shown at a real size (setting it earlier gets clamped).
        _split.Panel1MinSize = 240;
        bool splitSet = false;
        void SetSplit()
        {
            if (splitSet || !Visible || _split.Width < 900) return;
            _split.SplitterDistance = 380;
            _list.Columns[1].Width = 170;
            splitSet = true;
        }
        VisibleChanged += (_, _) => SetSplit();
        _split.SizeChanged += (_, _) => SetSplit();
    }

    // Loads the saved floors, symbol map and the last panel configuration. Call once
    // the page is on a form.
    public async Task InitialiseAsync()
    {
        _nodeOffset = int.TryParse(_repo.GetSetting("NodeOffset"), out var o) ? o : 0;
        _symbolMap = _repo.GetSymbolMap();
        _placed = _repo.GetPlacedDevices().ToDictionary(p => p.Key);
        RefreshFloorBox();

        // "SkfPath" is the first cut's name for the setting.
        var configPath = _repo.GetSetting("ConfigPath") ?? _repo.GetSetting("SkfPath");
        if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath)) await LoadConfigAsync(configPath);
        else UpdateListInfo();

        long.TryParse(_repo.GetSetting("FloorId"), out var floorId);
        var floor = _floors.FirstOrDefault(f => f.Id == floorId) ?? _floors.FirstOrDefault();
        if (floor != null) await ShowFloorAsync(floor);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool IsUndocked
    {
        set => _undockButton.Text = value ? "Dock" : "Undock";
    }

    // ------------------------------------------------------------- live events

    // The events still active when the app starts (Current Events, reloaded from the
    // database): their devices show in alarm/fault straight away, without zooming.
    public void LoadActiveEvents(IEnumerable<DraxEventRow> rows)
    {
        foreach (var row in rows.Where(r => r.On).OrderBy(r => r.ReceivedLocal))
            Apply(row);
        RefreshCanvasItems();
    }

    private string? Apply(DraxEventRow row)
    {
        if (!_byAmxPoint.TryGetValue((row.Node, row.Loop, row.Input), out var device)) return null;
        var key = device.Key;
        if (!_recent.TryGetValue(key, out var list)) _recent[key] = list = new List<DraxEventRow>();
        list.Insert(0, row);
        if (list.Count > 20) list.RemoveAt(list.Count - 1);
        var state = row.On ? StateFor(row) : DeviceState.Normal;
        if (state == DeviceState.Normal) _states.Remove(key); else _states[key] = state;
        return key;
    }

    // Every event from the MQTT feed comes through here (UI thread), whether or not
    // the page is showing, so device states are current when it is opened.
    public void OnEvent(DraxEventRow row)
    {
        // What this device was before the event, to spot the last alarm clearing.
        var before = _byAmxPoint.TryGetValue((row.Node, row.Loop, row.Input), out var dev)
                     && _states.TryGetValue(dev.Key, out var b) ? b : DeviceState.Normal;

        var key = Apply(row);
        if (key == null)
        {
            if (IsResetMessage(row)) ZoomOutAfterReset();
            return;
        }
        var state = _states.TryGetValue(key, out var st) ? st : DeviceState.Normal;

        RefreshCanvasItems();
        if (_slideKey == key) ShowSlideOut(key, animate: false);

        if (row.On && (state == DeviceState.Alarm || state == DeviceState.PreAlarm) && _followButton.Checked
            && _placed.TryGetValue(key, out var placed))
        {
            _ = FollowAlarmAsync(key, placed);
        }
        else if (IsResetMessage(row) ||
                 (!row.On && before is DeviceState.Alarm or DeviceState.PreAlarm && !AnyAlarmActive()))
        {
            ZoomOutAfterReset();
        }
    }

    // A panel reset clears its alarms (the last one clearing is the usual sign); some
    // panels also send a momentary "Fire Reset" message.
    private static bool IsResetMessage(DraxEventRow row) =>
        row.On && (row.Type + " " + row.Text + " " + row.Text2 + " " + row.Text3)
            .Contains("RESET", StringComparison.OrdinalIgnoreCase);

    private bool AnyAlarmActive() =>
        _states.Values.Any(s => s is DeviceState.Alarm or DeviceState.PreAlarm);

    // After a reset, back out to the whole floor and close the device's details.
    private void ZoomOutAfterReset()
    {
        if (!_followButton.Checked || _plan == null) return;
        _canvas.AnimateFit();
        if (_slideKey != null) HideSlideOut();
    }

    private static DeviceState StateFor(DraxEventRow row)
    {
        // AMX input types (enmNotAlarmType in the service): 0 fire, 2 pre-alarm,
        // 4 isolate, 6 test-mode fire, 8 fault, 11-14 sub-address disables.
        switch (row.InputType)
        {
            case 0:
            case 6: return DeviceState.Alarm;
            case 2: return DeviceState.PreAlarm;
            case 8: return DeviceState.Fault;
            case 4:
            case >= 11 and <= 14: return DeviceState.Disabled;
        }
        return row.Family switch
        {
            "alarm" => DeviceState.Alarm,
            "fault" => DeviceState.Fault,
            "isolation" => DeviceState.Disabled,
            _ => DeviceState.Fault,
        };
    }

    private async Task FollowAlarmAsync(string key, PlacedDevice placed)
    {
        if (_floor?.Id != placed.FloorId)
        {
            var floor = _floors.FirstOrDefault(f => f.Id == placed.FloorId);
            if (floor == null) return;
            await ShowFloorAsync(floor);
        }
        if (_plan == null) return;
        AlarmShown?.Invoke();
        _canvas.ZoomTo((float)(placed.X - _plan.OriginX), (float)(placed.Y - _plan.OriginY));
        ShowSlideOut(key);
    }

    // ----------------------------------------------------------------- toolbar

    private void BuildToolbar()
    {
        var plan = new ToolStripDropDownButton("Plan");
        plan.DropDownItems.Add("Open floor plan...", null, async (_, _) => await OpenDrawingAsync());
        plan.DropDownItems.Add("Replace drawing for this floor...", null, async (_, _) => await ReplaceDrawingAsync());
        plan.DropDownItems.Add("Import panel configuration (SKF or NCF)...", null, async (_, _) => await ImportConfigAsync());
        plan.DropDownItems.Add(new ToolStripSeparator());
        plan.DropDownItems.Add("New floor from an area of this drawing", null, (_, _) => StartAreaFloor());
        plan.DropDownItems.Add("Crop floor to view", null, async (_, _) => await CropToViewAsync());
        plan.DropDownItems.Add("Show the whole drawing again", null, async (_, _) => await UncropAsync());
        plan.DropDownItems.Add("Rotate floor clockwise", null, async (_, _) => await RotateFloorAsync(clockwise: true));
        plan.DropDownItems.Add("Rotate floor anticlockwise", null, async (_, _) => await RotateFloorAsync(clockwise: false));
        plan.DropDownItems.Add("Rename floor...", null, (_, _) => RenameFloor());
        plan.DropDownItems.Add("Remove this floor", null, (_, _) => RemoveFloor());
        plan.DropDownItems.Add("Remove floors...", null, (_, _) => RemoveFloors());
        plan.DropDownItems.Add("Remove all floors...", null, (_, _) => RemoveFloors(all: true));
        plan.DropDownItems.Add(new ToolStripSeparator());
        plan.DropDownItems.Add("Layers...", null, (_, _) => ChooseLayers());
        plan.DropDownItems.Add("AMX node offset...", null, (_, _) => SetNodeOffset());

        var fit = new ToolStripButton("Fit") { ToolTipText = "Show the whole floor" };
        fit.Click += (_, _) => _canvas.AnimateFit();
        var zoomIn = new ToolStripButton(" + ") { ToolTipText = "Zoom in", Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
        zoomIn.Click += (_, _) => _canvas.ZoomBy(1.5f);
        var zoomOut = new ToolStripButton(" − ") { ToolTipText = "Zoom out", Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
        zoomOut.Click += (_, _) => _canvas.ZoomBy(1 / 1.5f);
        _gridButton.CheckedChanged += async (_, _) => await ToggleGridAsync();
        _floorBox.SelectedIndexChanged += async (_, _) =>
        {
            if (_floorBox.SelectedItem is PlanFloor f && f.Id != _floor?.Id) await ShowFloorAsync(f);
        };
        _editButton.CheckedChanged += (_, _) => _canvas.AllowEdit = _editButton.Checked;
        _undockButton.Click += (_, _) => UndockRequested?.Invoke();
        _undockButton.Alignment = ToolStripItemAlignment.Right;

        _tools.Items.AddRange(new ToolStripItem[]
        {
            plan, new ToolStripSeparator(), new ToolStripLabel("Floor"), _floorBox, fit, zoomIn, zoomOut, _gridButton,
            new ToolStripSeparator(), _editButton, _followButton, _undockButton, new ToolStripSeparator(), _status,
        });
    }

    private async Task OpenDrawingAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = PlanFileFilter,
            Title = "Open floor plan",
            Multiselect = true,   // a site may send one PDF per floor
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        SetStatus("Checking the files...");
        UseWaitCursor = true;
        List<(string Path, string Hash)> picked;
        List<PlanFloor> known;
        try
        {
            picked = await Task.Run(() => dlg.FileNames.Select(f => (f, FileHash(f))).ToList());
            known = await KnownFloorsAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Could not read the file: " + ex.Message);
            return;
        }
        finally
        {
            UseWaitCursor = false;
        }

        // Files are matched on content (SHA-256), wherever they are now saved.  A DWG
        // already set up is skipped whole; a PDF is checked page by page, so a file
        // opened again in a later stage offers just the pages not yet added.  The same
        // content picked twice in one go is skipped after the first.
        var knownDwg = new Dictionary<string, PlanFloor>(StringComparer.OrdinalIgnoreCase);
        var knownPdfPages = new Dictionary<(string Hash, int Page), PlanFloor>();
        foreach (var f in known.Where(f => f.FileHash.Length > 0))
        {
            if (IsPdf(f.DwgPath)) knownPdfPages.TryAdd((f.FileHash.ToUpperInvariant(), f.Page), f);
            else knownDwg.TryAdd(f.FileHash, f);
        }
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // hash -> first file picked
        string? PickedTwice(string path, string hash)
        {
            if (seen.TryGetValue(hash, out var first)) return $"Same file as {Path.GetFileName(first)}.";
            seen[hash] = path;
            return null;
        }
        string? Duplicate(string path, string hash) =>
            knownDwg.TryGetValue(hash, out var floor) ? $"Already set up as \"{floor.Name}\"." : PickedTwice(path, hash);

        var dwgs = picked.Where(p => !IsPdf(p.Path)).ToList();
        var pdfs = picked.Where(p => IsPdf(p.Path)).ToList();
        var skipped = new List<string>();
        PlanFloor? firstNew = null;

        foreach (var (path, hash) in dwgs)
        {
            var dup = Duplicate(path, hash);
            if (dup != null) { skipped.Add($"{Path.GetFileName(path)}: {dup}"); continue; }
            var floors = await AddDwgFloorsAsync(path, hash);
            firstNew ??= floors.FirstOrDefault();
        }

        if (pdfs.Count > 0)
        {
            var choices = new List<PdfChoice>();
            foreach (var (path, hash) in pdfs)
            {
                var dup = PickedTwice(path, hash);
                int pages;
                try { pages = await Task.Run(() => PdfPlan.CountPages(path)); }
                catch (Exception ex) { skipped.Add($"{Path.GetFileName(path)}: could not be read ({ex.Message})"); continue; }
                var done = Enumerable.Range(0, pages)
                    .Where(pg => knownPdfPages.ContainsKey((hash.ToUpperInvariant(), pg)))
                    .ToDictionary(pg => pg, pg => $"Already set up as \"{knownPdfPages[(hash.ToUpperInvariant(), pg)].Name}\".");
                choices.Add(new PdfChoice(path, pages, dup, done));
            }

            // Just one new page (one PDF, one page, not already set up): nothing to tell
            // apart, so skip the naming step.  The floor takes the file's name, made
            // unique if it's taken, and can be renamed later from the Plan menu.
            bool single = picked.Count == 1 && choices.Count == 1 && choices[0].Pages == 1
                          && choices[0].Duplicate == null && (choices[0].PagesDone?.Count ?? 0) == 0;
            if (single)
            {
                var c = choices[0];
                // Read it first: a PDF that won't render isn't added, and the reason
                // stays in the status bar.
                if (await LoadPlanAsync(c.Path, 0) == null) return;
                var name = UniqueFloorName(Path.GetFileNameWithoutExtension(c.Path));
                var floor = new PlanFloor { Name = name, DwgPath = c.Path, Page = 0, FileHash = pdfs[0].Hash };
                _repo.SaveFloor(floor);
                RefreshFloorBox();
                if (await ShowFloorAsync(floor))
                    SetStatus($"Added as \"{name}\": rename it from Plan > Rename floor if that isn’t the floor’s name.");
                return;
            }
            if (picked.Count == 1 && choices.Count == 1 && choices[0].Pages == 1 &&
                (choices[0].Duplicate != null || (choices[0].PagesDone?.Count ?? 0) > 0))
            {
                // A single file that's already set up: say so rather than open a dialog
                // with nothing in it to add.
                var why = choices[0].Duplicate ?? choices[0].PagesDone!.Values.First();
                RefreshFloorBox();
                SetStatus($"{Path.GetFileName(choices[0].Path)} wasn’t added: {why}");
                return;
            }

            if (choices.Count > 0)
            {
                using var naming = new FloorNamingDialog(choices, _floors.Select(f => f.Name));
                if (naming.ShowDialog(this) == DialogResult.OK)
                {
                    var hashes = pdfs.ToDictionary(p => p.Path, p => p.Hash, StringComparer.OrdinalIgnoreCase);
                    foreach (var (path, page, name) in naming.Floors)
                    {
                        var floor = new PlanFloor { Name = name, DwgPath = path, Page = page, FileHash = hashes[path] };
                        _repo.SaveFloor(floor);
                        firstNew ??= floor;
                    }
                }
            }
        }

        RefreshFloorBox();
        if (firstNew != null) await ShowFloorAsync(firstNew);
        SetStatus(skipped.Count == 0 ? "" : "Not added again: " + string.Join("  ", skipped));
    }

    // A DWG becomes one floor per plan found on the sheet (Mike's X-Ref Basement-5th
    // Floors has all seven side by side), each cropped to its plan; a single plan
    // stays one floor showing the whole drawing.
    // A new revision of a floor's drawing ("Level 2 Rev B"): the floor keeps its
    // name, crop, hidden layers and placed devices, and only its drawing changes.
    // Device positions are stored in the drawing's own coordinates, so they carry
    // over when the revision keeps the same sheet layout, as revisions normally do.
    // Guarded so it can't quietly misplace devices:
    //  - PDF for PDF and DWG for DWG only: the two have unrelated coordinates;
    //  - not the file the floor already uses, nor one that is another floor's drawing;
    //  - a PDF page of a different size asks whether to scale the device positions;
    //  - a DWG that doesn't cover the same area asks before replacing;
    //  - a DWG shared by several floors (one sheet, many plans) asks whether to
    //    replace it for all of them.
    private async Task ReplaceDrawingAsync()
    {
        if (_floor == null || _plan == null) { SetStatus("Open a floor plan first."); return; }
        var floor = _floor;
        var oldPlan = _plan;
        bool pdf = IsPdf(floor.DwgPath);

        using var dlg = new OpenFileDialog
        {
            Filter = pdf ? "PDF floor plans (*.pdf)|*.pdf" : "AutoCAD floor plans (*.dwg)|*.dwg",
            Title = $"Replace the drawing for {floor.Name}",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var path = dlg.FileName;

        string hash;
        List<PlanFloor> known;
        try
        {
            hash = await Task.Run(() => FileHash(path));
            known = await KnownFloorsAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Could not read the file: " + ex.Message);
            return;
        }

        int page = 0;
        if (pdf)
        {
            int pages;
            try { pages = await Task.Run(() => PdfPlan.CountPages(path)); }
            catch (Exception ex) { SetStatus("Could not read the PDF: " + ex.Message); return; }
            if (pages > 1)
            {
                var answer = Microsoft.VisualBasic.Interaction.InputBox(
                    $"{Path.GetFileName(path)} has {pages} pages.  Which page is {floor.Name}?",
                    "Replace drawing", Math.Min(floor.Page + 1, pages).ToString());
                if (answer.Length == 0) return;
                if (!int.TryParse(answer, out var n) || n < 1 || n > pages)
                {
                    MessageBox.Show(this, $"Enter a page number from 1 to {pages}.", "Replace drawing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                page = n - 1;
            }
        }

        bool Same(PlanFloor f) => f.FileHash.Equals(hash, StringComparison.OrdinalIgnoreCase) && (!pdf || f.Page == page);
        // The stored copy: an older floor's fingerprint is only filled in there.
        var stored = known.FirstOrDefault(f => f.Id == floor.Id) ?? floor;
        if (Same(stored))
        {
            MessageBox.Show(this, $"That’s the drawing {floor.Name} already uses.", "Replace drawing", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // The floors that share this floor's drawing (a DWG sheet split into floors).
        var group = new List<PlanFloor> { stored };
        if (!pdf)
        {
            var sharing = known.Where(f => f.Id != floor.Id && !IsPdf(f.DwgPath) &&
                (string.Equals(f.DwgPath, floor.DwgPath, StringComparison.OrdinalIgnoreCase) ||
                 (f.FileHash.Length > 0 && f.FileHash.Equals(floor.FileHash, StringComparison.OrdinalIgnoreCase)))).ToList();
            if (sharing.Count > 0)
            {
                var all = MessageBox.Show(this,
                    $"This drawing is used by {sharing.Count + 1} floors: {string.Join(", ", new[] { floor }.Concat(sharing).Select(f => f.Name))}.\n\n" +
                    "Yes: replace it for all of them (a new revision of the whole sheet).\n" +
                    $"No: replace it for {floor.Name} only.",
                    "Replace drawing", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (all == DialogResult.Cancel) return;
                if (all == DialogResult.Yes) group.AddRange(sharing);
            }
        }

        var clash = known.FirstOrDefault(f => !group.Any(g => g.Id == f.Id) && Same(f));
        if (clash != null)
        {
            MessageBox.Show(this, $"That drawing is already used by the floor \"{clash.Name}\".", "Replace drawing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // At the floor's turn, so the size check below compares like with like and a
        // new revision of a sideways sheet comes in turned the same way.
        var newPlan = await LoadPlanAsync(path, page, stored.Rotation);
        if (newPlan == null) return;

        float sx = 1, sy = 1;
        if (pdf)
        {
            var o = oldPlan.Extents;
            var n = newPlan.Extents;
            if (Math.Abs(n.Width - o.Width) > o.Width * 0.01f || Math.Abs(n.Height - o.Height) > o.Height * 0.01f)
            {
                static string Mm(SKRect r) => $"{r.Width * 25.4f / 72:F0} x {r.Height * 25.4f / 72:F0} mm";
                var scale = MessageBox.Show(this,
                    $"The new page is a different size ({Mm(n)}, was {Mm(o)}).\n\n" +
                    "Yes: scale the placed devices to the new page (the same drawing at a different paper size).\n" +
                    "No: keep the devices where they are.\n" +
                    "Cancel: don’t replace the drawing.",
                    "Replace drawing", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (scale == DialogResult.Cancel) return;
                if (scale == DialogResult.Yes) { sx = n.Width / o.Width; sy = n.Height / o.Height; }
            }
        }
        else
        {
            // In true drawing coordinates, how much of each floor's area does the new
            // drawing cover?
            var newWorld = new SKRect((float)(newPlan.Extents.Left + newPlan.OriginX), (float)(newPlan.Extents.Top + newPlan.OriginY),
                                      (float)(newPlan.Extents.Right + newPlan.OriginX), (float)(newPlan.Extents.Bottom + newPlan.OriginY));
            var poor = group.Where(f =>
            {
                var area = f.HasCrop
                    ? new SKRect((float)f.CropLeft!.Value, (float)f.CropBottom!.Value, (float)f.CropRight!.Value, (float)f.CropTop!.Value)
                    : new SKRect((float)(oldPlan.Extents.Left + oldPlan.OriginX), (float)(oldPlan.Extents.Top + oldPlan.OriginY),
                                 (float)(oldPlan.Extents.Right + oldPlan.OriginX), (float)(oldPlan.Extents.Bottom + oldPlan.OriginY));
                var overlap = SKRect.Intersect(area, newWorld);
                return overlap.IsEmpty || overlap.Width * overlap.Height < area.Width * area.Height * 0.5f;
            }).ToList();
            if (poor.Count > 0 && MessageBox.Show(this,
                    $"The new drawing doesn’t cover the same area as {string.Join(", ", poor.Select(f => f.Name))}, " +
                    "so the placed devices may land in the wrong place.\n\nReplace it anyway?",
                    "Replace drawing", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        }

        foreach (var f in group)
        {
            f.DwgPath = path;
            f.Page = page;
            f.FileHash = hash;
            if (f.HasCrop && (sx != 1 || sy != 1))
            {
                f.CropLeft *= sx; f.CropRight *= sx;
                f.CropBottom *= sy; f.CropTop *= sy;
            }
            _repo.SaveFloor(f);
        }
        int devices = 0;
        foreach (var p in _placed.Values.Where(p => group.Any(g => g.Id == p.FloorId)).ToList())
        {
            devices++;
            if (sx == 1 && sy == 1) continue;
            var moved = p with { X = p.X * sx, Y = p.Y * sy };
            _repo.PlaceDevice(moved);
            _placed[p.Key] = moved;
        }

        RefreshFloorBox();
        await ShowFloorAsync(group[0]);
        SetStatus($"Replaced the drawing for {string.Join(", ", group.Select(f => f.Name))}; " +
                  $"{devices} placed device{(devices == 1 ? "" : "s")} kept{(sx != 1 || sy != 1 ? ", scaled to the new page" : "")}.");
    }

    private async Task<List<PlanFloor>> AddDwgFloorsAsync(string path, string hash)
    {
        var plan = await LoadPlanAsync(path, 0);
        if (plan == null) return new List<PlanFloor>();
        var hidden = plan.Layers.Where(l => l.InitiallyHidden).Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = Path.GetFileNameWithoutExtension(path);

        var areas = await Task.Run(() => plan.FindFloorAreas());
        var floors = new List<PlanFloor>();
        if (areas.Count <= 1)
        {
            floors.Add(new PlanFloor { Name = name, DwgPath = path, HiddenLayers = hidden, FileHash = hash });
        }
        else
        {
            for (int i = 0; i < areas.Count; i++)
            {
                var a = areas[i];
                floors.Add(new PlanFloor
                {
                    Name = $"{name} ({i + 1} of {areas.Count})",
                    DwgPath = path,
                    FileHash = hash,
                    CropLeft = a.Left + plan.OriginX,
                    CropBottom = a.Top + plan.OriginY,   // SKRect Top is the low Y here (plan Y is up)
                    CropRight = a.Right + plan.OriginX,
                    CropTop = a.Bottom + plan.OriginY,
                    HiddenLayers = new HashSet<string>(hidden, StringComparer.OrdinalIgnoreCase),
                });
            }
            SetStatus($"Found {areas.Count} floors on {Path.GetFileName(path)}: rename them from the Plan menu.");
        }
        foreach (var f in floors) _repo.SaveFloor(f);
        return floors;
    }

    // Every floor set up, with its file's content fingerprint.  Floors set up before
    // fingerprints were kept get theirs now, from the file if it's still there.
    // The database is only touched on the UI thread (one SqliteConnection, not safe
    // across threads); only the file hashing runs in the background.
    private async Task<List<PlanFloor>> KnownFloorsAsync()
    {
        var floors = _repo.GetFloors();
        var missing = floors.Where(f => string.IsNullOrEmpty(f.FileHash) && File.Exists(f.DwgPath)).ToList();
        if (missing.Count > 0)
        {
            var hashes = await Task.Run(() => missing.Select(f => FileHash(f.DwgPath)).ToList());
            for (int i = 0; i < missing.Count; i++)
            {
                missing[i].FileHash = hashes[i];
                _repo.SaveFloor(missing[i]);
            }
        }
        return floors;
    }

    // A floor name not already in use: "Level 2", else "Level 2 (2)", "Level 2 (3)"...
    private string UniqueFloorName(string wanted)
    {
        var taken = new HashSet<string>(_repo.GetFloors().Select(f => f.Name.Trim()), StringComparer.OrdinalIgnoreCase);
        var name = string.IsNullOrWhiteSpace(wanted) ? "Floor" : wanted.Trim();
        if (!taken.Contains(name)) return name;
        for (int i = 2; ; i++)
            if (!taken.Contains($"{name} ({i})")) return $"{name} ({i})";
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    // Every file type a floor plan can be opened from, in one filter, so the dialog
    // offers them all at once; add an extension here when a reader is added.
    private static readonly string[] PlanExtensions = { "dwg", "pdf" };
    private static string PlanFileFilter =>
        $"Floor plans ({string.Join("; ", PlanExtensions.Select(e => "*." + e))})|{string.Join(";", PlanExtensions.Select(e => "*." + e))}";

    private static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private void TrimPlanCache()
    {
        for (int i = _planCacheOrder.Count - 1; i >= PlanCacheSize; i--)
        {
            var key = _planCacheOrder[i];
            if (_planCache.TryGetValue(key, out var plan) && ReferenceEquals(plan, _plan)) continue; // on screen
            _planCacheOrder.RemoveAt(i);
            if (_planCache.Remove(key, out var gone)) gone.Dispose();
        }
    }

    private async Task<IPlanSource?> LoadPlanAsync(string path, int page, int rotation = 0)
    {
        if (!File.Exists(path))
        {
            SetStatus("Drawing not found: " + path);
            return null;
        }
        // Keyed on the file as it is now: a revision saved over the old file at the same
        // path has a new write time, so it's read again rather than served stale.
        var key = path + "|" + page + "|" + rotation + "|" + File.GetLastWriteTimeUtc(path).Ticks;
        if (_planCache.TryGetValue(key, out var cached))
        {
            _planCacheOrder.Remove(key);
            _planCacheOrder.Insert(0, key);
            return cached;
        }
        SetStatus("Loading " + Path.GetFileName(path) + "...");
        UseWaitCursor = true;
        try
        {
            IPlanSource plan = IsPdf(path)
                ? await Task.Run(() => PdfPlan.Load(path, page, rotation))
                : await Task.Run(() => DwgPlan.Load(path));
            _planCache[key] = plan;
            _planCacheOrder.Insert(0, key);
            TrimPlanCache();
            return plan;
        }
        catch (Exception ex)
        {
            SetStatus("Could not read the drawing: " + ex.Message);
            return null;
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    // Bumped by each ShowFloorAsync: a load that finishes after a newer one started
    // (a big DWG takes seconds; floors can be switched meanwhile) is dropped.
    private int _showTicket;

    // Returns false if the floor couldn't be shown (the reason is in the status bar).
    // Never throws: its callers are UI event handlers, where an exception would end
    // the app.
    private async Task<bool> ShowFloorAsync(PlanFloor floor)
    {
        try
        {
            return await ShowFloorCoreAsync(floor);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not show {floor.Name}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ShowFloorCoreAsync(PlanFloor floor)
    {
        int ticket = ++_showTicket;
        var plan = await LoadPlanAsync(floor.DwgPath, floor.Page, IsPdf(floor.DwgPath) ? floor.Rotation : 0);
        if (plan == null || ticket != _showTicket) return false;

        var sw = Stopwatch.StartNew();
        SKRect? crop = floor.HasCrop
            ? new SKRect((float)(floor.CropLeft!.Value - plan.OriginX), (float)(floor.CropBottom!.Value - plan.OriginY),
                         (float)(floor.CropRight!.Value - plan.OriginX), (float)(floor.CropTop!.Value - plan.OriginY))
            : IsPdf(floor.DwgPath) ? null
            // A DWG floor without its own crop is drawn only within the plan area Fit
            // shows: legends, title blocks and stray annotation outside it (Level 00's
            // legend) don't appear when zooming out.
            : plan.Extents;
        var picture = await Task.Run(() => plan.Record(floor.HiddenLayers, crop, Ink));
        var recordMs = sw.ElapsedMilliseconds;
        if (ticket != _showTicket) { picture.Dispose(); return false; }

        // Only now does the floor become current: until its picture is ready the old
        // floor stays on screen, so a device dropped meanwhile lands on the floor shown.
        // The Floor list holds its own copies (rebuilt from the database), so select by
        // Id: selecting the object passed in matched nothing and the box kept showing the
        // previous floor (seen after a drawing was split into new floors).
        var listed = _floorBox.Items.Cast<PlanFloor>().FirstOrDefault(f => f.Id == floor.Id);
        _floor = listed ?? floor;
        _plan = plan;
        _repo.SetSetting("FloorId", floor.Id.ToString());
        if (listed != null && _floorBox.SelectedItem != listed) _floorBox.SelectedItem = listed;
        _canvas.SetPlan(picture, crop ?? plan.Extents);
        UpdateGridButton();
        RefreshCanvasItems();
        _canvas.StatusText = $"{Path.GetFileName(floor.DwgPath)}  ·  {plan.Description}, floor built in {recordMs} ms";
        SetStatus("");
        _canvas.Invalidate();
        _list.Invalidate();
        return true;
    }

    // Grid layers: any layer whose name has GRID in it (S-GRID, S-GRID-IDEN...).
    private List<string> GridLayers() =>
        _plan?.Layers.Where(l => l.Name.Contains("GRID", StringComparison.OrdinalIgnoreCase)).Select(l => l.Name).ToList()
        ?? new List<string>();

    private bool _updatingGrid;

    private void UpdateGridButton()
    {
        var grid = GridLayers();
        _updatingGrid = true;
        _gridButton.Enabled = grid.Count > 0;
        _gridButton.Checked = grid.Count == 0 || grid.Any(n => !(_floor?.HiddenLayers.Contains(n) ?? false));
        // A PDF has no layers, so its grid lines can't be told apart from the rest.
        _gridButton.ToolTipText = grid.Count > 0 ? "Show or hide the drawing’s grid lines"
            : _plan?.Layers.Count == 0 ? "A PDF has no layers, so its grid lines can’t be switched off"
            : "This drawing has no grid layers";
        GridButtonText();
        _updatingGrid = false;
    }

    // Says which state it's in: a pressed/unpressed toolbar button is easy to misread.
    private void GridButtonText() =>
        _gridButton.Text = !_gridButton.Enabled ? "Grid" : _gridButton.Checked ? "Grid on" : "Grid off";

    // Switches the floor's grid layers on or off, remembered for the floor.
    private async Task ToggleGridAsync()
    {
        if (_updatingGrid || _floor == null) return;
        GridButtonText();
        var grid = GridLayers();
        if (grid.Count == 0) return;
        foreach (var n in grid)
        {
            if (_gridButton.Checked) _floor.HiddenLayers.Remove(n);
            else _floor.HiddenLayers.Add(n);
        }
        _repo.SaveFloor(_floor);
        await ShowFloorAsync(_floor);
    }

    // Crop the floor to what's on screen now: for a drawing whose plan area isn't
    // picked up right, zoom to the plan and crop.
    private async Task CropToViewAsync()
    {
        if (_floor == null || _plan == null) { SetStatus("Open a floor plan first."); return; }
        var v = _canvas.VisibleArea;
        if (v.Width <= 0 || v.Height <= 0) return;
        _floor.CropLeft = v.Left + _plan.OriginX;
        _floor.CropBottom = v.Top + _plan.OriginY;     // SKRect Top is the low Y here (plan Y is up)
        _floor.CropRight = v.Right + _plan.OriginX;
        _floor.CropTop = v.Bottom + _plan.OriginY;
        _repo.SaveFloor(_floor);
        await ShowFloorAsync(_floor);
        SetStatus($"{_floor.Name} cropped to the view.  Plan > Show the whole drawing again undoes it.");
    }

    // Turns a PDF floor a quarter turn, for a sheet drawn or scanned sideways.  The
    // crop and the devices placed on the floor turn with it, so nothing needs placing
    // again.  A DWG is drawn the right way up already.
    private async Task RotateFloorAsync(bool clockwise)
    {
        if (_floor == null || _plan == null) { SetStatus("Open a floor plan first."); return; }
        if (!IsPdf(_floor.DwgPath)) { SetStatus("Rotate is for PDF floors: a DWG is already drawn the right way up."); return; }

        var floor = _floor;
        // The page as shown now, in plan coordinates (Y up, from 0,0).
        double w = _plan.Extents.Width, h = _plan.Extents.Height;
        // Clockwise the left edge becomes the top: (x, y) -> (y, w - x).
        // Anticlockwise the bottom edge becomes the right: (x, y) -> (h - y, x).
        (double X, double Y) Turn(double x, double y) => clockwise ? (y, w - x) : (h - y, x);

        if (floor.HasCrop)
        {
            var a = Turn(floor.CropLeft!.Value, floor.CropBottom!.Value);
            var b = Turn(floor.CropRight!.Value, floor.CropTop!.Value);
            floor.CropLeft = Math.Min(a.X, b.X);
            floor.CropRight = Math.Max(a.X, b.X);
            floor.CropBottom = Math.Min(a.Y, b.Y);
            floor.CropTop = Math.Max(a.Y, b.Y);
        }
        floor.Rotation = PdfPlan.NormaliseRotation(floor.Rotation + (clockwise ? 90 : -90));
        _repo.SaveFloor(floor);

        int moved = 0;
        foreach (var p in _placed.Values.Where(p => p.FloorId == floor.Id).ToList())
        {
            var t = Turn(p.X, p.Y);
            var turned = p with { X = t.X, Y = t.Y };
            _repo.PlaceDevice(turned);
            _placed[p.Key] = turned;
            moved++;
        }

        if (await ShowFloorAsync(floor))
            SetStatus($"{floor.Name} turned {(clockwise ? "clockwise" : "anticlockwise")}" +
                      (floor.Rotation != 0 ? $" ({floor.Rotation}° from the PDF)" : " (back as the PDF has it)") +
                      (moved > 0 ? $"; {moved} placed device{(moved == 1 ? "" : "s")} turned with it." : "."));
    }

    private async Task UncropAsync()
    {
        if (_floor == null) { SetStatus("Open a floor plan first."); return; }
        if (!_floor.HasCrop) { SetStatus($"{_floor.Name} already shows the whole drawing."); return; }
        _floor.CropLeft = _floor.CropBottom = _floor.CropRight = _floor.CropTop = null;
        _repo.SaveFloor(_floor);
        await ShowFloorAsync(_floor);
    }

    private void StartAreaFloor()
    {
        if (_plan == null || _floor == null) { SetStatus("Open a floor plan first."); return; }
        SetStatus("Drag a box round the floor you want...");
        _canvas.DefineArea();
    }

    private async void OnAreaDefined(SKRect area)
    {
        if (_plan == null || _floor == null) return;
        var name = Microsoft.VisualBasic.Interaction.InputBox("Name for this floor:", "New floor", "Level " + (_floors.Count));
        if (string.IsNullOrWhiteSpace(name)) { SetStatus(""); return; }
        var floor = new PlanFloor
        {
            Name = name.Trim(),
            DwgPath = _floor.DwgPath,
            Page = _floor.Page,
            Rotation = _floor.Rotation,   // the area was drawn on the page as turned
            CropLeft = area.Left + _plan.OriginX,
            CropBottom = area.Top + _plan.OriginY,     // SKRect Top is the low Y here (plan Y is up)
            CropRight = area.Right + _plan.OriginX,
            CropTop = area.Bottom + _plan.OriginY,
            HiddenLayers = new HashSet<string>(_floor.HiddenLayers, StringComparer.OrdinalIgnoreCase),
        };
        _repo.SaveFloor(floor);
        RefreshFloorBox();
        await ShowFloorAsync(floor);
    }

    private void RenameFloor()
    {
        if (_floor == null) return;
        var name = Microsoft.VisualBasic.Interaction.InputBox("Floor name:", "Rename floor", _floor.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        _floor.Name = name.Trim();
        _repo.SaveFloor(_floor);
        RefreshFloorBox();
    }

    private async void RemoveFloor()
    {
        if (_floor == null) return;
        int onFloor = _placed.Values.Count(p => p.FloorId == _floor.Id);
        if (MessageBox.Show(this, $"Remove the floor \"{_floor.Name}\"" + (onFloor > 0 ? $" and the {onFloor} device(s) placed on it" : "") + "?",
                "Remove floor", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        _repo.DeleteFloor(_floor.Id);
        _placed = _repo.GetPlacedDevices().ToDictionary(p => p.Key);
        _floor = null;
        _plan = null;
        RefreshFloorBox();
        if (_floors.Count > 0) await ShowFloorAsync(_floors[0]);
        else { _canvas.SetPlan(null, SKRect.Empty); RefreshCanvasItems(); }
        FilterList(); // its devices are back as not yet placed
    }

    // Several floors at once: all the floors from one file (a DWG sheet's seven, a
    // PDF set a site has replaced), or everything.  The dialog lists them by file
    // with device counts; this confirms with the totals before removing anything.
    private async void RemoveFloors(bool all = false)
    {
        var floors = _repo.GetFloors();
        if (floors.Count == 0) { SetStatus("There are no floors to remove."); return; }
        var counts = _placed.Values.GroupBy(p => p.FloorId).ToDictionary(g => g.Key, g => g.Count());

        using var dlg = new RemoveFloorsDialog(floors, counts, selectAll: all);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Selected.Count == 0) return;
        var chosen = dlg.Selected.ToList();
        int devices = chosen.Sum(f => counts.TryGetValue(f.Id, out var n) ? n : 0);
        // Cancel is the default button: Enter must never remove floors.  Removing every
        // floor says so in as many words.
        bool everything = chosen.Count == floors.Count;
        // Name them (the first dozen), so the confirmation shows what goes, not just how many.
        var names = string.Join("\n", chosen.Take(12).Select(f => "  " + f.Name)) +
                    (chosen.Count > 12 ? $"\n  ...and {chosen.Count - 12} more" : "");
        if (MessageBox.Show(this,
                (everything ? $"Remove ALL {chosen.Count} floors" : $"Remove {chosen.Count} floor{(chosen.Count == 1 ? "" : "s")}") +
                (devices > 0 ? $" and the {devices} device{(devices == 1 ? "" : "s")} placed on {(chosen.Count == 1 ? "it" : "them")}" : "") +
                "?\n\n" + names + "\n\n" +
                (everything ? "The plan will be empty until a floor plan is opened again.  " : "") + "This can’t be undone.",
                everything ? "Remove all floors" : "Remove floors",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

        foreach (var f in chosen) _repo.DeleteFloor(f.Id);
        bool currentGone = _floor != null && chosen.Any(f => f.Id == _floor.Id);
        _placed = _repo.GetPlacedDevices().ToDictionary(p => p.Key);
        if (currentGone) { _floor = null; _plan = null; }
        RefreshFloorBox();
        if (currentGone)
        {
            if (_floors.Count > 0) await ShowFloorAsync(_floors[0]);
            else { _canvas.SetPlan(null, SKRect.Empty); RefreshCanvasItems(); }
        }
        else
        {
            RefreshCanvasItems();
        }
        FilterList();
        SetStatus($"Removed {chosen.Count} floor{(chosen.Count == 1 ? "" : "s")}" +
                  (devices > 0 ? $"; {devices} device{(devices == 1 ? " is" : "s are")} back in the list as not placed." : "."));
    }

    private async void ChooseLayers()
    {
        if (_plan == null || _floor == null) return;
        if (_plan.Layers.Count == 0)
        {
            MessageBox.Show(this, "A PDF has no layers to switch on or off.", "Layers", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var form = new Form
        {
            Text = "Layers shown on " + _floor.Name, Width = 420, Height = 560, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.SizableToolWindow, TopMost = true, MinimizeBox = false, MaximizeBox = false,
        };
        var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
        foreach (var l in _plan.Layers.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            list.Items.Add(l.Name, !_floor.HiddenLayers.Contains(l.Name));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        var all = new Button { Text = "All" };
        var none = new Button { Text = "None" };
        all.Click += (_, _) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, true); };
        none.Click += (_, _) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, false); };
        buttons.Controls.AddRange(new Control[] { ok, none, all });
        form.Controls.Add(list);
        form.Controls.Add(buttons);
        form.AcceptButton = ok;
        if (form.ShowDialog(this) != DialogResult.OK) return;

        _floor.HiddenLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < list.Items.Count; i++)
            if (!list.GetItemChecked(i)) _floor.HiddenLayers.Add((string)list.Items[i]);
        _repo.SaveFloor(_floor);
        await ShowFloorAsync(_floor);
    }

    private void SetNodeOffset()
    {
        var text = Microsoft.VisualBasic.Interaction.InputBox(
            "The panel's AMX node offset (giAmx1Offset in AdvMan.ini). A device on configuration panel N reports to AMX as node N + offset.",
            "AMX node offset", _nodeOffset.ToString());
        if (!int.TryParse(text, out var o)) return;
        _nodeOffset = o;
        _repo.SetSetting("NodeOffset", o.ToString());
        RebuildAmxIndex();
    }

    private void RefreshFloorBox()
    {
        _floors = _repo.GetFloors();
        _floorBox.Items.Clear();
        foreach (var f in _floors) _floorBox.Items.Add(f);
        var current = _floors.FirstOrDefault(f => f.Id == _floor?.Id);
        if (current != null) { _floor = current; _floorBox.SelectedItem = current; }
    }

    private void SetStatus(string text) => _status.Text = text;

    // ------------------------------------------------------------- device list

    private void BuildList()
    {
        _list.Columns.Add("Device", 96);
        _list.Columns.Add("Location", 160);
        _list.Columns.Add("Zone", 80);
        _list.RetrieveVirtualItem += (_, e) => e.Item = ListItem(_listRows[e.ItemIndex]);
        _list.ItemDrag += (_, e) =>
        {
            if (_list.SelectedIndices.Count == 0 || !_editButton.Checked) return;
            var d = _listRows[_list.SelectedIndices[0]];
            _list.DoDragDrop(new DataObject(PlanCanvas.DragFormat, d.Key), DragDropEffects.Copy);
        };
        _list.DoubleClick += async (_, _) =>
        {
            if (_list.SelectedIndices.Count == 0) return;
            var d = _listRows[_list.SelectedIndices[0]];
            if (_placed.TryGetValue(d.Key, out var p)) await GoToDeviceAsync(d.Key, p);
            else ShowSlideOut(d.Key);
        };
        _search.TextChanged += (_, _) => FilterList();
        _unplacedOnly.CheckedChanged += (_, _) => FilterList();

        _split.Panel1.Controls.Add(_list);
        _split.Panel1.Controls.Add(_unplacedOnly);
        _split.Panel1.Controls.Add(_search);
        _split.Panel1.Controls.Add(_listInfo);
    }

    private ListViewItem ListItem(PanelDevice d)
    {
        var item = new ListViewItem(new[] { d.RefText, d.Location, d.Zone > 0 ? $"{d.Zone} {d.ZoneText}" : "" });
        if (_placed.ContainsKey(d.Key)) item.ForeColor = Color.Gray; // already on a plan
        if (_states.TryGetValue(d.Key, out var s) && s != DeviceState.Normal)
            item.BackColor = AmxSymbols.StateColor(s).ToDrawingColor(70);
        return item;
    }

    private async Task GoToDeviceAsync(string key, PlacedDevice p)
    {
        if (_floor?.Id != p.FloorId)
        {
            var floor = _floors.FirstOrDefault(f => f.Id == p.FloorId);
            if (floor == null) return;
            await ShowFloorAsync(floor);
        }
        if (_plan == null) return;
        _canvas.ZoomTo((float)(p.X - _plan.OriginX), (float)(p.Y - _plan.OriginY));
        ShowSlideOut(key);
    }

    private void FilterList()
    {
        var q = _search.Text.Trim();
        IEnumerable<PanelDevice> rows = _devices.Values;
        if (_unplacedOnly.Checked) rows = rows.Where(d => !_placed.ContainsKey(d.Key));
        if (q.Length > 0)
            rows = rows.Where(d =>
                d.Location.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.ZoneText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.NodeName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                d.RefText.Contains(q, StringComparison.OrdinalIgnoreCase));
        _listRows = rows.ToList();
        _list.VirtualListSize = _listRows.Count;
        _list.Invalidate();
        UpdateListInfo();
    }

    private void UpdateListInfo()
    {
        _listInfo.Text = _config == null
            ? "No panel configuration loaded: Plan > Import panel configuration (SKF or NCF)."
            : $"{_config.SiteName} ({_config.Format})  ·  {_devices.Count:N0} devices on {_config.PanelCount} panels, {_placed.Count:N0} placed  ·  showing {_listRows.Count:N0}";
    }

    private async Task ImportConfigAsync()
    {
        using var dlg = new OpenFileDialog { Filter = PanelConfig.FileFilter, Title = "Import panel configuration" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (await LoadConfigAsync(dlg.FileName)) _repo.SetSetting("ConfigPath", dlg.FileName);
    }

    // Read fully before anything is swapped in: a file that fails part-way leaves the
    // previous configuration (devices, event routing, list) exactly as it was.
    private async Task<bool> LoadConfigAsync(string path)
    {
        SetStatus("Reading " + Path.GetFileName(path) + "...");
        try
        {
            var config = await Task.Run(() => PanelConfig.Load(path));
            var devices = config.Devices.GroupBy(d => d.Key).ToDictionary(g => g.Key, g => g.First());
            _config = config;
            _devices = devices;
            RebuildAmxIndex();
            FilterList();
            RefreshCanvasItems();
            SetStatus("");
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("Could not read the panel configuration: " + ex.Message);
            UpdateListInfo();
            return false;
        }
    }

    private void RebuildAmxIndex()
    {
        _byAmxPoint = new();
        foreach (var d in _devices.Values) _byAmxPoint[(d.Node + _nodeOffset, d.Loop, d.Address)] = d;
    }

    // -------------------------------------------------------------- placement

    private void OnDeviceDropped(string key, float x, float y)
    {
        if (_floor == null || _plan == null || !_devices.TryGetValue(key, out var d)) return;
        var placed = new PlacedDevice(_floor.Id, d.Node, d.Loop, d.Address, x + _plan.OriginX, y + _plan.OriginY);
        _repo.PlaceDevice(placed);
        _placed[key] = placed;
        RefreshCanvasItems();
        _canvas.SelectedKey = key;
        if (_unplacedOnly.Checked) FilterList(); else { _list.Invalidate(); UpdateListInfo(); }
    }

    private void OnDeviceMoved(string key, float x, float y)
    {
        if (_floor == null || _plan == null || !_placed.TryGetValue(key, out var p)) return;
        var moved = p with { FloorId = _floor.Id, X = x + _plan.OriginX, Y = y + _plan.OriginY };
        _repo.PlaceDevice(moved);
        _placed[key] = moved;
    }

    private AmxSymbol SymbolFor(PanelDevice d) =>
        _symbolMap.TryGetValue(d.SymbolCode, out var s) ? s : AmxSymbols.Default(d);

    private void RefreshCanvasItems()
    {
        if (_floor == null || _plan == null) { _canvas.SetItems(Array.Empty<PlotItem>()); return; }
        var items = new List<PlotItem>();
        foreach (var p in _placed.Values.Where(p => p.FloorId == _floor.Id))
        {
            if (!_devices.TryGetValue(p.Key, out var d)) continue; // not in the loaded configuration
            items.Add(new PlotItem
            {
                Key = p.Key,
                X = (float)(p.X - _plan.OriginX),
                Y = (float)(p.Y - _plan.OriginY),
                Symbol = SymbolFor(d),
                State = _states.TryGetValue(p.Key, out var s) ? s : DeviceState.Normal,
            });
        }
        _canvas.SetItems(items);
        _list.Invalidate();
    }

    // -------------------------------------------------------------- slide-out

    private void BuildSlideOut()
    {
        foreach (AmxSymbol s in Enum.GetValues<AmxSymbol>()) _sSymbol.Items.Add(new SymbolChoice(s));
        _sSymbol.SelectionChangeCommitted += (_, _) =>
        {
            if (_slideKey == null || !_devices.TryGetValue(_slideKey, out var d) || _sSymbol.SelectedItem is not SymbolChoice c) return;
            // One choice sets the symbol for every device with this panel device code,
            // so the site's symbols are mapped in a handful of clicks.
            _symbolMap[d.SymbolCode] = c.Symbol;
            _repo.SetSymbol(d.SymbolCode, c.Symbol);
            RefreshCanvasItems();
            ShowSlideOut(_slideKey, animate: false);
        };
        _sClose.Click += (_, _) => HideSlideOut();
        _sRemove.Click += (_, _) =>
        {
            if (_slideKey == null || !_devices.TryGetValue(_slideKey, out var d)) return;
            _repo.RemoveDevice(d.Node, d.Loop, d.Address);
            _placed.Remove(_slideKey);
            RefreshCanvasItems();
            FilterList();
            HideSlideOut();
        };
        _sButtons.Controls.AddRange(new Control[] { _sClose, _sRemove });

        // Docked controls stack in reverse order of adding: last added sits on top.
        _slide.Controls.Add(_sEvents);
        _slide.Controls.Add(_sEventsLabel);
        _slide.Controls.Add(_sSymbolNote);
        _slide.Controls.Add(_sSymbol);
        _slide.Controls.Add(_sSymbolLabel);
        _slide.Controls.Add(_sDetails);
        _slide.Controls.Add(_sState);
        _slide.Controls.Add(_sTitle);
        _slide.Controls.Add(_sButtons);

        _slideTimer.Tick += (_, _) =>
        {
            int step = Math.Max(12, Math.Abs(_slideTarget - _slide.Width) / 3);
            _slide.Width = _slide.Width < _slideTarget ? Math.Min(_slideTarget, _slide.Width + step) : Math.Max(_slideTarget, _slide.Width - step);
            if (_slide.Width == _slideTarget) _slideTimer.Stop();
        };
    }

    private void ShowSlideOut(string key, bool animate = true)
    {
        if (!_devices.TryGetValue(key, out var d)) return;
        _slideKey = key;
        _canvas.SelectedKey = key;

        _sTitle.Text = string.IsNullOrEmpty(d.Location) ? d.RefText : d.Location;
        var state = _states.TryGetValue(key, out var s) ? s : DeviceState.Normal;
        _sState.Text = state switch
        {
            DeviceState.Alarm => "FIRE ALARM",
            DeviceState.PreAlarm => "PRE-ALARM",
            DeviceState.Fault => "FAULT",
            DeviceState.Disabled => "DISABLED",
            _ => "Normal",
        };
        _sState.BackColor = state == DeviceState.Normal ? Color.FromArgb(225, 240, 225) : AmxSymbols.StateColor(state).ToDrawingColor(255);
        _sState.ForeColor = state is DeviceState.Alarm or DeviceState.Disabled ? Color.White : Color.Black;

        _sDetails.Text =
            $"AMX point   N{d.Node + _nodeOffset} L{d.Loop} D{d.Address}\n" +
            $"Panel       {d.Node}  {d.NodeName}\n" +
            $"Zone        {d.Zone}  {d.ZoneText}\n" +
            $"Type        {DeviceTypeText(d)}\n" +
            $"Device code {d.CodeText}\n" +
            (_placed.TryGetValue(key, out var p) ? $"Plan        {_floors.FirstOrDefault(f => f.Id == p.FloorId)?.Name}" : "Plan        not placed yet");

        var symbol = SymbolFor(d);
        _sSymbol.SelectedItem = _sSymbol.Items.Cast<SymbolChoice>().First(c => c.Symbol == symbol);
        int sameCode = _devices.Values.Count(x => x.SymbolCode == d.SymbolCode);
        _sSymbolNote.Text = $"Applies to all {sameCode:N0} devices with {d.Format} device code {d.DeviceId}.";
        _sRemove.Visible = _placed.ContainsKey(key);

        _sEvents.Items.Clear();
        if (_recent.TryGetValue(key, out var events))
            foreach (var e in events)
                _sEvents.Items.Add($"{e.ReceivedLocal:dd/MM HH:mm:ss}  {(e.On ? "ON " : "off")}  {e.Type}  {e.CombinedText}");
        else
            _sEvents.Items.Add("No events since the app started.");

        _slideTarget = 320;
        if (animate && _slide.Width < _slideTarget) _slideTimer.Start(); else _slide.Width = _slideTarget;
    }

    // "input + output, 3 channels".  An NCF doesn't record input/output, so it shows
    // just the channels, or "not given".
    private static string DeviceTypeText(PanelDevice d)
    {
        var parts = new List<string>();
        var io = string.Join(" + ", new[] { d.IsInput ? "input" : null, d.IsOutput ? "output" : null }.Where(x => x != null));
        if (io.Length > 0) parts.Add(io);
        if (d.Channels > 1) parts.Add($"{d.Channels} channels");
        return parts.Count > 0 ? string.Join(", ", parts) : "not given";
    }

    private void HideSlideOut()
    {
        _slideKey = null;
        _canvas.SelectedKey = null;
        _slideTarget = 0;
        _slideTimer.Start();
    }

    private sealed record SymbolChoice(AmxSymbol Symbol)
    {
        public override string ToString() => AmxSymbols.Label(Symbol);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _slideTimer.Dispose();
        base.Dispose(disposing);
    }
}

internal static class SkColorExtensions
{
    public static Color ToDrawingColor(this SKColor c, byte alpha) => Color.FromArgb(alpha, c.Red, c.Green, c.Blue);
}
