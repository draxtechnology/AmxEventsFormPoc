using System.Drawing;
using System.Windows.Forms;

namespace AmxEventsFormPoc;

partial class MainForm
{
    /// <summary>
    /// Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer? components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null)
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
        pnlStatus = new FlowLayoutPanel();
        lblBroker = new Label();
        btBack = new Button();
        btForward = new Button();
        btEvents = new Button();
        btHistory = new Button();
        lblPanels = new Label();
        lblMeta = new Label();
        menuStrip1 = new MenuStrip();
        mainMenuToolStripMenuItem = new ToolStripMenuItem();
        logOffToolStripMenuItem = new ToolStripMenuItem();
        smartToolStripMenuItem = new ToolStripMenuItem();
        serviceReportsToolStripMenuItem = new ToolStripMenuItem();
        contractCompletionReportToolStripMenuItem = new ToolStripMenuItem();
        managementToolStripMenuItem = new ToolStripMenuItem();
        engineerToolStripMenuItem = new ToolStripMenuItem();
        windowToolStripMenuItem = new ToolStripMenuItem();
        helpToolStripMenuItem = new ToolStripMenuItem();
        aboutToolStripMenuItem = new ToolStripMenuItem();
        pnlToolbar = new FlowLayoutPanel();
        txtFilter = new TextBox();
        pnlFooter = new FlowLayoutPanel();
        lblActive = new Label();
        eventsGrid = new DataGridView();
        contextMenuStrip1 = new ContextMenuStrip(components);
        acceptToolStripMenuItem = new ToolStripMenuItem();
        clearTheAlarmToolStripMenuItem = new ToolStripMenuItem();
        pnlStatus.SuspendLayout();
        menuStrip1.SuspendLayout();
        pnlToolbar.SuspendLayout();
        pnlFooter.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)eventsGrid).BeginInit();
        contextMenuStrip1.SuspendLayout();
        SuspendLayout();
        // 
        // pnlStatus
        // 
        pnlStatus.BackColor = Color.FromArgb(228, 227, 216);
        pnlStatus.Controls.Add(lblBroker);
        pnlStatus.Controls.Add(btBack);
        pnlStatus.Controls.Add(btForward);
        pnlStatus.Controls.Add(btEvents);
        pnlStatus.Controls.Add(btHistory);
        pnlStatus.Controls.Add(lblPanels);
        pnlStatus.Controls.Add(lblMeta);
        pnlStatus.Dock = DockStyle.Top;
        pnlStatus.Location = new Point(0, 24);
        pnlStatus.Name = "pnlStatus";
        pnlStatus.Padding = new Padding(8, 6, 8, 6);
        pnlStatus.Size = new Size(1500, 66);
        pnlStatus.TabIndex = 2;
        pnlStatus.WrapContents = false;
        // 
        // lblBroker
        // 
        lblBroker.AutoSize = true;
        lblBroker.ForeColor = Color.FromArgb(200, 90, 90);
        lblBroker.Location = new Point(8, 10);
        lblBroker.Margin = new Padding(0, 4, 24, 0);
        lblBroker.Name = "lblBroker";
        lblBroker.Size = new Size(0, 15);
        lblBroker.TabIndex = 0;
        // 
        // btBack
        // 
        btBack.Font = new Font("Segoe UI", 7F);
        btBack.Image = (Image)resources.GetObject("btBack.Image");
        btBack.Location = new Point(32, 6);
        btBack.Margin = new Padding(0);
        btBack.Name = "btBack";
        btBack.Size = new Size(50, 60);
        btBack.TabIndex = 5;
        btBack.Text = "Back";
        btBack.TextAlign = ContentAlignment.BottomCenter;
        btBack.TextImageRelation = TextImageRelation.ImageAboveText;
        btBack.UseVisualStyleBackColor = true;
        // 
        // btForward
        // 
        btForward.Font = new Font("Segoe UI", 7F);
        btForward.Image = (Image)resources.GetObject("btForward.Image");
        btForward.Location = new Point(82, 6);
        btForward.Margin = new Padding(0);
        btForward.Name = "btForward";
        btForward.Size = new Size(50, 60);
        btForward.TabIndex = 6;
        btForward.Text = "Forward";
        btForward.TextAlign = ContentAlignment.BottomCenter;
        btForward.TextImageRelation = TextImageRelation.ImageAboveText;
        btForward.UseVisualStyleBackColor = true;
        // 
        // btEvents
        // 
        btEvents.Font = new Font("Segoe UI", 7F);
        btEvents.Image = (Image)resources.GetObject("btEvents.Image");
        btEvents.Location = new Point(132, 6);
        btEvents.Margin = new Padding(0);
        btEvents.Name = "btEvents";
        btEvents.Size = new Size(50, 60);
        btEvents.TabIndex = 3;
        btEvents.Text = "Events";
        btEvents.TextAlign = ContentAlignment.BottomCenter;
        btEvents.TextImageRelation = TextImageRelation.ImageAboveText;
        btEvents.UseVisualStyleBackColor = true;
        // 
        // btHistory
        // 
        btHistory.Font = new Font("Segoe UI", 7F);
        btHistory.Image = (Image)resources.GetObject("btHistory.Image");
        btHistory.Location = new Point(182, 6);
        btHistory.Margin = new Padding(0);
        btHistory.Name = "btHistory";
        btHistory.Size = new Size(50, 60);
        btHistory.TabIndex = 4;
        btHistory.Text = "History";
        btHistory.TextAlign = ContentAlignment.BottomCenter;
        btHistory.TextImageRelation = TextImageRelation.ImageAboveText;
        btHistory.UseVisualStyleBackColor = true;
        btHistory.Click += btHistory_Click;
        // 
        // lblPanels
        // 
        lblPanels.AutoSize = true;
        lblPanels.ForeColor = Color.FromArgb(90, 190, 120);
        lblPanels.Location = new Point(232, 10);
        lblPanels.Margin = new Padding(0, 4, 24, 0);
        lblPanels.Name = "lblPanels";
        lblPanels.Size = new Size(0, 15);
        lblPanels.TabIndex = 1;
        // 
        // lblMeta
        // 
        lblMeta.AutoSize = true;
        lblMeta.ForeColor = Color.Gainsboro;
        lblMeta.Location = new Point(256, 10);
        lblMeta.Margin = new Padding(0, 4, 0, 0);
        lblMeta.Name = "lblMeta";
        lblMeta.Size = new Size(0, 15);
        lblMeta.TabIndex = 2;
        // 
        // menuStrip1
        // 
        menuStrip1.Items.AddRange(new ToolStripItem[] { mainMenuToolStripMenuItem, smartToolStripMenuItem, managementToolStripMenuItem, engineerToolStripMenuItem, windowToolStripMenuItem, helpToolStripMenuItem });
        menuStrip1.Location = new Point(0, 0);
        menuStrip1.Name = "menuStrip1";
        menuStrip1.Size = new Size(1500, 24);
        menuStrip1.TabIndex = 3;
        menuStrip1.Text = "menuStrip1";
        // 
        // mainMenuToolStripMenuItem
        // 
        mainMenuToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { logOffToolStripMenuItem });
        mainMenuToolStripMenuItem.Name = "mainMenuToolStripMenuItem";
        mainMenuToolStripMenuItem.Size = new Size(80, 20);
        mainMenuToolStripMenuItem.Text = "&Main Menu";
        // 
        // logOffToolStripMenuItem
        // 
        logOffToolStripMenuItem.Name = "logOffToolStripMenuItem";
        logOffToolStripMenuItem.Size = new Size(114, 22);
        logOffToolStripMenuItem.Text = "Log Off";
        // 
        // smartToolStripMenuItem
        // 
        smartToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { serviceReportsToolStripMenuItem });
        smartToolStripMenuItem.Name = "smartToolStripMenuItem";
        smartToolStripMenuItem.Size = new Size(50, 20);
        smartToolStripMenuItem.Text = "Smart";
        // 
        // serviceReportsToolStripMenuItem
        // 
        serviceReportsToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { contractCompletionReportToolStripMenuItem });
        serviceReportsToolStripMenuItem.Name = "serviceReportsToolStripMenuItem";
        serviceReportsToolStripMenuItem.Size = new Size(154, 22);
        serviceReportsToolStripMenuItem.Text = "Service Reports";
        // 
        // contractCompletionReportToolStripMenuItem
        // 
        contractCompletionReportToolStripMenuItem.Name = "contractCompletionReportToolStripMenuItem";
        contractCompletionReportToolStripMenuItem.Size = new Size(224, 22);
        contractCompletionReportToolStripMenuItem.Text = "Contract Completion Report";
        // 
        // managementToolStripMenuItem
        // 
        managementToolStripMenuItem.Name = "managementToolStripMenuItem";
        managementToolStripMenuItem.Size = new Size(90, 20);
        managementToolStripMenuItem.Text = "Management";
        // 
        // engineerToolStripMenuItem
        // 
        engineerToolStripMenuItem.Name = "engineerToolStripMenuItem";
        engineerToolStripMenuItem.Size = new Size(65, 20);
        engineerToolStripMenuItem.Text = "Engineer";
        // 
        // windowToolStripMenuItem
        // 
        windowToolStripMenuItem.Name = "windowToolStripMenuItem";
        windowToolStripMenuItem.Size = new Size(63, 20);
        windowToolStripMenuItem.Text = "Window";
        // 
        // helpToolStripMenuItem
        // 
        helpToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { aboutToolStripMenuItem });
        helpToolStripMenuItem.Name = "helpToolStripMenuItem";
        helpToolStripMenuItem.Size = new Size(44, 20);
        helpToolStripMenuItem.Text = "&Help";
        helpToolStripMenuItem.Click += helpToolStripMenuItem_Click;
        // 
        // aboutToolStripMenuItem
        // 
        aboutToolStripMenuItem.Name = "aboutToolStripMenuItem";
        aboutToolStripMenuItem.Size = new Size(107, 22);
        aboutToolStripMenuItem.Text = "&About";
        aboutToolStripMenuItem.Click += aboutToolStripMenuItem_Click;
        // 
        // pnlToolbar
        // 
        pnlToolbar.Controls.Add(txtFilter);
        pnlToolbar.Dock = DockStyle.Top;
        pnlToolbar.Location = new Point(0, 90);
        pnlToolbar.Name = "pnlToolbar";
        pnlToolbar.Padding = new Padding(8, 6, 8, 6);
        pnlToolbar.Size = new Size(1500, 40);
        pnlToolbar.TabIndex = 1;
        pnlToolbar.WrapContents = false;
        // 
        // txtFilter
        // 
        txtFilter.Location = new Point(8, 9);
        txtFilter.Margin = new Padding(0, 3, 12, 3);
        txtFilter.Name = "txtFilter";
        txtFilter.PlaceholderText = "Filter by panel, type, text or AMX ref";
        txtFilter.Size = new Size(280, 23);
        txtFilter.TabIndex = 0;
        // 
        // pnlFooter
        // 
        pnlFooter.BackColor = Color.FromArgb(240, 240, 250);
        pnlFooter.Controls.Add(lblActive);
        pnlFooter.Dock = DockStyle.Bottom;
        pnlFooter.Location = new Point(0, 806);
        pnlFooter.Name = "pnlFooter";
        pnlFooter.Padding = new Padding(8, 5, 8, 5);
        pnlFooter.Size = new Size(1500, 26);
        pnlFooter.TabIndex = 3;
        pnlFooter.WrapContents = false;
        // 
        // lblActive
        // 
        lblActive.AutoSize = true;
        lblActive.ForeColor = Color.Black;
        lblActive.Location = new Point(8, 7);
        lblActive.Margin = new Padding(0, 2, 0, 0);
        lblActive.Name = "lblActive";
        lblActive.Size = new Size(45, 15);
        lblActive.TabIndex = 0;
        lblActive.Text = "0 Event";
        // 
        // eventsGrid
        // 
        eventsGrid.AllowUserToAddRows = false;
        eventsGrid.AllowUserToDeleteRows = false;
        eventsGrid.Dock = DockStyle.Fill;
        eventsGrid.Location = new Point(0, 130);
        eventsGrid.MultiSelect = false;
        eventsGrid.Name = "eventsGrid";
        eventsGrid.ReadOnly = true;
        eventsGrid.RowHeadersVisible = false;
        eventsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        eventsGrid.Size = new Size(1500, 676);
        eventsGrid.TabIndex = 0;
        eventsGrid.CellContextMenuStripNeeded += eventsGrid_CellContextMenuStripNeeded;
        // 
        // contextMenuStrip1
        // 
        contextMenuStrip1.Items.AddRange(new ToolStripItem[] { acceptToolStripMenuItem, clearTheAlarmToolStripMenuItem });
        contextMenuStrip1.Name = "contextMenuStrip1";
        contextMenuStrip1.Size = new Size(167, 48);
        // 
        // acceptToolStripMenuItem
        // 
        acceptToolStripMenuItem.Name = "acceptToolStripMenuItem";
        acceptToolStripMenuItem.Size = new Size(166, 22);
        acceptToolStripMenuItem.Text = "Accept the Alarm";
        acceptToolStripMenuItem.Click += acceptToolStripMenuItem_Click;
        // 
        // clearTheAlarmToolStripMenuItem
        // 
        clearTheAlarmToolStripMenuItem.Name = "clearTheAlarmToolStripMenuItem";
        clearTheAlarmToolStripMenuItem.Size = new Size(166, 22);
        clearTheAlarmToolStripMenuItem.Text = "Clear the Alarm";
        clearTheAlarmToolStripMenuItem.Click += clearTheAlarmToolStripMenuItem_Click;
        // 
        // MainForm
        // 
        ClientSize = new Size(1500, 832);
        Controls.Add(eventsGrid);
        Controls.Add(pnlToolbar);
        Controls.Add(pnlStatus);
        Controls.Add(menuStrip1);
        Controls.Add(pnlFooter);
        Icon = (Icon)resources.GetObject("$this.Icon");
        MainMenuStrip = menuStrip1;
        MinimizeBox = false;
        MinimumSize = new Size(760, 400);
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "AMX Events - native form";
        pnlStatus.ResumeLayout(false);
        pnlStatus.PerformLayout();
        menuStrip1.ResumeLayout(false);
        menuStrip1.PerformLayout();
        pnlToolbar.ResumeLayout(false);
        pnlToolbar.PerformLayout();
        pnlFooter.ResumeLayout(false);
        pnlFooter.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)eventsGrid).EndInit();
        contextMenuStrip1.ResumeLayout(false);
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private FlowLayoutPanel pnlStatus;
    private Label lblBroker;
    private Label lblPanels;
    private Label lblMeta;
    private FlowLayoutPanel pnlToolbar;
    private TextBox txtFilter;
    private FlowLayoutPanel pnlFooter;
    private Label lblActive;
    private DataGridView eventsGrid;
    private MenuStrip menuStrip1;
    private ToolStripMenuItem mainMenuToolStripMenuItem;
    private ToolStripMenuItem logOffToolStripMenuItem;
    private ToolStripMenuItem smartToolStripMenuItem;
    private ToolStripMenuItem managementToolStripMenuItem;
    private ToolStripMenuItem engineerToolStripMenuItem;
    private ToolStripMenuItem windowToolStripMenuItem;
    private ToolStripMenuItem helpToolStripMenuItem;
    private ToolStripMenuItem serviceReportsToolStripMenuItem;
    private ToolStripMenuItem contractCompletionReportToolStripMenuItem;
    private ToolStripMenuItem aboutToolStripMenuItem;
    private ContextMenuStrip contextMenuStrip1;
    private ToolStripMenuItem acceptToolStripMenuItem;
    private ToolStripMenuItem clearTheAlarmToolStripMenuItem;
    private Button btEvents;
    private Button btHistory;
    private Button btBack;
    private Button btForward;
    // The six DataGridViewTextBoxColumn fields (colDate, colTime, colType, colAmxRef,
    // colInputType, colText) used to be declared here too. They're now declared and
    // built entirely in MainForm.cs's ConfigureEventsGridColumns() instead — see the
    // comment on eventsGrid above for why.
}
