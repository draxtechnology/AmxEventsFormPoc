namespace AmxEventsFormPoc;

partial class HistoryForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        this.pnlFilter = new System.Windows.Forms.Panel();
        this.lblCount = new System.Windows.Forms.Label();
        this.btSearch = new System.Windows.Forms.Button();
        this.dtTo = new System.Windows.Forms.DateTimePicker();
        this.lblTo = new System.Windows.Forms.Label();
        this.dtFrom = new System.Windows.Forms.DateTimePicker();
        this.lblFrom = new System.Windows.Forms.Label();
        this.historyGrid = new System.Windows.Forms.DataGridView();
        this.pnlFilter.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)(this.historyGrid)).BeginInit();
        this.SuspendLayout();
        //
        // pnlFilter
        //
        this.pnlFilter.Controls.Add(this.lblCount);
        this.pnlFilter.Controls.Add(this.btSearch);
        this.pnlFilter.Controls.Add(this.dtTo);
        this.pnlFilter.Controls.Add(this.lblTo);
        this.pnlFilter.Controls.Add(this.dtFrom);
        this.pnlFilter.Controls.Add(this.lblFrom);
        this.pnlFilter.Dock = System.Windows.Forms.DockStyle.Top;
        this.pnlFilter.Location = new System.Drawing.Point(0, 0);
        this.pnlFilter.Name = "pnlFilter";
        this.pnlFilter.Padding = new System.Windows.Forms.Padding(10);
        this.pnlFilter.Size = new System.Drawing.Size(1000, 50);
        this.pnlFilter.TabIndex = 0;
        //
        // lblFrom
        //
        this.lblFrom.AutoSize = true;
        this.lblFrom.Location = new System.Drawing.Point(13, 17);
        this.lblFrom.Name = "lblFrom";
        this.lblFrom.Size = new System.Drawing.Size(34, 15);
        this.lblFrom.TabIndex = 0;
        this.lblFrom.Text = "From";
        //
        // dtFrom
        //
        this.dtFrom.Format = System.Windows.Forms.DateTimePickerFormat.Short;
        this.dtFrom.Location = new System.Drawing.Point(53, 13);
        this.dtFrom.Name = "dtFrom";
        this.dtFrom.Size = new System.Drawing.Size(120, 23);
        this.dtFrom.TabIndex = 1;
        this.dtFrom.Value = System.DateTime.Today.AddDays(-7);
        //
        // lblTo
        //
        this.lblTo.AutoSize = true;
        this.lblTo.Location = new System.Drawing.Point(189, 17);
        this.lblTo.Name = "lblTo";
        this.lblTo.Size = new System.Drawing.Size(18, 15);
        this.lblTo.TabIndex = 2;
        this.lblTo.Text = "To";
        //
        // dtTo
        //
        this.dtTo.Format = System.Windows.Forms.DateTimePickerFormat.Short;
        this.dtTo.Location = new System.Drawing.Point(213, 13);
        this.dtTo.Name = "dtTo";
        this.dtTo.Size = new System.Drawing.Size(120, 23);
        this.dtTo.TabIndex = 3;
        this.dtTo.Value = System.DateTime.Today;
        //
        // btSearch
        //
        this.btSearch.Location = new System.Drawing.Point(349, 12);
        this.btSearch.Name = "btSearch";
        this.btSearch.Size = new System.Drawing.Size(90, 25);
        this.btSearch.TabIndex = 4;
        this.btSearch.Text = "Search";
        this.btSearch.UseVisualStyleBackColor = true;
        this.btSearch.Click += new System.EventHandler(this.btSearch_Click);
        //
        // lblCount
        //
        this.lblCount.AutoSize = true;
        this.lblCount.Location = new System.Drawing.Point(455, 17);
        this.lblCount.Name = "lblCount";
        this.lblCount.Size = new System.Drawing.Size(46, 15);
        this.lblCount.TabIndex = 5;
        this.lblCount.Text = "0 events";
        //
        // historyGrid
        //
        this.historyGrid.AllowUserToAddRows = false;
        this.historyGrid.AllowUserToDeleteRows = false;
        this.historyGrid.AutoGenerateColumns = false;
        this.historyGrid.Dock = System.Windows.Forms.DockStyle.Fill;
        this.historyGrid.Location = new System.Drawing.Point(0, 50);
        this.historyGrid.Name = "historyGrid";
        this.historyGrid.ReadOnly = true;
        this.historyGrid.RowHeadersWidth = 25;
        this.historyGrid.Size = new System.Drawing.Size(1000, 550);
        this.historyGrid.TabIndex = 1;
        //
        // HistoryForm
        //
        this.ClientSize = new System.Drawing.Size(1000, 600);
        this.Controls.Add(this.historyGrid);
        this.Controls.Add(this.pnlFilter);
        this.MinimumSize = new System.Drawing.Size(650, 300);
        this.Name = "HistoryForm";
        this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
        this.Text = "AMX Events - History";
        this.pnlFilter.ResumeLayout(false);
        this.pnlFilter.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)(this.historyGrid)).EndInit();
        this.ResumeLayout(false);
    }

    private System.Windows.Forms.Panel pnlFilter;
    private System.Windows.Forms.Label lblFrom;
    private System.Windows.Forms.DateTimePicker dtFrom;
    private System.Windows.Forms.Label lblTo;
    private System.Windows.Forms.DateTimePicker dtTo;
    private System.Windows.Forms.Button btSearch;
    private System.Windows.Forms.Label lblCount;
    private System.Windows.Forms.DataGridView historyGrid;
}
