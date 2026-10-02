using System;
using System.ComponentModel;
using System.Windows.Forms;
using AmxEventsFormPoc.Models;
using AmxEventsFormPoc.Services;

namespace AmxEventsFormPoc;

// Separate History form (per the "new separate form" choice) showing accepted events
// — either accepted manually via "Accept the Alarm"/"Clear the Alarm", or
// automatically when the panel reported the point cleared — filtered by date range.
public partial class HistoryForm : Form
{
    private readonly EventsRepository _repo;
    private readonly BindingList<DraxEventRow> _rows = new();

    private readonly DataGridViewTextBoxColumn colDate = new();
    private readonly DataGridViewTextBoxColumn colTime = new();
    private readonly DataGridViewTextBoxColumn colType = new();
    private readonly DataGridViewTextBoxColumn colAmxRef = new();
    private readonly DataGridViewTextBoxColumn colInputType = new();
    private readonly DataGridViewTextBoxColumn colText = new();

    public HistoryForm(EventsRepository repo)
    {
        _repo = repo;
        InitializeComponent();

        ConfigureColumns();
        historyGrid.AutoGenerateColumns = false;
        historyGrid.DataSource = _rows;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RunSearch();
    }

    // Same column layout as MainForm's eventsGrid (Models/DraxEventRow.cs), built in
    // code for the same reason — see ConfigureEventsGridColumns in MainForm.cs.
    private void ConfigureColumns()
    {
        colDate.DataPropertyName = "Date"; colDate.HeaderText = "Date"; colDate.Name = "colDate"; colDate.Width = 90;
        colTime.DataPropertyName = "Time"; colTime.HeaderText = "Time"; colTime.Name = "colTime"; colTime.Width = 90;
        colType.DataPropertyName = "Type"; colType.HeaderText = "Type"; colType.Name = "colType"; colType.Width = 120;
        colAmxRef.DataPropertyName = "AmxRef"; colAmxRef.HeaderText = "AMX ref"; colAmxRef.Name = "colAmxRef"; colAmxRef.Width = 110;
        colInputType.DataPropertyName = "InputType"; colInputType.HeaderText = "Input Type"; colInputType.Name = "colInputType"; colInputType.Width = 80;
        colText.DataPropertyName = "CombinedText"; colText.HeaderText = "Text"; colText.Name = "colText"; colText.Width = 320; colText.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        historyGrid.Columns.AddRange(colDate, colTime, colType, colAmxRef, colInputType, colText);
    }

    private void btSearch_Click(object sender, EventArgs e) => RunSearch();

    private void RunSearch()
    {
        // dtFrom/dtTo are date-only pickers, so push "To" to the very end of that day
        // — otherwise picking today as "To" would exclude everything accepted today.
        var from = dtFrom.Value.Date;
        var to = dtTo.Value.Date.AddDays(1).AddTicks(-1);

        _rows.RaiseListChangedEvents = false;
        _rows.Clear();
        foreach (var row in _repo.GetHistory(from, to)) _rows.Add(row);
        _rows.RaiseListChangedEvents = true;
        _rows.ResetBindings();

        lblCount.Text = _rows.Count == 1 ? "1 event" : $"{_rows.Count} events";
    }
}
