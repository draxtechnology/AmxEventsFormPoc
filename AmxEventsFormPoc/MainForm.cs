using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AmxEventsFormPoc.Models;
using AmxEventsFormPoc.Services;
using Microsoft.VisualBasic;

namespace AmxEventsFormPoc;

public partial class MainForm : Form
{
    // POC-level only: this proves the database is genuinely encrypted (see
    // EventsRepository.Initialize/SetKey/Rekey) so it's demonstrable today, but a fixed
    // string in source code is readable by anyone with the built .exe (or this file) —
    // it is NOT where a real deployment's key should live. Before this goes near real
    // site data, move it to something per-install and not shipped in the binary, e.g.
    // Windows DPAPI (System.Security.Cryptography.ProtectedData) writing a generated
    // key out to a file under LocalApplicationData the first time the app runs, or the
    // Windows Credential Manager.
    private const string EventsDbEncryptionKey = "Drax-AmxEventsFormPoc-POC-Key-Change-Me";

    private readonly List<DraxEventRow> _allEvents = new();
    private readonly BindingList<DraxEventRow> _visibleEvents = new();
    private MqttEventsClient _mqtt = null!; // not readonly — assigned in OnLoad, not the constructor
    private EventsRepository _repo = null!; // not readonly — assigned in OnLoad, not the constructor

    // --- History page: a second "page" that swaps into the same space eventsGrid
    //     occupies, rather than a separate window — btHistory hides eventsGrid and
    //     shows this instead; btBack swaps back. Built entirely in code, same as
    //     eventsGrid's own columns, and for the same reason (see the comment on
    //     ConfigureEventsGridColumns below). ---
    private readonly Panel pnlHistoryPage = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Panel pnlHistoryFilter = new() { Dock = DockStyle.Top, Height = 50 };
    private readonly DataGridView historyGrid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoGenerateColumns = false,
        RowHeadersWidth = 25,
    };
    private readonly Label lblHistoryFrom = new() { Text = "From", AutoSize = true, Location = new Point(10, 16) };
    private readonly DateTimePicker dtHistoryFrom = new()
    {
        Format = DateTimePickerFormat.Short,
        Location = new Point(50, 12),
        Size = new Size(120, 23),
        Value = DateTime.Today.AddDays(-7),
    };
    private readonly Label lblHistoryTo = new() { Text = "To", AutoSize = true, Location = new Point(186, 16) };
    private readonly DateTimePicker dtHistoryTo = new()
    {
        Format = DateTimePickerFormat.Short,
        Location = new Point(210, 12),
        Size = new Size(120, 23),
        Value = DateTime.Today,
    };
    private readonly Button btHistorySearch = new() { Text = "Search", Location = new Point(346, 11), Size = new Size(90, 25) };
    private readonly Label lblHistoryCount = new() { AutoSize = true, Location = new Point(452, 16) };
    private readonly BindingList<DraxEventRow> _historyRows = new();

    private readonly DataGridViewTextBoxColumn hColDate = new();
    private readonly DataGridViewTextBoxColumn hColTime = new();
    private readonly DataGridViewTextBoxColumn hColType = new();
    private readonly DataGridViewTextBoxColumn hColAmxRef = new();
    private readonly DataGridViewTextBoxColumn hColInputType = new();
    private readonly DataGridViewTextBoxColumn hColText = new();

    // eventsGrid's own columns — built here in code rather than in
    // MainForm.Designer.cs, and instantiated up front so ConfigureEventsGridColumns()
    // below can be called from the constructor. See the comment on eventsGrid in
    // MainForm.Designer.cs for why: this exact set of columns has twice now been
    // silently dropped by a Designer resave triggered by an unrelated edit, so it no
    // longer lives anywhere the Designer can touch it.
    private readonly DataGridViewTextBoxColumn colDate = new();
    private readonly DataGridViewTextBoxColumn colTime = new();
    private readonly DataGridViewTextBoxColumn colType = new();
    private readonly DataGridViewTextBoxColumn colAmxRef = new();
    private readonly DataGridViewTextBoxColumn colInputType = new();
    private readonly DataGridViewTextBoxColumn colText = new();

    public MainForm()
    {
        InitializeComponent();

        // Safe, side-effect-free — always runs, including at design time. Everything
        // else (images loaded from disk, MQTT, the database, the keyboard hook, data
        // binding) is deliberately NOT here — it's in OnLoad below instead. Checking
        // DesignMode in the constructor itself is unreliable (the Designer hasn't
        // finished "siting" the control yet at this point), which is exactly the
        // problem we hit: the old "if (IsDesignTime) return;" check in this
        // constructor never actually evaluated true, even when genuinely running
        // inside the Designer, so the image-loading code below used to run anyway and
        // throw (relative paths don't resolve the same way in the Designer's own
        // process), aborting the constructor before the grid ever got its columns.
        // DesignMode becomes reliable once we're in OnLoad instead, because by then
        // the Designer has finished siting the control.
        ConfigureEventsGridColumns();
        eventsGrid.AutoGenerateColumns = false;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        TopMost = true;

        var bmp = new Bitmap(@"Icons\Events.png");
        bmp.MakeTransparent(Color.White);
        btEvents.Image = bmp;
        btEvents.FlatStyle = FlatStyle.Flat;
        btEvents.FlatAppearance.BorderSize = 0;
        btEvents.BackColor = Color.Transparent;
        btEvents.Text = "\nEvents";
        btEvents.FlatAppearance.MouseOverBackColor = Color.FromArgb(210, 209, 198);
        btEvents.FlatAppearance.MouseDownBackColor = Color.FromArgb(190, 189, 178);

        bmp = new Bitmap(@"Icons\History.png");
        bmp.MakeTransparent(Color.White);
        btHistory.Image = bmp;
        btHistory.FlatStyle = FlatStyle.Flat;
        btHistory.FlatAppearance.BorderSize = 0;
        btHistory.BackColor = Color.Transparent;
        btHistory.Text = "\nHistory";
        btHistory.FlatAppearance.MouseOverBackColor = Color.FromArgb(210, 209, 198);
        btHistory.FlatAppearance.MouseDownBackColor = Color.FromArgb(190, 189, 178);

        bmp = new Bitmap(@"Icons\Back.png");
        bmp.MakeTransparent(Color.White);
        btBack.Image = bmp;
        btBack.FlatStyle = FlatStyle.Flat;
        btBack.FlatAppearance.BorderSize = 0;
        btBack.BackColor = Color.Transparent;
        btBack.Text = "\nBack";
        btBack.FlatAppearance.MouseOverBackColor = Color.FromArgb(210, 209, 198);
        btBack.FlatAppearance.MouseDownBackColor = Color.FromArgb(190, 189, 178);
        btBack.Click += (_, _) => ShowEventsPage(); // only meaningful action it has right now: leave the History page
        btEvents.Click += (_, _) => ShowEventsPage();

        bmp = new Bitmap(@"Icons\Forward.png");
        bmp.MakeTransparent(Color.White);
        btForward.Image = bmp;
        btForward.FlatStyle = FlatStyle.Flat;
        btForward.FlatAppearance.BorderSize = 0;
        btForward.BackColor = Color.Transparent;
        btForward.Text = "\nForward";
        btForward.FlatAppearance.MouseOverBackColor = Color.FromArgb(210, 209, 198);
        btForward.FlatAppearance.MouseDownBackColor = Color.FromArgb(190, 189, 178);

        // Force the Dock stacking order every time, regardless of whatever order
        // a Designer resave happened to leave the Controls.Add(...) calls in:
        // eventsGrid (Fill) has to be "furthest back" so it fills whatever space
        // is left over; pnlToolbar and pnlStatus dock outward from the top, with
        // pnlStatus outermost (drawn at the very top), and pnlFooter docks
        // outward from the bottom, at the very bottom edge. If that relative
        // order ever gets scrambled, one of the docked panels can end up
        // rendered over the grid's edge — e.g. hiding its column header row —
        // which is what editing an unrelated control and triggering a Designer
        // resave can do.
        BuildHistoryPage();

        Controls.SetChildIndex(eventsGrid, 0);
        Controls.SetChildIndex(pnlHistoryPage, 1);
        Controls.SetChildIndex(pnlToolbar, 2);
        Controls.SetChildIndex(pnlStatus, 3);
        Controls.SetChildIndex(pnlFooter, 4);

        InstallLowLevelKeyboardHook();

        eventsGrid.DataSource = _visibleEvents;
        eventsGrid.CellFormatting += EventsGrid_CellFormatting;

        txtFilter.TextChanged += (_, _) => ApplyFilter();

        // Open (and, on first run, create) the events database, then reload whatever
        // was still "not accepted" from the last run — so Current Events survives an
        // app restart instead of starting empty every time.
        _repo = new EventsRepository { EncryptionKey = EventsDbEncryptionKey };
        _repo.Initialize();
        _allEvents.AddRange(_repo.GetCurrentEvents());

        // _mqtt has to exist before the first ApplyFilter()/UpdateStatusBar() call
        // below, since UpdateStatusBar reads _mqtt.Panels/_mqtt.IsConnected.
        _mqtt = new MqttEventsClient(MqttSettings.Load());
        _mqtt.StateChanged += OnMqttStateChanged;
        _mqtt.EventReceived += OnMqttEventReceived;

        ApplyFilter();

        _mqtt.Start();
    }

    // Builds and adds eventsGrid's six columns. To add, remove, reorder, rename, or
    // resize a column, edit it here — not in MainForm.Designer.cs or the visual
    // designer's "Edit Columns…" dialog, since that section of the Designer file no
    // longer defines any columns at all (see the comment on eventsGrid in
    // MainForm.Designer.cs). DataPropertyName has to match a real public property on
    // DraxEventRow (Models/DraxEventRow.cs) or the column shows blank.
    private void ConfigureEventsGridColumns()
    {
        colDate.DataPropertyName = "Date";
        colDate.HeaderText = "Date";
        colDate.Name = "colDate";
        colDate.Width = 90;

        colTime.DataPropertyName = "Time";
        colTime.HeaderText = "Time";
        colTime.Name = "colTime";
        colTime.Width = 90;

        colType.DataPropertyName = "Type";
        colType.HeaderText = "Type";
        colType.Name = "colType";
        colType.Width = 120;

        colAmxRef.DataPropertyName = "AmxRef";
        colAmxRef.HeaderText = "AMX ref";
        colAmxRef.Name = "colAmxRef";
        colAmxRef.Width = 110;

        colInputType.DataPropertyName = "InputType";
        colInputType.HeaderText = "Input Type";
        colInputType.Name = "colInputType";
        colInputType.Width = 80;

        colText.DataPropertyName = "CombinedText";
        colText.HeaderText = "Text";
        colText.Name = "colText";
        colText.Width = 320;
        colText.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        eventsGrid.Columns.AddRange(colDate, colTime, colType, colAmxRef, colInputType, colText);
    }

    // Builds the History page's controls and adds them to the form, hidden
    // (pnlHistoryPage starts Visible = false) until btHistory shows it. Same column
    // layout as eventsGrid (Models/DraxEventRow.cs), and built in code for the same
    // reason — see ConfigureEventsGridColumns above.
    private void BuildHistoryPage()
    {
        hColDate.DataPropertyName = "Date"; hColDate.HeaderText = "Date"; hColDate.Name = "hColDate"; hColDate.Width = 90;
        hColTime.DataPropertyName = "Time"; hColTime.HeaderText = "Time"; hColTime.Name = "hColTime"; hColTime.Width = 90;
        hColType.DataPropertyName = "Type"; hColType.HeaderText = "Type"; hColType.Name = "hColType"; hColType.Width = 120;
        hColAmxRef.DataPropertyName = "AmxRef"; hColAmxRef.HeaderText = "AMX ref"; hColAmxRef.Name = "hColAmxRef"; hColAmxRef.Width = 110;
        hColInputType.DataPropertyName = "InputType"; hColInputType.HeaderText = "Input Type"; hColInputType.Name = "hColInputType"; hColInputType.Width = 80;
        hColText.DataPropertyName = "CombinedText"; hColText.HeaderText = "Text"; hColText.Name = "hColText"; hColText.Width = 320; hColText.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        historyGrid.Columns.AddRange(hColDate, hColTime, hColType, hColAmxRef, hColInputType, hColText);
        historyGrid.DataSource = _historyRows;

        btHistorySearch.Click += (_, _) => RunHistorySearch();

        pnlHistoryFilter.Controls.Add(lblHistoryFrom);
        pnlHistoryFilter.Controls.Add(dtHistoryFrom);
        pnlHistoryFilter.Controls.Add(lblHistoryTo);
        pnlHistoryFilter.Controls.Add(dtHistoryTo);
        pnlHistoryFilter.Controls.Add(btHistorySearch);
        pnlHistoryFilter.Controls.Add(lblHistoryCount);

        pnlHistoryPage.Controls.Add(historyGrid);
        pnlHistoryPage.Controls.Add(pnlHistoryFilter);
        Controls.Add(pnlHistoryPage);
    }

    private void ShowHistoryPage()
    {
        eventsGrid.Visible = false;
        pnlHistoryPage.Visible = true;
        pnlHistoryPage.BringToFront();
        RunHistorySearch();
    }

    private void ShowEventsPage()
    {
        pnlHistoryPage.Visible = false;
        eventsGrid.Visible = true;
        eventsGrid.BringToFront();
    }

    private void RunHistorySearch()
    {
        // dtHistoryFrom/dtHistoryTo are date-only pickers, so push "To" to the very
        // end of that day — otherwise picking today as "To" would exclude everything
        // accepted today.
        var from = dtHistoryFrom.Value.Date;
        var to = dtHistoryTo.Value.Date.AddDays(1).AddTicks(-1);

        _historyRows.RaiseListChangedEvents = false;
        _historyRows.Clear();
        foreach (var row in _repo.GetHistory(from, to)) _historyRows.Add(row);
        _historyRows.RaiseListChangedEvents = true;
        _historyRows.ResetBindings();

        lblHistoryCount.Text = _historyRows.Count == 1 ? "1 event" : $"{_historyRows.Count} events";
    }

    // --- MQTT callbacks: these fire on a background thread (MQTTnet's own),
    //     so every one of them has to marshal back onto the UI thread before
    //     touching a control — that's what the InvokeRequired check is for. ---

    private void OnMqttStateChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(OnMqttStateChanged)); } catch (ObjectDisposedException) { }
            return;
        }

        UpdateStatusBar();
    }

    private void OnMqttEventReceived(DraxEventRow row)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => OnMqttEventReceived(row))); } catch (ObjectDisposedException) { }
            return;
        }

        if (row.On)
        {
            // Same physical point already showing as active (same AMX ref — Panel +
            // Node + Loop + Input — and Input Type) — drop it rather than adding a
            // second row, so a panel re-announcing the same active point doesn't
            // pile up repeats in the list.
            if (!IsDuplicateActiveRow(row))
            {
                // Recorded as "not accepted" the moment it arrives, so it's on Current
                // Events (and survives a restart) regardless of whether an operator
                // ever actually looks at it.
                _repo.Insert(row); // stamps row.Id with the new database row's id
                _allEvents.Insert(0, row); // newest first, same as DraxView's EventStore
                const int maxRetained = 1000; // matches DraxView's EventOptions.MaxRetained default
                while (_allEvents.Count > maxRetained) _allEvents.RemoveAt(_allEvents.Count - 1);
            }
        }
        else
        {
            // An "off" (cleared) event doesn't get its own row — it removes whichever
            // "on" row raised this same point, the way a panel's current event list
            // drops the entry once it clears rather than adding a second "cleared" line.
            // The point clearing itself is what moves it to History here (per the
            // "auto-moves to History" behaviour) — no operator click required — so the
            // matching database row is marked Accepted at the same time.
            _repo.AcceptMatching(row);
            RemoveMatchingOnRow(row);
        }

        ApplyFilter();
    }

    // Same point/event identity as AmxRef plus Type: Panel + Node + Loop + Input
    // narrows down the physical point, and Type distinguishes an Alarm clearing
    // from, say, a Fault clearing on that same point.
    private void RemoveMatchingOnRow(DraxEventRow offRow)
    {
        var match = _allEvents.FirstOrDefault(e =>
            e.On &&
            e.Panel.Equals(offRow.Panel, StringComparison.OrdinalIgnoreCase) &&
            e.Type.Equals(offRow.Type, StringComparison.OrdinalIgnoreCase) &&
            e.Node == offRow.Node &&
            e.Loop == offRow.Loop &&
            e.Input == offRow.Input);

        if (match != null) _allEvents.Remove(match);
    }

    // Deliberately narrower than RemoveMatchingOnRow above: only AMX ref (Node +
    // Loop + Input, scoped to the same Panel) plus Input Type — not Type — so a
    // repeat "on" for the same physical point is treated as a duplicate even if
    // you want a different Type to still show as its own row, say so and Type
    // can be added to this match too.
    private bool IsDuplicateActiveRow(DraxEventRow newRow)
    {
        return _allEvents.Any(e =>
            e.On &&
            e.Panel.Equals(newRow.Panel, StringComparison.OrdinalIgnoreCase) &&
            e.Node == newRow.Node &&
            e.Loop == newRow.Loop &&
            e.Input == newRow.Input &&
            e.InputType == newRow.InputType);
    }

    private void ApplyFilter()
    {
        IEnumerable<DraxEventRow> rows = _allEvents.OrderByDescending(e => e.ReceivedLocal);

        var filter = txtFilter.Text.Trim();
        if (!string.IsNullOrEmpty(filter))
        {
            rows = rows.Where(e =>
                e.Panel.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.Type.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.Text.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.Text2.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.AmxRef.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        _visibleEvents.RaiseListChangedEvents = false;
        _visibleEvents.Clear();
        foreach (var row in rows) _visibleEvents.Add(row);
        _visibleEvents.RaiseListChangedEvents = true;
        _visibleEvents.ResetBindings();

        UpdateStatusBar();
    }

    private void UpdateStatusBar()
    {
        var activeCount = _allEvents.Count(e => e.On && e.Family == "alarm");
        var panels = _mqtt.Panels;

        if (activeCount > 1)
        {
            lblActive.Text = $"{activeCount} Events";
        }
        else
        {
            lblActive.Text = $"{activeCount} Event";
        }
    }

    private void EventsGrid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _visibleEvents.Count) return;
        var row = _visibleEvents[e.RowIndex];

        e.CellStyle!.ForeColor = row.Family switch
        {
            "alarm" => Color.Firebrick,
            "fault" => Color.DarkOrange,
            "isolation" => Color.DarkGoldenrod,
            "control" => Color.SteelBlue,
            _ => SystemColors.ControlText,
        };

        if (row.On && (row.Family == "alarm" || row.Family == "fault"))
        {
            e.CellStyle.Font = new Font(eventsGrid.Font, FontStyle.Bold);
        }
    }

    protected override async void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        UninstallLowLevelKeyboardHook();
        if (_mqtt != null) await _mqtt.DisposeAsync();
        _repo?.Dispose();
    }

    private void helpToolStripMenuItem_Click(object sender, EventArgs e)
    {

    }

    private void eventsGrid_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;

        var hit = eventsGrid.HitTest(e.X, e.Y);

        System.Diagnostics.Debug.WriteLine(
            $"e=({e.X},{e.Y}) gridBounds={eventsGrid.ClientRectangle} " +
            $"hitType={hit.Type} row={hit.RowIndex} " +
            $"headerHeight={eventsGrid.ColumnHeadersHeight} rowCount={eventsGrid.RowCount} " +
            $"row0Bounds={(eventsGrid.RowCount > 0 ? eventsGrid.GetRowDisplayRectangle(0, false).ToString() : "n/a")}");

        if (hit.Type == DataGridViewHitTestType.Cell && hit.RowIndex >= 0)
        {
            eventsGrid.ClearSelection();
            eventsGrid.Rows[hit.RowIndex].Selected = true;
            eventsGrid.CurrentCell = eventsGrid.Rows[hit.RowIndex].Cells[Math.Max(hit.ColumnIndex, 0)];
            eventsGrid.ContextMenuStrip = contextMenuStrip1; // only now does Windows have a menu to show
        }
        else
        {
            eventsGrid.ContextMenuStrip = null; // header, empty space below last row, etc. — no menu
        }
    }

    private void eventsGrid_CellContextMenuStripNeeded(object sender, DataGridViewCellContextMenuStripNeededEventArgs e)
    {
        if (e.RowIndex >= 0)
        {
            eventsGrid.ClearSelection();
            eventsGrid.Rows[e.RowIndex].Selected = true;
            eventsGrid.CurrentCell = eventsGrid.Rows[e.RowIndex].Cells[Math.Max(e.ColumnIndex, 0)];
            e.ContextMenuStrip = contextMenuStrip1;
        }
        // e.RowIndex < 0 (header, or empty area below the last row) — leave
        // e.ContextMenuStrip unset, so no menu shows there at all.
    }

    private void acceptToolStripMenuItem_Click(object sender, EventArgs e)
    {
        if (eventsGrid.CurrentRow?.DataBoundItem is not DraxEventRow row) return;

        // Manual "Accept the Alarm": mark it accepted in the database (so it's on
        // History from now on) and drop it off Current Events immediately.
        _repo.Accept(row.Id);
        _allEvents.Remove(row);
        ApplyFilter();
    }

    private void clearTheAlarmToolStripMenuItem_Click(object sender, EventArgs e)
    {
        if (eventsGrid.CurrentRow?.DataBoundItem is not DraxEventRow row) return;

        // POC-level "Clear the Alarm" — no real MQTT command is sent to the panel.
        // It also marks the row accepted in the database (same as the manual Accept
        // action above) rather than just removing it from the in-memory list, so it
        // doesn't come back as a dangling Current Event the next time the app starts,
        // and still turns up on History the same way an accepted alarm would.
        _repo.Accept(row.Id);
        _allEvents.Remove(row);
        ApplyFilter();
    }

    private void btHistory_Click(object sender, EventArgs e) => ShowHistoryPage();

    private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
    {
        System.Windows.Forms.MessageBox.Show("AMX c# 64 bit native form", "About");
    }
}
