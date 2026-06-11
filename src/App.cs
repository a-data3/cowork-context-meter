// App.cs - WinForms UI for Cowork Context Meter.
// Compiled with .NET Framework 4 csc.exe -- C# 5 syntax only.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CoworkContextMeter
{
    public class MainForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        public static void Main()
        {
            // MUST run before ANY System.IO type is touched: the switch values
            // are cached on first read, and Cowork transcript paths (274-463
            // chars) need modern path handling + \\?\ to work with the
            // machine-wide LongPathsEnabled=0.
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private const int ColSession = 0;
        private const int ColSource = 1;
        private const int ColProject = 2;
        private const int ColActivity = 3;
        private const int ColModel = 4;
        private const int ColTokens = 5;
        private const int ColPercent = 6;

        private const string Dot = " \u00B7 ";
        private const string LivePrefix = "\u25CF live  ";

        private readonly Scanner _scanner = new Scanner();
        private List<SessionInfo> _all = new List<SessionInfo>();

        private Button _btnRefresh;
        private CheckBox _chkAuto;
        private ComboBox _cmbWindow;
        private TextBox _txtSearch;
        private CheckBox _chkHideEmpty;
        private CheckBox _chkHideArchived;
        private DataGridView _grid;
        private ToolStripStatusLabel _statusLabel;
        private Timer _timer;
        private Font _boldFont;
        private ToolTip _toolTip;

        private bool _scanning;
        private int _sortColumn = ColActivity;
        private bool _sortAsc = false;
        private long _lastScanMs;

        public MainForm()
        {
            Text = "Cowork Context Meter";
            Font = new Font("Segoe UI", 9f);
            _boldFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            ClientSize = new Size(1050, 620);
            MinimumSize = new Size(720, 400);
            StartPosition = FormStartPosition.CenterScreen;

            _grid = BuildGrid();
            Controls.Add(_grid);
            Controls.Add(BuildToolbar());
            Controls.Add(BuildStatusStrip());

            _timer = new Timer();
            _timer.Interval = 5000;
            _timer.Tick += delegate { StartScan(); };
            _timer.Enabled = _chkAuto.Checked;

            Shown += delegate { StartScan(); };
        }

        // ----- UI construction ---------------------------------------------

        private Control BuildToolbar()
        {
            FlowLayoutPanel panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Top;
            panel.Height = 38;
            panel.FlowDirection = FlowDirection.LeftToRight;
            panel.WrapContents = false;
            panel.Padding = new Padding(6, 5, 6, 0);

            _btnRefresh = new Button();
            _btnRefresh.Text = "Refresh";
            _btnRefresh.AutoSize = true;
            _btnRefresh.Margin = new Padding(0, 0, 12, 0);
            _btnRefresh.Click += delegate { StartScan(); };

            _chkAuto = new CheckBox();
            _chkAuto.Text = "Auto-refresh (5s)";
            _chkAuto.AutoSize = true;
            _chkAuto.Checked = true;
            _chkAuto.Margin = new Padding(0, 5, 14, 0);
            _chkAuto.CheckedChanged += delegate { _timer.Enabled = _chkAuto.Checked; };

            Label lblWindow = new Label();
            lblWindow.Text = "Window:";
            lblWindow.AutoSize = true;
            lblWindow.Margin = new Padding(0, 8, 4, 0);

            _cmbWindow = new ComboBox();
            _cmbWindow.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbWindow.Width = 80;
            _cmbWindow.Items.Add("Auto");
            _cmbWindow.Items.Add("200k");
            _cmbWindow.Items.Add("500k");
            _cmbWindow.Items.Add("1M");
            _cmbWindow.SelectedIndex = 0;
            _cmbWindow.Margin = new Padding(0, 4, 14, 0);
            _cmbWindow.SelectedIndexChanged += delegate { RebuildRows(); };

            Label lblSearch = new Label();
            lblSearch.Text = "Search:";
            lblSearch.AutoSize = true;
            lblSearch.Margin = new Padding(0, 8, 4, 0);

            _txtSearch = new TextBox();
            _txtSearch.Width = 220;
            _txtSearch.Margin = new Padding(0, 5, 14, 0);
            _txtSearch.TextChanged += delegate { RebuildRows(); };

            _chkHideEmpty = new CheckBox();
            _chkHideEmpty.Text = "Hide empty";
            _chkHideEmpty.AutoSize = true;
            _chkHideEmpty.Checked = true;
            _chkHideEmpty.Margin = new Padding(0, 5, 14, 0);
            _chkHideEmpty.CheckedChanged += delegate { RebuildRows(); };

            _chkHideArchived = new CheckBox();
            _chkHideArchived.Text = "Hide archived";
            _chkHideArchived.AutoSize = true;
            _chkHideArchived.Checked = true;
            _chkHideArchived.Margin = new Padding(0, 5, 0, 0);
            _chkHideArchived.CheckedChanged += delegate { RebuildRows(); };

            _toolTip = new ToolTip();
            _toolTip.SetToolTip(_cmbWindow,
                "Window used for the % column. Cowork sessions whose state file records a"
                + " \"[1m]\" model always use 1,000,000 regardless of this setting.");
            _toolTip.SetToolTip(_chkHideArchived, "Hide Cowork sessions marked archived");

            panel.Controls.Add(_btnRefresh);
            panel.Controls.Add(_chkAuto);
            panel.Controls.Add(lblWindow);
            panel.Controls.Add(_cmbWindow);
            panel.Controls.Add(lblSearch);
            panel.Controls.Add(_txtSearch);
            panel.Controls.Add(_chkHideEmpty);
            panel.Controls.Add(_chkHideArchived);
            return panel;
        }

        private DataGridView BuildGrid()
        {
            DataGridView grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.AutoGenerateColumns = false;
            grid.BackgroundColor = SystemColors.Window;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.GridColor = Color.FromArgb(230, 230, 230);
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.StandardTab = true;

            // reduce flicker on refresh (DoubleBuffered is protected)
            try
            {
                PropertyInfo pi = typeof(DataGridView).GetProperty("DoubleBuffered",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (pi != null) pi.SetValue(grid, true, null);
            }
            catch { }

            AddColumn(grid, "Session", 320, false, true);
            AddColumn(grid, "Source", 65, false, false);
            AddColumn(grid, "Project", 150, false, false);
            AddColumn(grid, "Last Activity", 115, false, false);
            AddColumn(grid, "Model", 115, false, false);
            AddColumn(grid, "Context Used", 100, true, false);
            AddColumn(grid, "% of Window", 170, false, false);

            grid.ColumnHeaderMouseClick += GridColumnHeaderMouseClick;
            grid.CellPainting += GridCellPainting;
            grid.CellDoubleClick += GridCellDoubleClick;
            return grid;
        }

        private static void AddColumn(DataGridView grid, string header, int width,
            bool rightAlign, bool fill)
        {
            DataGridViewTextBoxColumn col = new DataGridViewTextBoxColumn();
            col.HeaderText = header;
            col.Width = width;
            col.ReadOnly = true;
            col.SortMode = DataGridViewColumnSortMode.Programmatic;
            col.Resizable = DataGridViewTriState.True;
            if (rightAlign)
            {
                col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                col.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
            if (fill)
            {
                col.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                col.MinimumWidth = 180;
            }
            grid.Columns.Add(col);
        }

        private Control BuildStatusStrip()
        {
            StatusStrip strip = new StatusStrip();
            strip.SizingGrip = true;
            _statusLabel = new ToolStripStatusLabel();
            _statusLabel.Spring = true;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.Text = "Starting...";
            strip.Items.Add(_statusLabel);
            return strip;
        }

        // ----- scanning ----------------------------------------------------

        private void StartScan()
        {
            if (_scanning) return;
            _scanning = true;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<SessionInfo> list = null;
                string error = null;
                Stopwatch sw = Stopwatch.StartNew();
                try { list = _scanner.Scan(); }
                catch (Exception ex) { error = ex.Message; }
                sw.Stop();
                long ms = sw.ElapsedMilliseconds;

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _scanning = false;
                        if (error != null)
                        {
                            _statusLabel.Text = "Scan error: " + error;
                            return;
                        }
                        _all = list;
                        _lastScanMs = ms;
                        RebuildRows();
                    });
                }
                catch
                {
                    _scanning = false; // form closed mid-scan
                }
            });
        }

        // ----- grid population ---------------------------------------------

        private void RebuildRows()
        {
            List<SessionInfo> rows = new List<SessionInfo>();
            string q = _txtSearch.Text.Trim().ToLowerInvariant();

            foreach (SessionInfo s in _all)
            {
                if (_chkHideEmpty.Checked && !s.HasUsage) continue;
                if (_chkHideArchived.Checked && s.IsArchived) continue;
                if (q.Length > 0)
                {
                    string hay = ((s.Title ?? "") + " " + (s.ProjectName ?? "") + " "
                        + (s.Model ?? "") + " " + (s.SessionId ?? "") + " "
                        + (s.Source ?? "")).ToLowerInvariant();
                    if (hay.IndexOf(q, StringComparison.Ordinal) < 0) continue;
                }
                rows.Add(s);
            }

            SortRows(rows);

            // Capture the selection from SelectedRows, not CurrentRow: after
            // Rows.Clear()+Add the grid silently makes row 0 current, so
            // reading CurrentRow on the next tick would migrate the user's
            // selection to the top row every ~10 s under auto-refresh.
            string selectedId = null;
            DataGridViewRow capturedRow = null;
            if (_grid.SelectedRows.Count > 0) capturedRow = _grid.SelectedRows[0];
            else capturedRow = _grid.CurrentRow;
            if (capturedRow != null)
            {
                SessionInfo cur = capturedRow.Tag as SessionInfo;
                if (cur != null) selectedId = cur.SessionId;
            }
            int firstVisible = -1;
            try { firstVisible = _grid.FirstDisplayedScrollingRowIndex; } catch { }

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (SessionInfo s in rows)
            {
                int idx = _grid.Rows.Add();
                DataGridViewRow row = _grid.Rows[idx];
                row.Tag = s;

                string title = s.Title;
                if (string.IsNullOrEmpty(title)) title = s.SessionId ?? "";
                if (s.IsLive)
                {
                    title = LivePrefix + title;
                    row.DefaultCellStyle.Font = _boldFont;
                }
                row.Cells[ColSession].Value = title;
                row.Cells[ColSource].Value = s.Source ?? "Code";
                row.Cells[ColProject].Value = s.ProjectName ?? "";
                row.Cells[ColActivity].Value =
                    s.LastActivityUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                row.Cells[ColModel].Value = s.Model ?? "";
                if (s.HasUsage)
                {
                    row.Cells[ColTokens].Value =
                        s.TotalContextTokens.ToString("N0", CultureInfo.CurrentCulture);
                    row.Cells[ColPercent].Value =
                        PercentOf(s).ToString("0.0", CultureInfo.CurrentCulture) + "%";
                }
                else
                {
                    row.Cells[ColTokens].Value = "";
                    row.Cells[ColPercent].Value = "";
                }
            }

            // sort glyphs
            for (int i = 0; i < _grid.Columns.Count; i++)
                _grid.Columns[i].HeaderCell.SortGlyphDirection = SortOrder.None;
            _grid.Columns[_sortColumn].HeaderCell.SortGlyphDirection =
                _sortAsc ? SortOrder.Ascending : SortOrder.Descending;

            // restore selection and scroll position
            if (selectedId != null)
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    SessionInfo s = row.Tag as SessionInfo;
                    if (s != null && s.SessionId == selectedId)
                    {
                        // Re-establish CurrentCell as well as Selected so the
                        // current row tracks the user's row instead of staying
                        // on the auto-assigned row 0.
                        try { _grid.CurrentCell = row.Cells[ColSession]; }
                        catch { }
                        row.Selected = true;
                        break;
                    }
                }
            }
            if (firstVisible >= 0 && firstVisible < _grid.Rows.Count)
            {
                try { _grid.FirstDisplayedScrollingRowIndex = firstVisible; } catch { }
            }
            _grid.ResumeLayout();

            UpdateStatus(rows.Count);
        }

        private void SortRows(List<SessionInfo> rows)
        {
            int col = _sortColumn;
            bool asc = _sortAsc;
            rows.Sort(delegate(SessionInfo a, SessionInfo b)
            {
                int c;
                switch (col)
                {
                    case ColSession:
                        c = string.Compare(a.Title ?? "", b.Title ?? "", StringComparison.OrdinalIgnoreCase);
                        break;
                    case ColSource:
                        c = string.Compare(a.Source ?? "", b.Source ?? "", StringComparison.OrdinalIgnoreCase);
                        break;
                    case ColProject:
                        c = string.Compare(a.ProjectName ?? "", b.ProjectName ?? "", StringComparison.OrdinalIgnoreCase);
                        break;
                    case ColActivity:
                        c = a.LastActivityUtc.CompareTo(b.LastActivityUtc);
                        break;
                    case ColModel:
                        c = string.Compare(a.Model ?? "", b.Model ?? "", StringComparison.OrdinalIgnoreCase);
                        break;
                    case ColTokens:
                        c = a.TotalContextTokens.CompareTo(b.TotalContextTokens);
                        break;
                    case ColPercent:
                        c = PercentOf(a).CompareTo(PercentOf(b));
                        break;
                    default:
                        c = 0;
                        break;
                }
                if (c == 0) c = a.LastActivityUtc.CompareTo(b.LastActivityUtc);
                if (!asc) c = -c;
                return c;
            });
        }

        private void UpdateStatus(int shown)
        {
            string text;
            if (shown == _all.Count)
            {
                text = string.Format(CultureInfo.CurrentCulture,
                    "{0} sessions{1}scanned in {2} ms{1}{3}",
                    _all.Count, Dot, _lastScanMs, _scanner.ProjectsDir);
            }
            else
            {
                text = string.Format(CultureInfo.CurrentCulture,
                    "{0} shown of {1} sessions{2}scanned in {3} ms{2}{4}",
                    shown, _all.Count, Dot, _lastScanMs, _scanner.ProjectsDir);
            }
            _statusLabel.Text = text;
        }

        // ----- window size / percent ---------------------------------------

        private long WindowFor(SessionInfo s)
        {
            // exact window known (Cowork state file recorded a "[1m]" model):
            // always wins; the combo only governs sessions without an override
            if (s != null && s.WindowOverride > 0) return s.WindowOverride;
            switch (_cmbWindow.SelectedIndex)
            {
                case 1: return 200000L;
                case 2: return 500000L;
                case 3: return 1000000L;
                default:
                    // Auto: 200k unless this session has clearly exceeded it
                    if (s != null && s.TotalContextTokens > 200000L) return 1000000L;
                    return 200000L;
            }
        }

        private double PercentOf(SessionInfo s)
        {
            if (s == null || !s.HasUsage) return 0.0;
            long w = WindowFor(s);
            if (w <= 0) return 0.0;
            return (double)s.TotalContextTokens * 100.0 / (double)w;
        }

        // ----- grid events -------------------------------------------------

        private void GridColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex < 0) return;
            if (_sortColumn == e.ColumnIndex)
            {
                _sortAsc = !_sortAsc;
            }
            else
            {
                _sortColumn = e.ColumnIndex;
                // text columns default ascending, numeric/date descending
                _sortAsc = e.ColumnIndex == ColSession
                        || e.ColumnIndex == ColSource
                        || e.ColumnIndex == ColProject
                        || e.ColumnIndex == ColModel;
            }
            RebuildRows();
        }

        private void GridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ColPercent) return;
            SessionInfo s = _grid.Rows[e.RowIndex].Tag as SessionInfo;
            if (s == null || !s.HasUsage) return;

            e.PaintBackground(e.CellBounds, true);

            double pct = PercentOf(s);
            double clamped = pct;
            if (clamped > 100.0) clamped = 100.0;
            if (clamped < 0.0) clamped = 0.0;

            Rectangle bar = new Rectangle(
                e.CellBounds.X + 4, e.CellBounds.Y + 4,
                e.CellBounds.Width - 9, e.CellBounds.Height - 9);
            if (bar.Width > 2 && bar.Height > 2)
            {
                Color barColor;
                if (pct > 80.0) barColor = Color.FromArgb(217, 58, 50);        // red
                else if (pct >= 60.0) barColor = Color.FromArgb(240, 160, 20); // amber
                else barColor = Color.FromArgb(76, 165, 80);                   // green

                using (SolidBrush back = new SolidBrush(Color.FromArgb(235, 235, 235)))
                {
                    e.Graphics.FillRectangle(back, bar);
                }
                int w = (int)Math.Round(bar.Width * clamped / 100.0);
                if (w > 0)
                {
                    using (SolidBrush fill = new SolidBrush(barColor))
                    {
                        e.Graphics.FillRectangle(fill, new Rectangle(bar.X, bar.Y, w, bar.Height));
                    }
                }
                using (Pen border = new Pen(Color.FromArgb(200, 200, 200)))
                {
                    e.Graphics.DrawRectangle(border, bar);
                }
                string txt = pct.ToString("0.0", CultureInfo.CurrentCulture) + "%";
                TextRenderer.DrawText(e.Graphics, txt, e.CellStyle.Font, bar,
                    Color.FromArgb(20, 20, 20),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            e.Handled = true;
        }

        private void GridCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            SessionInfo s = _grid.Rows[e.RowIndex].Tag as SessionInfo;
            if (s == null) return;
            ShowDetails(s);
        }

        // ----- details dialog ----------------------------------------------

        private void ShowDetails(SessionInfo s)
        {
            // Forms shown via ShowDialog are NOT disposed automatically on
            // close (unlike Show), so dispose the dialog and its Consolas
            // font explicitly or every double-click leaks GDI handles.
            Form dlg = new Form();
            Font monoFont = new Font("Consolas", 9f);
            try
            {
                ShowDetailsBody(s, dlg, monoFont);
            }
            finally
            {
                dlg.Dispose();
                monoFont.Dispose();
            }
        }

        private void ShowDetailsBody(SessionInfo s, Form dlg, Font monoFont)
        {
            dlg.Text = "Session details";
            dlg.Font = this.Font;
            dlg.ClientSize = new Size(740, 540);
            dlg.MinimumSize = new Size(520, 360);
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.MinimizeBox = false;
            dlg.ShowInTaskbar = false;

            TextBox box = new TextBox();
            box.Multiline = true;
            box.ReadOnly = true;
            box.WordWrap = false;
            box.ScrollBars = ScrollBars.Both;
            box.Font = monoFont;
            box.BackColor = SystemColors.Window;
            box.Dock = DockStyle.Fill;

            FlowLayoutPanel bottom = new FlowLayoutPanel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 38;
            bottom.FlowDirection = FlowDirection.LeftToRight;
            bottom.Padding = new Padding(6, 4, 6, 0);

            Button btnOpen = new Button();
            btnOpen.Text = "Open folder";
            btnOpen.AutoSize = true;
            btnOpen.Click += delegate
            {
                try
                {
                    Process.Start("explorer.exe", "/select,\"" + s.FilePath + "\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(dlg, "Could not open folder: " + ex.Message,
                        "Cowork Context Meter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            Button btnGrowth = new Button();
            btnGrowth.Text = "Load context growth";
            btnGrowth.AutoSize = true;

            Button btnClose = new Button();
            btnClose.Text = "Close";
            btnClose.AutoSize = true;
            btnClose.Click += delegate { dlg.Close(); };

            bottom.Controls.Add(btnOpen);
            bottom.Controls.Add(btnGrowth);
            bottom.Controls.Add(btnClose);

            long window = WindowFor(s);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Title:           " + (s.Title ?? ""));
            sb.AppendLine("Session id:      " + (s.SessionId ?? ""));
            sb.AppendLine("Source:          " + (s.Source ?? "Code"));
            sb.AppendLine("Archived:        " + (s.IsArchived ? "yes" : "no"));
            sb.AppendLine("File:            " + (s.FilePath ?? ""));
            sb.AppendLine("File size:       " + FormatBytes(s.FileSizeBytes));
            sb.AppendLine("Working dir:     " + (s.Cwd ?? "(unknown - using project folder name)"));
            sb.AppendLine("Project:         " + (s.ProjectName ?? ""));
            sb.AppendLine("Model:           " + (s.Model ?? ""));
            sb.AppendLine("Live:            " + (s.IsLive ? "yes" : "no"));
            sb.AppendLine("Last activity:   "
                + s.LastActivityUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine();
            string windowNote;
            if (s.WindowOverride > 0)
                windowNote = "  (exact: state file records a [1m] model)";
            else if (_cmbWindow.SelectedIndex == 0)
                windowNote = "  (Auto)";
            else
                windowNote = "";
            sb.AppendLine("Window used:     " + window.ToString("N0", CultureInfo.CurrentCulture)
                + windowNote);
            sb.AppendLine();
            if (s.HasUsage)
            {
                sb.AppendLine("Token breakdown (last assistant turn):");
                sb.AppendLine(string.Format(CultureInfo.CurrentCulture,
                    "  input_tokens:                  {0,12:N0}", s.InputTokens));
                sb.AppendLine(string.Format(CultureInfo.CurrentCulture,
                    "  cache_creation_input_tokens:   {0,12:N0}", s.CacheCreationTokens));
                sb.AppendLine(string.Format(CultureInfo.CurrentCulture,
                    "  cache_read_input_tokens:       {0,12:N0}", s.CacheReadTokens));
                sb.AppendLine(string.Format(CultureInfo.CurrentCulture,
                    "  output_tokens:                 {0,12:N0}", s.OutputTokens));
                sb.AppendLine(string.Format(CultureInfo.CurrentCulture,
                    "  TOTAL context used:            {0,12:N0}", s.TotalContextTokens));
                sb.AppendLine(string.Format(CultureInfo.CurrentCulture,
                    "  Percent of window:             {0,11:0.0}%", PercentOf(s)));
            }
            else
            {
                sb.AppendLine("No assistant usage found in this transcript.");
            }
            sb.AppendLine();
            sb.AppendLine("Context growth: click \"Load context growth\" to parse the full");
            sb.AppendLine("transcript and list the last 25 points where the total changed.");
            box.Text = sb.ToString();

            btnGrowth.Click += delegate
            {
                btnGrowth.Enabled = false;
                btnGrowth.Text = "Parsing...";
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    string text;
                    try
                    {
                        List<GrowthPoint> pts = _scanner.ReadGrowth(s.FilePath, 25);
                        StringBuilder g = new StringBuilder();
                        g.AppendLine();
                        g.AppendLine(string.Format(CultureInfo.CurrentCulture,
                            "Context growth - last {0} change(s), oldest first:", pts.Count));
                        foreach (GrowthPoint pnt in pts)
                        {
                            string when;
                            if (pnt.TimestampUtc == DateTime.MinValue)
                                when = "(no timestamp)     ";
                            else
                                when = pnt.TimestampUtc.ToLocalTime()
                                    .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                            g.AppendLine(string.Format(CultureInfo.CurrentCulture,
                                "  {0}  {1,12:N0} tokens", when, pnt.Total));
                        }
                        if (pts.Count == 0)
                            g.AppendLine("  (no usage lines found)");
                        text = g.ToString();
                    }
                    catch (Exception ex)
                    {
                        text = Environment.NewLine + "Growth parse error: " + ex.Message;
                    }
                    try
                    {
                        dlg.BeginInvoke((MethodInvoker)delegate
                        {
                            box.AppendText(text);
                            btnGrowth.Text = "Load context growth";
                            btnGrowth.Enabled = true;
                        });
                    }
                    catch { }
                });
            };

            dlg.Controls.Add(box);
            dlg.Controls.Add(bottom);
            dlg.ShowDialog(this);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1048576L)
                return string.Format(CultureInfo.CurrentCulture,
                    "{0:0.0} MB ({1:N0} bytes)", bytes / 1048576.0, bytes);
            if (bytes >= 1024L)
                return string.Format(CultureInfo.CurrentCulture,
                    "{0:0.0} KB ({1:N0} bytes)", bytes / 1024.0, bytes);
            return string.Format(CultureInfo.CurrentCulture, "{0:N0} bytes", bytes);
        }
    }
}
