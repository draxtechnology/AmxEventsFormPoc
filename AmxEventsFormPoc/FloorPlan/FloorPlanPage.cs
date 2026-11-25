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
    private DwgPlan? _plan;
    private readonly Dictionary<string, DwgPlan> _planCache = new(StringComparer.OrdinalIgnoreCase);
    private int _nodeOffset;
    private List<PanelDevice> _listRows = new();
    private string? _slideKey;

    // --- controls ---
    private readonly ToolStrip _tools = new() { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top, Padding = new Padding(4, 2, 4, 2) };
    private readonly ToolStripComboBox _floorBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly ToolStripButton _editButton = new("Edit") { CheckOnClick = true, Checked = true, ToolTipText = "Drag devices from the list onto the plan, and move them" };
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
    private readonly Label _sDetails = new() { Dock = DockStyle.Top, Height = 132 };
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
        var key = Apply(row);
        if (key == null) return;
        var state = _states.TryGetValue(key, out var st) ? st : DeviceState.Normal;

        RefreshCanvasItems();
        if (_slideKey == key) ShowSlideOut(key, animate: false);

        if (row.On && (state == DeviceState.Alarm || state == DeviceState.PreAlarm) && _followButton.Checked
            && _placed.TryGetValue(key, out var placed))
        {
            _ = FollowAlarmAsync(key, placed);
        }
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
        plan.DropDownItems.Add("Open drawing (DWG)...", null, async (_, _) => await OpenDrawingAsync());
        plan.DropDownItems.Add("Import panel configuration (SKF or NCF)...", null, async (_, _) => await ImportConfigAsync());
        plan.DropDownItems.Add(new ToolStripSeparator());
        plan.DropDownItems.Add("New floor from an area of this drawing", null, (_, _) => StartAreaFloor());
        plan.DropDownItems.Add("Rename floor...", null, (_, _) => RenameFloor());
        plan.DropDownItems.Add("Remove floor", null, (_, _) => RemoveFloor());
        plan.DropDownItems.Add(new ToolStripSeparator());
        plan.DropDownItems.Add("Layers...", null, (_, _) => ChooseLayers());
        plan.DropDownItems.Add("AMX node offset...", null, (_, _) => SetNodeOffset());

        var fit = new ToolStripButton("Fit") { ToolTipText = "Show the whole floor" };
        fit.Click += (_, _) => _canvas.FitToContent();
        _floorBox.SelectedIndexChanged += async (_, _) =>
        {
            if (_floorBox.SelectedItem is PlanFloor f && f.Id != _floor?.Id) await ShowFloorAsync(f);
        };
        _editButton.CheckedChanged += (_, _) => _canvas.AllowEdit = _editButton.Checked;
        _undockButton.Click += (_, _) => UndockRequested?.Invoke();
        _undockButton.Alignment = ToolStripItemAlignment.Right;

        _tools.Items.AddRange(new ToolStripItem[]
        {
            plan, new ToolStripSeparator(), new ToolStripLabel("Floor"), _floorBox, fit,
            new ToolStripSeparator(), _editButton, _followButton, _undockButton, new ToolStripSeparator(), _status,
        });
    }

    private async Task OpenDrawingAsync()
    {
        using var dlg = new OpenFileDialog { Filter = "AutoCAD drawings (*.dwg)|*.dwg", Title = "Open a floor plan drawing" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var plan = await LoadPlanAsync(dlg.FileName);
        if (plan == null) return;
        var floor = new PlanFloor
        {
            Name = Path.GetFileNameWithoutExtension(dlg.FileName),
            DwgPath = dlg.FileName,
            HiddenLayers = plan.Layers.Where(l => l.InitiallyHidden).Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
        };
        _repo.SaveFloor(floor);
        RefreshFloorBox();
        await ShowFloorAsync(floor);
    }

    private async Task<DwgPlan?> LoadPlanAsync(string path)
    {
        if (_planCache.TryGetValue(path, out var cached)) return cached;
        if (!File.Exists(path))
        {
            SetStatus("Drawing not found: " + path);
            return null;
        }
        SetStatus("Loading " + Path.GetFileName(path) + "...");
        UseWaitCursor = true;
        try
        {
            var plan = await Task.Run(() => DwgPlan.Load(path));
            _planCache[path] = plan;
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

    private async Task ShowFloorAsync(PlanFloor floor)
    {
        var plan = await LoadPlanAsync(floor.DwgPath);
        if (plan == null) return;
        _floor = floor;
        _plan = plan;
        _repo.SetSetting("FloorId", floor.Id.ToString());
        if (_floorBox.SelectedItem != floor) _floorBox.SelectedItem = floor;

        var sw = Stopwatch.StartNew();
        SKRect? crop = floor.HasCrop
            ? new SKRect((float)(floor.CropLeft!.Value - plan.OriginX), (float)(floor.CropBottom!.Value - plan.OriginY),
                         (float)(floor.CropRight!.Value - plan.OriginX), (float)(floor.CropTop!.Value - plan.OriginY))
            : null;
        var picture = await Task.Run(() => plan.Record(floor.HiddenLayers, crop, Ink));
        var recordMs = sw.ElapsedMilliseconds;
        _canvas.SetPlan(picture, crop ?? plan.Extents);
        RefreshCanvasItems();
        _canvas.StatusText = $"{Path.GetFileName(floor.DwgPath)}  ·  read {plan.ReadTime.TotalSeconds:F1} s, prepared {plan.FlattenTime.TotalSeconds:F1} s, " +
                             $"{plan.SegmentCount / 1000:N0}k line segments, floor built in {recordMs} ms";
        SetStatus("");
        _canvas.Invalidate();
        _list.Invalidate();
    }

    private void StartAreaFloor()
    {
        if (_plan == null || _floor == null) { SetStatus("Open a drawing first."); return; }
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
                "Remove floor", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _repo.DeleteFloor(_floor.Id);
        _placed = _repo.GetPlacedDevices().ToDictionary(p => p.Key);
        _floor = null;
        _plan = null;
        RefreshFloorBox();
        if (_floors.Count > 0) await ShowFloorAsync(_floors[0]);
        else { _canvas.SetPlan(null, SKRect.Empty); RefreshCanvasItems(); }
        _list.Invalidate();
    }

    private async void ChooseLayers()
    {
        if (_plan == null || _floor == null) return;
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
        _config = null;
        await LoadConfigAsync(dlg.FileName);
        if (_config != null) _repo.SetSetting("ConfigPath", dlg.FileName);
    }

    private async Task LoadConfigAsync(string path)
    {
        SetStatus("Reading " + Path.GetFileName(path) + "...");
        try
        {
            _config = await Task.Run(() => PanelConfig.Load(path));
            _devices = _config.Devices.ToDictionary(d => d.Key);
            RebuildAmxIndex();
            FilterList();
            RefreshCanvasItems();
            SetStatus("");
        }
        catch (Exception ex)
        {
            SetStatus("Could not read the panel configuration: " + ex.Message);
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
            $"Type        {(d.IsInput ? "input" : "")}{(d.IsInput && d.IsOutput ? " + " : "")}{(d.IsOutput ? "output" : "")}" +
            $"{(d.Channels > 1 ? $", {d.Channels} channels" : "")}\n" +
            $"Device code {d.CodeText}\n" +
            (_placed.TryGetValue(key, out var p) ? $"Plan        {_floors.FirstOrDefault(f => f.Id == p.FloorId)?.Name}" : "Plan        not placed yet");
        _sDetails.Font = new Font("Consolas", 9f);

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
