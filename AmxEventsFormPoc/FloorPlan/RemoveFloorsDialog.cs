using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AmxEventsFormPoc.FloorPlan;

// Plan > Remove floors...: every floor, grouped by the file it comes from, with the
// number of devices placed on it.  Ticking a file ticks all its floors (the seven
// from one DWG sheet, a PDF set a site has replaced).  Returns the floors ticked;
// the page confirms with the totals before removing anything.
public sealed class RemoveFloorsDialog : Form
{
    private readonly TreeView _tree = new NoDoubleClickTreeView { Dock = DockStyle.Fill, CheckBoxes = true, ShowLines = true, FullRowSelect = true };
    private readonly CheckBox _all = new() { Text = "Select all", AutoSize = true, Dock = DockStyle.Left, Padding = new Padding(8, 0, 0, 0) };
    private readonly Label _summary = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray };
    private readonly Button _remove = new() { Text = "Remove...", Dock = DockStyle.Fill, Enabled = false };
    private readonly Dictionary<long, int> _devices;
    private bool _updating;

    public List<PlanFloor> Selected { get; } = new();

    // selectAll: open with every floor ticked (Plan > Remove all floors...), still
    // showing exactly what will go before anything is removed.
    public RemoveFloorsDialog(IReadOnlyList<PlanFloor> floors, IReadOnlyDictionary<long, int> devicesPerFloor, bool selectAll = false)
    {
        _devices = new Dictionary<long, int>(devicesPerFloor);
        Text = "Remove floors";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(560, 560);
        MinimumSize = new Size(420, 320);
        TopMost = true;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 9f);

        var intro = new Label
        {
            Dock = DockStyle.Top, Height = 44, Padding = new Padding(10, 8, 10, 0),
            Text = "Tick the floors to remove.  Devices placed on them go back to the device list as not yet placed.  The drawing files themselves aren’t touched.",
        };

        foreach (var group in floors.GroupBy(f => f.DwgPath, StringComparer.OrdinalIgnoreCase))
        {
            var fileNode = new TreeNode(Path.GetFileName(group.Key)) { Tag = null };
            foreach (var f in group.OrderBy(f => f.Page).ThenBy(f => f.Id))
            {
                int n = _devices.TryGetValue(f.Id, out var c) ? c : 0;
                fileNode.Nodes.Add(new TreeNode($"{f.Name}   ({n} device{(n == 1 ? "" : "s")} placed)") { Tag = f });
            }
            fileNode.Text += group.Count() > 1 ? $"   ({group.Count()} floors)" : "";
            _tree.Nodes.Add(fileNode);
        }
        _tree.ExpandAll();
        _tree.AfterCheck += OnAfterCheck;
        _all.CheckedChanged += (_, _) =>
        {
            if (_updating) return;
            _updating = true;
            foreach (TreeNode file in _tree.Nodes)
            {
                file.Checked = _all.Checked;
                foreach (TreeNode floor in file.Nodes) floor.Checked = _all.Checked;
            }
            _updating = false;
            UpdateSummary();
        };

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 46, ColumnCount = 4, Padding = new Padding(4, 6, 10, 6) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        var cancel = new Button { Text = "Cancel", Dock = DockStyle.Fill, DialogResult = DialogResult.Cancel };
        // Re-read the ticks at the moment Remove is pressed, rather than trusting the
        // list kept up by AfterCheck.
        _remove.Click += (_, _) =>
        {
            UpdateSummary();
            if (Selected.Count == 0) return;
            DialogResult = DialogResult.OK;
            Close();
        };
        bottom.Controls.Add(_all, 0, 0);
        bottom.Controls.Add(_summary, 1, 0);
        bottom.Controls.Add(_remove, 2, 0);
        bottom.Controls.Add(cancel, 3, 0);
        CancelButton = cancel;

        Controls.Add(_tree);
        Controls.Add(bottom);
        Controls.Add(intro);
        if (selectAll)
        {
            Text = "Remove all floors";
            _all.Checked = true; // ticks every floor through the handler above
        }
        UpdateSummary();
    }

    // A file's tick sets all its floors; a floor's tick updates its file (ticked when
    // every floor of it is).
    private void OnAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_updating || e.Node == null) return;
        _updating = true;
        if (e.Node.Tag == null)
        {
            foreach (TreeNode floor in e.Node.Nodes) floor.Checked = e.Node.Checked;
        }
        else if (e.Node.Parent != null)
        {
            e.Node.Parent.Checked = e.Node.Parent.Nodes.Cast<TreeNode>().All(n => n.Checked);
        }
        _all.Checked = _tree.Nodes.Cast<TreeNode>().SelectMany(f => f.Nodes.Cast<TreeNode>()).All(n => n.Checked);
        _updating = false;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        Selected.Clear();
        foreach (TreeNode file in _tree.Nodes)
            foreach (TreeNode floor in file.Nodes)
                if (floor.Checked && floor.Tag is PlanFloor f) Selected.Add(f);
        int devices = Selected.Sum(f => _devices.TryGetValue(f.Id, out var n) ? n : 0);
        _summary.Text = Selected.Count == 0 ? "" : $"{Selected.Count} floor{(Selected.Count == 1 ? "" : "s")}, {devices} placed device{(devices == 1 ? "" : "s")}";
        _remove.Enabled = Selected.Count > 0;
    }

    // A WinForms TreeView with check boxes toggles a box on double-click without
    // raising AfterCheck for the second toggle, so what's shown and what's selected
    // can disagree.  Swallow the double-click; single clicks work as normal.
    private sealed class NoDoubleClickTreeView : TreeView
    {
        protected override void WndProc(ref Message m)
        {
            const int WM_LBUTTONDBLCLK = 0x0203;
            if (m.Msg == WM_LBUTTONDBLCLK) m.Msg = 0x0201; // treat as a single click
            base.WndProc(ref m);
        }
    }
}
