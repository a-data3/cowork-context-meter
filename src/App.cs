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
        private bool _firstScan = true;
        private int _sortColumn = ColActivity;
        private bool _sortAsc = false;
        private long _lastScanMs;

        private NotifyIcon _tray;
        private ToolStripMenuItem _trayOverlayItem;
        private OverlayForm _overlay;
        private HashSet<string> _pinned;
        private const string PinPrefix = "★ ";

        public MainForm()
        {
            Text = AppTitle();
            Font = new Font("Segoe UI", 9f);
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            HandleCreated += delegate { Theme.DarkTitleBar(Handle); };
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

            _pinned = LoadPinned();
            _grid.ContextMenuStrip = BuildGridMenu();
            _grid.CellMouseDown += delegate(object sender, DataGridViewCellMouseEventArgs e)
            {
                // right-click selects the row the menu will act on
                if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
                {
                    try { _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[ColSession]; } catch { }
                    _grid.Rows[e.RowIndex].Selected = true;
                }
            };

            _overlay = new OverlayForm();
            _overlay.Location = LoadOverlayPos();
            _overlay.ContextMenuStrip = BuildOverlayMenu();
            _overlay.Show();

            BuildTray();
            Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
            FormClosing += delegate
            {
                SaveOverlayPos(_overlay.Location);
                _tray.Visible = false; // else a dead icon lingers in the tray
            };
        }

        // ----- tray + overlay ----------------------------------------------

        private void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open meter", null, delegate { RestoreFromTray(); });
            _trayOverlayItem = new ToolStripMenuItem("Show overlay");
            _trayOverlayItem.Checked = true;
            _trayOverlayItem.Click += delegate
            {
                _trayOverlayItem.Checked = !_trayOverlayItem.Checked;
                _overlay.Visible = _trayOverlayItem.Checked;
            };
            menu.Items.Add(_trayOverlayItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Close(); });

            _tray = new NotifyIcon();
            _tray.Icon = Icon;
            _tray.Text = "Cowork Context Meter " + AppVersion;
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { RestoreFromTray(); };
            _tray.Visible = true;
        }

        private void RestoreFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        private ContextMenuStrip BuildGridMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem pin = new ToolStripMenuItem("Pin to overlay");
            pin.Click += delegate
            {
                SessionInfo s = SelectedSession();
                if (s != null) TogglePin(s.SessionId);
            };
            menu.Items.Add(pin);
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                SessionInfo s = SelectedSession();
                if (s == null) { e.Cancel = true; return; }
                pin.Text = _pinned.Contains(s.SessionId) ? "Unpin from overlay" : "Pin to overlay";
            };
            return menu;
        }

        private void TogglePin(string id)
        {
            if (id == null) return;
            if (!_pinned.Remove(id)) _pinned.Add(id);
            SavePinned();
            RebuildRows();
        }

        /// <summary>
        /// Right-click menu on the overlay itself: the 15 most recent Claude Code
        /// sessions (live first by recency), checked = pinned. Rebuilt on every
        /// open so it always reflects the latest scan.
        /// </summary>
        private ContextMenuStrip BuildOverlayMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("(loading)"); // an empty strip refuses to open
            menu.Opening += delegate
            {
                menu.Items.Clear();
                List<SessionInfo> code = new List<SessionInfo>();
                foreach (SessionInfo s in _all)
                    if (s != null && s.SessionId != null && s.HasUsage && s.Source == "Code")
                        code.Add(s);
                code.Sort(delegate(SessionInfo a, SessionInfo b)
                {
                    return b.LastActivityUtc.CompareTo(a.LastActivityUtc);
                });

                ToolStripMenuItem hdr = new ToolStripMenuItem("Pin Claude Code sessions:");
                hdr.Enabled = false;
                menu.Items.Add(hdr);
                for (int i = 0; i < code.Count && i < 15; i++)
                {
                    SessionInfo s = code[i];
                    string id = s.SessionId;
                    string name = string.IsNullOrEmpty(s.Title) ? id : s.Title;
                    if (name.Length > 55) name = name.Substring(0, 55) + "...";
                    ToolStripMenuItem it = new ToolStripMenuItem(
                        (s.IsLive ? "● " : "") + name + "   "
                        + PercentOf(s).ToString("0", CultureInfo.CurrentCulture) + "%");
                    it.Checked = _pinned.Contains(id);
                    it.Click += delegate { TogglePin(id); };
                    menu.Items.Add(it);
                }
                if (code.Count == 0) menu.Items.Add("(no Claude Code sessions found)").Enabled = false;

                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Open meter", null, delegate { RestoreFromTray(); });
                menu.Items.Add("Hide overlay", null, delegate
                {
                    _trayOverlayItem.Checked = false;
                    _overlay.Visible = false;
                });
                menu.Items.Add("Exit", null, delegate { Close(); });
            };
            return menu;
        }

        private SessionInfo SelectedSession()
        {
            if (_grid.SelectedRows.Count == 0) return null;
            return _grid.SelectedRows[0].Tag as SessionInfo;
        }

        /// <summary>Push the pinned sessions (current numbers) to the overlay + tray tooltip.</summary>
        private void UpdateOverlay()
        {
            List<OverlayForm.Line> lines = new List<OverlayForm.Line>();
            foreach (SessionInfo s in _all)
            {
                if (s == null || s.SessionId == null || !_pinned.Contains(s.SessionId)) continue;
                OverlayForm.Line l = new OverlayForm.Line();
                l.Name = string.IsNullOrEmpty(s.Title) ? s.SessionId : s.Title;
                l.Pct = PercentOf(s);
                l.Usage = s.HasUsage
                    ? l.Pct.ToString("0", CultureInfo.CurrentCulture) + "%  "
                      + (s.TotalContextTokens / 1000).ToString("N0", CultureInfo.CurrentCulture) + "k"
                    : "--";
                lines.Add(l);
            }
            _overlay.SetLines(lines);

            // NotifyIcon.Text is capped at 63 chars on .NET Framework
            string tip = lines.Count > 0
                ? lines[0].Usage + "  " + lines[0].Name
                : "Cowork Context Meter " + AppVersion;
            _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        private static string AppDataFile(string name)
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CoworkContextMeter");
            try { System.IO.Directory.CreateDirectory(dir); } catch { }
            return System.IO.Path.Combine(dir, name);
        }

        private static HashSet<string> LoadPinned()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string line in System.IO.File.ReadAllLines(AppDataFile("pinned.txt")))
                    if (line.Trim().Length > 0) set.Add(line.Trim());
            }
            catch { }
            return set;
        }

        private void SavePinned()
        {
            try { System.IO.File.WriteAllLines(AppDataFile("pinned.txt"), new List<string>(_pinned).ToArray()); }
            catch { }
        }

        private static Point LoadOverlayPos()
        {
            try
            {
                string[] p = System.IO.File.ReadAllText(AppDataFile("overlay-pos.txt")).Split(',');
                Point pt = new Point(int.Parse(p[0]), int.Parse(p[1]));
                // only reuse if still on a connected screen
                foreach (Screen sc in Screen.AllScreens)
                    if (sc.WorkingArea.Contains(pt)) return pt;
            }
            catch { }
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            return new Point(wa.Right - 340, wa.Top + 20);
        }

        private static void SaveOverlayPos(Point p)
        {
            try { System.IO.File.WriteAllText(AppDataFile("overlay-pos.txt"), p.X + "," + p.Y); }
            catch { }
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
            Theme.StyleToolbar(panel);
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
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            Theme.StyleGrid(grid);
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
            strip.RenderMode = ToolStripRenderMode.System;
            strip.BackColor = Theme.Card;
            _statusLabel.ForeColor = Theme.FgMuted;
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
                bool first = _firstScan;
                _firstScan = false;
                Stopwatch sw = Stopwatch.StartNew();
                DebugLog("scan start first=" + first);
                // Plain, NON-BLOCKING scan. The Scanner's on-disk cache already
                // covers a cold start while the folders are still settling, so we
                // must not sleep/retry here: that delayed the first render for
                // tens of seconds AND starved the 5 s auto-refresh, because
                // _scanning stayed true the whole time (grid rendered nothing).
                try { list = _scanner.Scan(); }
                catch (Exception ex) { error = ex.ToString(); }
                sw.Stop();
                long ms = sw.ElapsedMilliseconds;
                if (error != null)
                    DebugLog("scan ERROR after " + ms + "ms: "
                        + error.Replace("\r", " ").Replace("\n", " | "));
                else
                    DebugLog("scan ok " + ms + "ms total=" + (list == null ? -1 : list.Count)
                        + " cowork=" + CountCowork(list) + " stale=" + _scanner.StaleCoworkShown);

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _scanning = false;
                        if (error != null)
                        {
                            // Keep the rows we already show -- one failed scan must
                            // never blank the list. Auto-refresh retries in 5 s.
                            _statusLabel.Text = "Scan error (will retry): " + FirstLine(error);
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

            int rendered = 0, skipped = 0;
            _grid.SuspendLayout();
            try
            {
                _grid.Rows.Clear();
                foreach (SessionInfo s in rows)
                {
                    try
                    {
                        int idx = _grid.Rows.Add();
                        DataGridViewRow row = _grid.Rows[idx];
                        row.Tag = s;

                        string title = s.Title;
                        if (string.IsNullOrEmpty(title)) title = s.SessionId ?? "";
                        if (_pinned.Contains(s.SessionId ?? "")) title = PinPrefix + title;
                        if (s.IsLive)
                        {
                            title = LivePrefix + title;
                            row.DefaultCellStyle.Font = _boldFont;
                        }
                        row.Cells[ColSession].Value = title;
                        row.Cells[ColSource].Value = s.Source ?? "Code";
                        row.Cells[ColProject].Value = s.ProjectName ?? "";
                        row.Cells[ColActivity].Value = FmtLocal(s.LastActivityUtc);
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
                        rendered++;
                    }
                    catch
                    {
                        // A single malformed session must NEVER blank the grid.
                        skipped++;
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
            }
            finally
            {
                _grid.ResumeLayout(); // always runs, even if a row threw
            }

            DebugLog("rebuild all=" + _all.Count + " coworkAll=" + CountCowork(_all)
                + " filtered=" + rows.Count + " rendered=" + rendered + " skipped=" + skipped
                + " q='" + q + "' hideEmpty=" + _chkHideEmpty.Checked
                + " hideArch=" + _chkHideArchived.Checked);

            UpdateStatus(rows.Count);
            UpdateOverlay();
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

        // Local-time formatting that can never throw: ToLocalTime() overflows
        // for near-min/max timestamps in some time zones, which (unguarded)
        // would abort the whole grid rebuild and blank the list.
        private static string FmtLocal(DateTime utc)
        {
            try { return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
            catch { return utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
        }

        /// <summary>
        /// Version of this build. BUMP THIS whenever the app changes.
        /// v2.5 = dark slate theme (UI UX Pro Max "Dark Mode (OLED)" palette):
        ///        dark title bar, flat toolbar, dark grid, slim % bars with the
        ///        number beside them; overlay gets rounded corners + per-row bar.
        /// v2.4 = right-click the OVERLAY to pin/unpin from the 15 most recent
        ///        Claude Code sessions (Cowork has merged into chat).
        /// v2.3 = system-tray icon (minimise hides to tray) + always-on-top
        ///        translucent overlay showing only PINNED sessions (right-click a
        ///        row > Pin to overlay). Pins and overlay position persist in
        ///        %LOCALAPPDATA%CoworkContextMeter.
        /// v2.2 = surface-aware context window for Cowork sessions. Cowork runs
        ///        a SMALLER window than other surfaces (claude-sonnet-5:
        ///        500,000 on Cowork vs 967,000 elsewhere), so a Cowork session
        ///        previously assumed 200k and displayed over 100% once it
        ///        passed 200,000 tokens. Auto-compaction fires at
        ///        (window - 33,000), so the amber/red bands now land where
        ///        they are actually useful.
        ///        NOTE: v2.1 (unreleased) also read Claude Desktop's HTTP cache
        ///        to recover CLOUD Cowork sessions. That is deliberately NOT in
        ///        v2.2: reading another app's browser cache is the behavioural
        ///        signature of an infostealer and was quarantined by antivirus,
        ///        it only ever worked ~2 attempts in 9, and it is unnecessary --
        ///        Cowork sessions run with cloud DISABLED write a normal
        ///        transcript and are read exactly and reliably.
        /// v2.0 = recognise daemon-run Cowork jobs (~/.claude/jobs/*/state.json):
        ///        label them "Cowork" and use the job's real name as the title.
        ///        Previously they looked like plain "Code" rows.
        /// v1.9 = revert v1.8's blanket relabel: claude-code-sessions holds real
        ///        Claude CODE sessions, so it is "Code" again (v1.8 wrongly
        ///        tagged the user's own Code sessions as Cowork).
        /// v1.8 = every Claude DESKTOP session counts as "Cowork" (both storage
        ///        generations); "Code" now means a pure command-line session.
        /// v1.7 = scan Claude Desktop's MSIX package container (the real home of
        ///        the Cowork session data) instead of only the plain %APPDATA%.
        /// </summary>
        private const string AppVersion = "v2.5";

        /// <summary>
        /// Window title: name + version + when this exe was actually built, so
        /// which build you are looking at is never a guess. The timestamp comes
        /// from the exe's own file time, so it can't go stale.
        /// </summary>
        private static string AppTitle()
        {
            string stamp = "unknown";
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(exe) && System.IO.File.Exists(exe))
                    stamp = System.IO.File.GetLastWriteTime(exe)
                        .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch { }
            return "Cowork Context Meter " + AppVersion + "  -  updated " + stamp;
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOfAny(new char[] { '\r', '\n' });
            return i > 0 ? s.Substring(0, i) : s;
        }

        private static int CountCowork(List<SessionInfo> list)
        {
            int n = 0;
            if (list != null)
                foreach (SessionInfo s in list)
                    if (s != null && s.Source == "Cowork") n++;
            return n;
        }

        // Small rolling black-box log so a recurrence of "no Cowork shown" is
        // provable (what the scan produced vs what was rendered) instead of a
        // guess. Never throws.
        private static void DebugLog(string msg)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CoworkContextMeter");
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "ui-debug.log");
                try
                {
                    System.IO.FileInfo fi = new System.IO.FileInfo(path);
                    if (fi.Exists && fi.Length > 262144) System.IO.File.Delete(path);
                }
                catch { }
                System.IO.File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + "  " + msg + Environment.NewLine);
            }
            catch { }
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
            if (_scanner.StaleCoworkShown)
                text += Dot + "âš  Cowork folder busy â€” showing last known";
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

            Rectangle cell = e.CellBounds;
            string txt = pct.ToString("0.0", CultureInfo.CurrentCulture) + "%";
            TextRenderer.DrawText(e.Graphics, txt, e.CellStyle.Font,
                new Rectangle(cell.X + 6, cell.Y, 52, cell.Height), Theme.Fg,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            Rectangle track = new Rectangle(cell.X + 64, cell.Y + cell.Height / 2 - 3,
                cell.Width - 72, 6);
            if (track.Width > 4)
            {
                using (SolidBrush back = new SolidBrush(Theme.Muted))
                    e.Graphics.FillRectangle(back, track);
                int w = (int)Math.Round(track.Width * clamped / 100.0);
                if (w > 0)
                    using (SolidBrush fill = new SolidBrush(Theme.ForPct(pct)))
                        e.Graphics.FillRectangle(fill, new Rectangle(track.X, track.Y, w, track.Height));
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

    /// <summary>
    /// Small borderless, always-on-top, semi-transparent panel listing only the
    /// pinned sessions: name + context used. Drag anywhere to move it.
    /// </summary>
    public class OverlayForm : Form
    {
        public class Line
        {
            public string Name;
            public string Usage;
            public double Pct;
        }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int RowH = 30;
        private List<Line> _lines = new List<Line>();
        private readonly Font _bold = new Font("Segoe UI", 9f, FontStyle.Bold);

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Bg;
            Opacity = 0.85;
            Font = new Font("Segoe UI", 9f);
            DoubleBuffered = true;
            Size = new Size(320, RowH + 10);
            // drag: pretend the click landed on a title bar
            MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
            };
        }

        // tool window: no Alt-Tab entry; never steal focus when shown
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80 /* WS_EX_TOOLWINDOW */ | 0x08000000 /* WS_EX_NOACTIVATE */;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.RoundCorners(Handle);
        }

        public void SetLines(List<Line> lines)
        {
            _lines = lines ?? new List<Line>();
            int h = Math.Max(1, _lines.Count) * RowH + 10;
            if (Height != h) Height = h;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (_lines.Count == 0)
            {
                TextRenderer.DrawText(g, "Right-click a session → Pin to overlay", Font,
                    new Rectangle(10, 5, Width - 20, RowH), Color.Silver,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }
            const int usageW = 110;
            int y = 5;
            foreach (Line l in _lines)
            {
                Color c = Theme.ForPct(l.Pct);
                TextRenderer.DrawText(g, l.Name, Font,
                    new Rectangle(10, y, Width - 20 - usageW, 20), Theme.Fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, l.Usage, _bold,
                    new Rectangle(Width - 10 - usageW, y, usageW, 20), c,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                // slim progress bar under the line (text above carries the number)
                Rectangle track = new Rectangle(10, y + 21, Width - 20, 3);
                using (SolidBrush back = new SolidBrush(Theme.Muted)) g.FillRectangle(back, track);
                double pct = l.Pct > 100.0 ? 100.0 : (l.Pct < 0.0 ? 0.0 : l.Pct);
                int w = (int)Math.Round(track.Width * pct / 100.0);
                if (w > 0) using (SolidBrush fill = new SolidBrush(c))
                    g.FillRectangle(fill, new Rectangle(track.X, track.Y, w, track.Height));
                y += RowH;
            }
        }
    }

    /// <summary>
    /// Dark slate palette from the UI UX Pro Max "Dark Mode (OLED)" design system
    /// (developer-tool profile). Colour never carries meaning alone: every bar
    /// also prints its percentage.
    /// </summary>
    internal static class Theme
    {
        public static readonly Color Bg = Hex(0x0F172A);
        public static readonly Color Card = Hex(0x1B2336);
        public static readonly Color Muted = Hex(0x272F42);
        public static readonly Color Secondary = Hex(0x334155);
        public static readonly Color Border = Hex(0x475569);
        public static readonly Color Fg = Hex(0xF8FAFC);
        public static readonly Color FgMuted = Hex(0x94A3B8);
        public static readonly Color Green = Hex(0x22C55E);
        public static readonly Color Amber = Hex(0xF59E0B);
        public static readonly Color Red = Hex(0xEF4444);

        private static Color Hex(int v)
        {
            return Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
        }

        public static Color ForPct(double pct)
        {
            if (pct > 80.0) return Red;
            if (pct >= 60.0) return Amber;
            return Green;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Dark caption bar (Windows 10 20H1+ / 11). Silently ignored elsewhere.</summary>
        public static void DarkTitleBar(IntPtr hwnd)
        {
            try { int on = 1; DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, 4); }
            catch { }
        }

        /// <summary>Rounded window corners (Windows 11). Silently ignored elsewhere.</summary>
        public static void RoundCorners(IntPtr hwnd)
        {
            try { int round = 2; DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, 4); }
            catch { }
        }

        public static void StyleToolbar(Control panel)
        {
            panel.BackColor = Card;
            panel.ForeColor = Fg;
            foreach (Control c in panel.Controls)
            {
                Button b = c as Button;
                if (b != null)
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Border;
                    b.BackColor = Secondary;
                    b.Cursor = Cursors.Hand;
                }
                else if (c is TextBox)
                {
                    ((TextBox)c).BorderStyle = BorderStyle.FixedSingle;
                    c.BackColor = Muted;
                }
                else if (c is ComboBox)
                {
                    ((ComboBox)c).FlatStyle = FlatStyle.Flat; // Flat honours BackColor
                    c.BackColor = Muted;
                }
                else if (c is Label)
                {
                    c.ForeColor = FgMuted;
                }
            }
        }

        public static void StyleGrid(DataGridView g)
        {
            g.BackgroundColor = Bg;
            g.GridColor = Muted;
            g.EnableHeadersVisualStyles = false; // otherwise header colours are ignored
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            g.ColumnHeadersDefaultCellStyle.BackColor = Card;
            g.ColumnHeadersDefaultCellStyle.ForeColor = FgMuted;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Card;
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 6, 4, 6);
            g.DefaultCellStyle.BackColor = Bg;
            g.DefaultCellStyle.ForeColor = Fg;
            g.DefaultCellStyle.SelectionBackColor = Secondary;
            g.DefaultCellStyle.SelectionForeColor = Fg;
            g.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            g.AlternatingRowsDefaultCellStyle.BackColor = Hex(0x131C2F);
            g.RowTemplate.Height = 30;
        }
    }
}
