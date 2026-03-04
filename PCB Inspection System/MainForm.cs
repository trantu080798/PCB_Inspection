using AForge.Video;
using AForge.Video.DirectShow;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PCB_Inspection_System
{
    public partial class MainForm : Form
    {

        private Process? _pythonProcess;
        private FilterInfoCollection? _videoDevices;
        private VideoCaptureDevice? _videoSource;
        private RoundedPanel _logCard = null!;
        private Bitmap? _currentFrame;
        private readonly object _frameLock = new();
        private volatile bool _isDetecting;
        private int _displayFrameCounter;
        private bool _isClosing;

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        private long _totalOk = 0;
        private long _totalNg = 0;

        private readonly BindingList<DetectHistoryRow> _history = new();
        private DataGridView _gvHistory = null!;

        private readonly ConcurrentQueue<LogItem> _logQueue = new();
        private readonly System.Windows.Forms.Timer _logFlushTimer = new() { Interval = 80 };
        private RichTextBox _rtbLog = null!;

        private bool _lCtrlDown, _rCtrlDown, _ctrlComboFired;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private void UpdateCtrlStates()
        {
            _lCtrlDown = (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0;
            _rCtrlDown = (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;
        }

        private TableLayoutPanel _root = null!;

        private RoundedPanel _topBar = null!;
        private PictureBox _pbLogo = null!;
        private Label _lbProgText = null!;
        private ModernProgressBar _progTop = null!;

        private RoundedPanel _viewportCard = null!;
        private Panel _viewportHost = null!;
        private RoundedPanel _panelLive = null!;
        private RoundedPanel _panelResult = null!;
        private PictureBox _pbLive = null!;
        private PictureBox _pbResult = null!;
        private bool _detectedMode;


        private RoundedPanel _rightCard = null!;
        private ComboBox _cbModel = null!;
        private ModernButton _btnRestartServer = null!;
        private ModernButton _btnDetect = null!;
        private ModernButton _btnRefresh = null!;
        private Label _lbHistoryEmpty = null!;

        
        private Label _lbOk = null!;
        private Label _lbNg = null!;
        private Label _lbTotalOk = null!;
        private Label _lbTotalNg = null!;

      
        private System.Windows.Forms.Timer? _viewAnimTimer;
        private readonly Stopwatch _viewAnimSw = new();
        private Rectangle _liveFrom, _liveTo, _resFrom, _resTo;
        private bool _hideResultAfterAnim;

        public MainForm()
        {
            Text = "PCB_Inspection";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1200, 720);

            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 10f);

            WindowState = FormWindowState.Maximized;
            MaximizedBounds = Screen.FromControl(this).WorkingArea;

            DoubleBuffered = true;

            BuildUI();

            UiPerf.EnableDoubleBuffer(this);
            UiPerf.EnableDoubleBuffer(_viewportHost);
            UiPerf.EnableDoubleBuffer(_viewportCard);
            UiPerf.EnableDoubleBuffer(_rightCard);

          
            _logFlushTimer.Tick += (_, __) => FlushLogBatch(80);
            _logFlushTimer.Start();

     
            KeyPreview = true;
            KeyDown += MainForm_KeyDown;
            KeyUp += MainForm_KeyUp;

            Shown += async (_, __) =>
            {
                WindowState = FormWindowState.Maximized;
                MaximizedBounds = Screen.FromControl(this).WorkingArea;

                _detectedMode = false;
                ApplyViewportLayout(animated: false);

                LogInfo("App started. Mode: LIVE full.");
                SetTopProgress("Initializing...", indeterminate: true);

                StartCamera();
                LoadModels();

                await RestartServerAsync();
                SetTopProgress("Ready", indeterminate: false, value: 0);
            };

            Resize += (_, __) => ApplyViewportLayout(animated: false);
            FormClosing += OnFormClosing;
        }

   
        private async void MainForm_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.ControlKey) return;

            UpdateCtrlStates();
            if (_lCtrlDown && _rCtrlDown && !_ctrlComboFired)
            {
                _ctrlComboFired = true;
                e.Handled = true;

                if (!_isDetecting)
                    await DetectAsync();
            }
        }

        private void MainForm_KeyUp(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.ControlKey) return;

            UpdateCtrlStates();
            if (!(_lCtrlDown && _rCtrlDown))
                _ctrlComboFired = false;
        }

        private void BuildUI()
        {
            SuspendLayout();
            Controls.Clear();

            _root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Bg,
                Padding = new Padding(16),
                ColumnCount = 2,
                RowCount = 3,
                Margin = new Padding(0)
            };
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420)); 

            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 230));
            Controls.Add(_root);

            BuildTopBar();
            BuildViewport();
            BuildRightPanel();
            BuildLogPanel();

            ResumeLayout();
        }

        private void BuildTopBar()
        {
            _topBar = Card(Theme.Surface);
            _topBar.Padding = new Padding(16, 14, 16, 14);
            _root.Controls.Add(_topBar, 0, 0);
            _root.SetColumnSpan(_topBar, 2);

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
            _topBar.Controls.Add(grid);

            _pbLogo = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Image = LoadLogoSafe()
            };
            grid.Controls.Add(_pbLogo, 0, 0);

            var titleStack = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1,
                BackColor = Color.Transparent,
                Padding = new Padding(10, 2, 6, 0),
                Margin = new Padding(0)
            };
            titleStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            titleStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

            var lbTitle = new Label
            {
                Dock = DockStyle.Fill,
                Text = "PCB Inspection System",
                Font = new Font("Segoe UI Semibold", 16f),
                ForeColor = Theme.Text,
                TextAlign = ContentAlignment.MiddleLeft
            };
            var lbSub = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Live Camera • AI Detect • Industrial UI",
                ForeColor = Theme.Muted,
                TextAlign = ContentAlignment.MiddleLeft
            };
            titleStack.Controls.Add(lbTitle, 0, 0);
            titleStack.Controls.Add(lbSub, 0, 1);
            grid.Controls.Add(titleStack, 1, 0);

            var progWrap = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1,
                BackColor = Color.Transparent,
                Padding = new Padding(8, 4, 8, 0),
                Margin = new Padding(0)
            };
            progWrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            progWrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));

            _lbProgText = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Idle",
                ForeColor = Theme.Muted,
                TextAlign = ContentAlignment.MiddleRight
            };

            _progTop = new ModernProgressBar
            {
                Dock = DockStyle.Fill,
                Height = 10,
                CornerRadius = 6,
                TrackColor = Theme.Track,
                ProgressColor = Theme.Accent,
                Margin = new Padding(0, 2, 0, 0)
            };

            progWrap.Controls.Add(_lbProgText, 0, 0);
            progWrap.Controls.Add(_progTop, 0, 1);
            grid.Controls.Add(progWrap, 2, 0);
        }

        private void BuildViewport()
        {
            _viewportCard = Card(Theme.Surface);
            _viewportCard.Padding = new Padding(12);
            _root.Controls.Add(_viewportCard, 0, 1);

            _viewportHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            _viewportCard.Controls.Add(_viewportHost);

            _panelResult = ViewportPanel("RESULT (After Detect)", "Zoom • Pan (tuỳ bạn thêm)", out _pbResult);
            _panelLive = ViewportPanel("LIVE CAMERA", "Realtime stream", out _pbLive);

            _viewportHost.Controls.Add(_panelResult);
            _viewportHost.Controls.Add(_panelLive);

            _panelResult.Visible = false;
        }

    
        private void BuildRightPanel()
        {
            _rightCard = Card(Theme.Surface);
            _rightCard.Padding = new Padding(12);
            _root.Controls.Add(_rightCard, 1, 1);

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 10,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };

            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // model label
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));   // combo
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));   // spacer
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));   // restart
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));   // spacer
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));   // detect
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));   // spacer
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 122));  // current
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));   // spacer
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // combined fill

            _rightCard.Controls.Add(grid);

            grid.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "Model",
                ForeColor = Theme.Muted,
                TextAlign = ContentAlignment.BottomLeft
            }, 0, 0);

            _cbModel = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 10.5f),
                BackColor = Theme.InputBg,
                ForeColor = Theme.Text,
                Margin = new Padding(0)
            };
            _cbModel.SelectedIndexChanged += async (_, __) => await OnModelChangedAsync();
            grid.Controls.Add(_cbModel, 0, 1);

            _btnRestartServer = new ModernButton
            {
                Dock = DockStyle.Fill,
                Text = "Restart AI Server",
                BackColor = Theme.Btn,
                HoverBackColor = Theme.BtnHover,
                PressedBackColor = Theme.BtnPressed,
                ForeColor = Theme.Text
            };
            _btnRestartServer.Click += async (_, __) => await RestartServerAsync();
            grid.Controls.Add(_btnRestartServer, 0, 3);

            _btnDetect = new ModernButton
            {
                Dock = DockStyle.Fill,
                Text = "Detect",
                BackColor = Theme.Accent,
                HoverBackColor = Theme.AccentHover,
                PressedBackColor = Theme.AccentPressed,
                ForeColor = Color.White
            };
            _btnDetect.Click += async (_, __) => await DetectAsync();
            grid.Controls.Add(_btnDetect, 0, 5);

            // ===== CURRENT CARD =====
            var cardCurrent = Card(Theme.Surface2);
            cardCurrent.CornerRadius = 14;
            cardCurrent.Padding = new Padding(12);
            cardCurrent.Dock = DockStyle.Fill;

            var curWrap = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            curWrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            curWrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            curWrap.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "CURRENT (This Detect)",
                ForeColor = Theme.Muted,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);

            var curStats = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            curStats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            curStats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            var curOk = MetricChip("OK", Theme.Ok, out _lbOk);
            var curNg = MetricChip("NG", Theme.Ng, out _lbNg);
            curOk.Margin = new Padding(0, 0, 8, 0);
            curNg.Margin = new Padding(8, 0, 0, 0);

            curStats.Controls.Add(curOk, 0, 0);
            curStats.Controls.Add(curNg, 1, 0);

            curWrap.Controls.Add(curStats, 0, 1);
            cardCurrent.Controls.Add(curWrap);
            grid.Controls.Add(cardCurrent, 0, 7);

        
            var cardTotalHistory = Card(Theme.Surface2);
            cardTotalHistory.CornerRadius = 14;
            cardTotalHistory.Padding = new Padding(12);
            cardTotalHistory.Dock = DockStyle.Fill;

            var wrap = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   
            wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));   
            wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 5));  
            wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));    
            wrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   

            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));

            header.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "TOTAL (Checked Count)",
                ForeColor = Theme.Muted,
                Font = new Font("Segoe UI Semibold", 9.5f),
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);

            _btnRefresh = new ModernButton
            {
                Dock = DockStyle.Fill,
                Text = "Refresh",
                CornerRadius = 14,
                BackColor = Theme.Btn,
                HoverBackColor = Theme.BtnHover,
                PressedBackColor = Theme.BtnPressed,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI Semibold", 9f),
                Height = 28,
                Margin = new Padding(0, 3, 0, 3),
                Padding = new Padding(0)
            };
            _btnRefresh.Click += (_, __) => ResetTotalsAndHistory();
            header.Controls.Add(_btnRefresh, 1, 0);

            var chips = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            chips.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            chips.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            var totOk = MetricChip("Total OK", Theme.Ok, out _lbTotalOk);
            var totNg = MetricChip("Total NG", Theme.Ng, out _lbTotalNg);
            totOk.Margin = new Padding(0, 0, 8, 0);
            totNg.Margin = new Padding(8, 0, 0, 0);

            chips.Controls.Add(totOk, 0, 0);
            chips.Controls.Add(totNg, 1, 0);

            var divider = new Panel { Dock = DockStyle.Fill, Height = 1, BackColor = Theme.Border, Margin = new Padding(0) };

            var historyHost = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                FillColor = Theme.Surface,
                BorderColor = Theme.Border,
                BorderThickness = 1f,
                CornerRadius = 14,
                Padding = new Padding(6),
                Margin = new Padding(0, 8, 0, 0)
            };

            var overlay = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };

            _gvHistory = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToResizeColumns = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BorderStyle = BorderStyle.None,
                BackgroundColor = Theme.Surface,
                GridColor = Theme.Border,
                RowHeadersVisible = false,
                EnableHeadersVisualStyles = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            UiPerf.EnableDoubleBuffer(_gvHistory);
            ConfigureHistoryGrid(_gvHistory);
            _gvHistory.DataSource = _history;

            _lbHistoryEmpty = new Label
            {
                AutoSize = true,
                Text = "No history yet",
                ForeColor = Theme.Muted,
                Font = new Font("Segoe UI", 10f),
                BackColor = Color.Transparent
            };

            overlay.Controls.Add(_gvHistory);
            overlay.Controls.Add(_lbHistoryEmpty);
            _lbHistoryEmpty.BringToFront();

            overlay.Resize += (_, __) => PositionHistoryEmptyLabel();

            historyHost.Controls.Add(overlay);

            _history.ListChanged += (_, __) => UpdateHistoryEmptyState();
            UpdateHistoryEmptyState();

            wrap.Controls.Add(header, 0, 0);
            wrap.Controls.Add(chips, 0, 1);
            wrap.Controls.Add(new Panel { Dock = DockStyle.Fill }, 0, 2);
            wrap.Controls.Add(divider, 0, 3);
            wrap.Controls.Add(historyHost, 0, 4);

            cardTotalHistory.Controls.Add(wrap);
            grid.Controls.Add(cardTotalHistory, 0, 9);
        }

        
        private RoundedPanel MetricChip(string title, Color valueColor, out Label valueLabel)
        {
            var chip = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                FillColor = Theme.Surface,
                BorderColor = Theme.Border,
                BorderThickness = 1f,
                CornerRadius = 14,
                Padding = new Padding(12, 10, 12, 10)
            };

            var g = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            g.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            g.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = title,
                ForeColor = Theme.Muted,
                Font = new Font("Segoe UI Semibold", 9f),
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);

            valueLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "0",
                ForeColor = valueColor,
                Font = new Font("Segoe UI Semibold", 18f),
                TextAlign = ContentAlignment.MiddleLeft
            };
            g.Controls.Add(valueLabel, 0, 1);

            chip.Controls.Add(g);
            return chip;
        }

        private void UpdateHistoryEmptyState()
        {
            if (_lbHistoryEmpty == null) return;

            _lbHistoryEmpty.Visible = _history.Count == 0;
            if (_lbHistoryEmpty.Visible)
                PositionHistoryEmptyLabel();
        }

        private void PositionHistoryEmptyLabel()
        {
            if (_lbHistoryEmpty == null || _gvHistory == null) return;
            var host = _gvHistory.Parent;
            if (host == null) return;

            int headerH = _gvHistory.ColumnHeadersVisible ? _gvHistory.ColumnHeadersHeight : 0;

            int x = Math.Max(0, (host.ClientSize.Width - _lbHistoryEmpty.Width) / 2);
            int y = headerH + Math.Max(0, (host.ClientSize.Height - headerH - _lbHistoryEmpty.Height) / 2);

            _lbHistoryEmpty.Location = new Point(x, y);
        }

        private void ResetTotalsAndHistory()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(ResetTotalsAndHistory));
                return;
            }

            _totalOk = 0;
            _totalNg = 0;

            if (_lbTotalOk != null) _lbTotalOk.Text = "0";
            if (_lbTotalNg != null) _lbTotalNg.Text = "0";

            _history.Clear();
            UpdateHistoryEmptyState();

            LogInfo("Refresh: cleared TOTAL + HISTORY.");
        }

        private void BuildLogPanel()
        {
            _logCard = Card(Theme.Surface);
            _logCard.Padding = new Padding(12);

            _root.Controls.Add(_logCard, 0, 2);
            _root.SetColumnSpan(_logCard, 2);

            var header = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent };
            header.Controls.Add(new Label
            {
                Dock = DockStyle.Left,
                Width = 120,
                Text = "Log",
                Font = new Font("Segoe UI Semibold", 11f),
                ForeColor = Theme.Text,
                TextAlign = ContentAlignment.MiddleLeft
            });

            var wrap = Card(Theme.Surface2);
            wrap.Dock = DockStyle.Fill;
            wrap.CornerRadius = 14;
            wrap.Padding = new Padding(10);

            _rtbLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Surface2,
                ForeColor = Theme.Text,
                Font = new Font("Consolas", 9.5f),
                DetectUrls = false
            };

            wrap.Controls.Add(_rtbLog);
            _logCard.Controls.Add(wrap);
            _logCard.Controls.Add(header);
        }

        private RoundedPanel ViewportPanel(string title, string subtitle, out PictureBox pb)
        {
            var p = new RoundedPanel
            {
                FillColor = Theme.Surface2,
                BorderColor = Theme.Border,
                CornerRadius = 16,
                BorderThickness = 1f,
                Padding = new Padding(10),
                Dock = DockStyle.None,
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                Margin = new Padding(0)
            };

            UiPerf.EnableDoubleBuffer(p);

            var header = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.Transparent };

            header.Controls.Add(new Label
            {
                Dock = DockStyle.Left,
                Width = 260,
                Text = title,
                Font = new Font("Segoe UI Semibold", 10.5f),
                ForeColor = Theme.Text,
                TextAlign = ContentAlignment.MiddleLeft
            });

            header.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = subtitle,
                ForeColor = Theme.Muted,
                TextAlign = ContentAlignment.MiddleRight
            });

            pb = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.ViewportBg,
                SizeMode = PictureBoxSizeMode.Zoom
            };

            p.Controls.Add(pb);
            p.Controls.Add(header);
            return p;
        }

        private RoundedPanel Card(Color fill) => new RoundedPanel
        {
            FillColor = fill,
            BorderColor = Theme.Border,
            CornerRadius = 16,
            BorderThickness = 1f,
            Dock = DockStyle.Fill
        };


        private void ApplyViewportLayout(bool animated)
        {
            var bounds = _viewportHost.ClientRectangle;
            if (bounds.Width < 100 || bounds.Height < 100) return;

            const int gap = 12;

            Rectangle liveTarget;
            Rectangle resTarget;

            if (!_detectedMode)
            {
                liveTarget = bounds;
                resTarget = new Rectangle(bounds.Right + gap, bounds.Y, Math.Max(10, bounds.Width / 5), bounds.Height);
            }
            else
            {
                int rightW = Math.Clamp((int)(bounds.Width * 0.30), 280, 460);
                int leftW = bounds.Width - rightW - gap;
                leftW = Math.Max(520, leftW);

                if (leftW + rightW + gap > bounds.Width)
                {
                    rightW = Math.Clamp(bounds.Width - 520 - gap, 240, 420);
                    leftW = bounds.Width - rightW - gap;
                }

                resTarget = new Rectangle(bounds.X, bounds.Y, leftW, bounds.Height);
                liveTarget = new Rectangle(bounds.Right - rightW, bounds.Y, rightW, bounds.Height);
            }

            if (!animated)
            {
                _panelLive.Bounds = liveTarget;
                _panelResult.Bounds = resTarget;
                _panelResult.Visible = _detectedMode;

                _panelLive.BringToFront();
                _panelLive.RefreshRegionNow();
                _panelResult.RefreshRegionNow();
                return;
            }

            _panelResult.Visible = true;

            StartViewportAnimation(liveTarget, resTarget, hideResultAfter: !_detectedMode);
        }

        private void EnterDetectedMode()
        {
            _detectedMode = true;

            if (!_panelResult.Visible)
            {
                _panelResult.Visible = true;
                _panelResult.Bounds = new Rectangle(_panelLive.Right + 12, _panelLive.Top, Math.Max(10, _panelLive.Width / 4), _panelLive.Height);
            }

            ApplyViewportLayout(animated: true);
        }

        private void StartViewportAnimation(Rectangle liveTo, Rectangle resTo, bool hideResultAfter)
        {
            StopViewportAnimation();

            _hideResultAfterAnim = hideResultAfter;
            _liveFrom = _panelLive.Bounds;
            _resFrom = _panelResult.Bounds;
            _liveTo = liveTo;
            _resTo = resTo;

            _panelLive.SuppressRegionUpdate = true;
            _panelResult.SuppressRegionUpdate = true;

            _viewAnimSw.Restart();
            _viewAnimTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _viewAnimTimer.Tick += (_, __) =>
            {
                const int durationMs = 240;
                float t = (float)_viewAnimSw.ElapsedMilliseconds / durationMs;
                if (t >= 1f) t = 1f;

                float k = EaseOutCubic(t);

                _panelLive.Bounds = LerpRect(_liveFrom, _liveTo, k);
                _panelResult.Bounds = LerpRect(_resFrom, _resTo, k);

                _panelLive.BringToFront();

                if (t >= 1f)
                {
                    StopViewportAnimation();

                    if (_hideResultAfterAnim)
                        _panelResult.Visible = false;

                    _panelLive.SuppressRegionUpdate = false;
                    _panelResult.SuppressRegionUpdate = false;
                    _panelLive.RefreshRegionNow();
                    _panelResult.RefreshRegionNow();
                }
            };
            _viewAnimTimer.Start();
        }

        private void StopViewportAnimation()
        {
            if (_viewAnimTimer != null)
            {
                _viewAnimTimer.Stop();
                _viewAnimTimer.Dispose();
                _viewAnimTimer = null;
            }
        }

        private static float EaseOutCubic(float t) => 1f - (float)Math.Pow(1f - t, 3);

        private static Rectangle LerpRect(Rectangle a, Rectangle b, float t)
        {
            int x = a.X + (int)Math.Round((b.X - a.X) * t);
            int y = a.Y + (int)Math.Round((b.Y - a.Y) * t);
            int w = a.Width + (int)Math.Round((b.Width - a.Width) * t);
            int h = a.Height + (int)Math.Round((b.Height - a.Height) * t);
            return new Rectangle(x, y, w, h);
        }

        private void StartCamera()
        {
            try
            {
                _videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (_videoDevices.Count == 0)
                {
                    LogError("Không tìm thấy webcam.");
                    SetTopProgress("No camera", indeterminate: false, value: 0);
                    return;
                }

                _videoSource = new VideoCaptureDevice(_videoDevices[0].MonikerString);

                foreach (var cap in _videoSource.VideoCapabilities)
                {
                    if (cap.FrameSize.Width == 2560 && cap.FrameSize.Height == 1440)
                    {
                        _videoSource.VideoResolution = cap;
                        break;
                    }
                }

                _videoSource.NewFrame -= VideoSource_NewFrame;
                _videoSource.NewFrame += VideoSource_NewFrame;
                _videoSource.Start();

                LogOk("Camera started.");
            }
            catch (Exception ex)
            {
                LogError("StartCamera error: " + ex.Message);
            }
        }

        private void VideoSource_NewFrame(object sender, NewFrameEventArgs e)
        {
            Bitmap? raw = null;
            Bitmap? storeCopy = null;
            Bitmap? displayFrame = null;

            try
            {
                raw = (Bitmap)e.Frame.Clone();
                storeCopy = (Bitmap)raw.Clone();

                lock (_frameLock)
                {
                    var old = _currentFrame;
                    _currentFrame = storeCopy;
                    storeCopy = null;
                    old?.Dispose();
                }

                _displayFrameCounter++;
                if (_displayFrameCounter % 3 != 0) return;

                displayFrame = new Bitmap(640, 480, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(displayFrame))
                {
                    g.InterpolationMode = InterpolationMode.Low;
                    g.DrawImage(raw, 0, 0, 640, 480);
                }

                if (_isClosing || _pbLive.IsDisposed || !_pbLive.IsHandleCreated)
                {
                    displayFrame.Dispose();
                    displayFrame = null;
                    return;
                }

                _pbLive.BeginInvoke(new Action(() =>
                {
                    if (_pbLive.IsDisposed)
                    {
                        displayFrame?.Dispose();
                        return;
                    }

                    var oldImg = _pbLive.Image;
                    _pbLive.Image = displayFrame;
                    displayFrame = null;
                    oldImg?.Dispose();
                }));
            }
            catch (Exception ex)
            {
                LogError("NewFrame error: " + ex.Message);
                displayFrame?.Dispose();
                storeCopy?.Dispose();
            }
            finally
            {
                raw?.Dispose();
                storeCopy?.Dispose();
            }
        }

        private void StopCamera()
        {
            try
            {
                if (_videoSource != null)
                {
                    _videoSource.NewFrame -= VideoSource_NewFrame;
                    if (_videoSource.IsRunning)
                    {
                        _videoSource.SignalToStop();
                        _videoSource.WaitForStop();
                    }
                    _videoSource = null;
                }

                lock (_frameLock)
                {
                    _currentFrame?.Dispose();
                    _currentFrame = null;
                }

                _pbLive.Image?.Dispose();
                _pbLive.Image = null;

                LogInfo("Camera stopped.");
            }
            catch (Exception ex)
            {
                LogError("StopCamera error: " + ex.Message);
            }
        }

        private void StopPythonServer()
        {
            try
            {
                if (_pythonProcess != null && !_pythonProcess.HasExited)
                {
                    _pythonProcess.Kill(true);
                    _pythonProcess.Dispose();
                }
            }
            catch { }
            finally
            {
                _pythonProcess = null;
            }
        }

        private async Task RestartServerAsync()
        {
            try
            {
                DisableRightControls(true);

                SetTopProgress("Restarting server...", indeterminate: true);
                LogInfo("Restart AI server...");

                StopPythonServer();

                var app = AppDomain.CurrentDomain.BaseDirectory;
                var serverPy = Path.Combine(app, "AI_Server", "server.py");
                var pythonExe = "py";

                if (!File.Exists(serverPy))
                {
                    LogError("Không thấy AI_Server/server.py");
                    SetTopProgress("server.py missing", indeterminate: false, value: 0);
                    return;
                }

                _pythonProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = pythonExe,
                        Arguments = $"-3.11 \"{serverPy}\"",
                        WorkingDirectory = Path.GetDirectoryName(serverPy),
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                _pythonProcess.Start();

                await Task.Delay(1500);

                SetTopProgress("Server ready", indeterminate: false, value: 0);
                LogOk("Server restarted.");
            }
            catch (Exception ex)
            {
                SetTopProgress("Restart error", indeterminate: false, value: 0);
                LogError("RestartServer error: " + ex.Message);
            }
            finally
            {
                DisableRightControls(false);
            }
        }

        private void LoadModels()
        {
            try
            {
                var app = AppDomain.CurrentDomain.BaseDirectory;
                var dataDir = Path.Combine(app, "Data");
                if (!Directory.Exists(dataDir))
                {
                    LogError("Không thấy folder Data.");
                    return;
                }

                var folders = Directory.GetDirectories(dataDir)
                    .Select(Path.GetFileName)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToArray();

                _cbModel.Items.Clear();
                _cbModel.Items.AddRange(folders);

                if (_cbModel.Items.Count > 0)
                    _cbModel.SelectedIndex = 0;

                LogInfo($"Loaded models: {folders.Length}");
            }
            catch (Exception ex)
            {
                LogError("LoadModels error: " + ex.Message);
            }
        }

        private async Task OnModelChangedAsync()
        {
            if (_cbModel.SelectedItem == null) return;

            string selectedFolder = _cbModel.SelectedItem.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(selectedFolder)) return;

            string app = AppDomain.CurrentDomain.BaseDirectory;
            string source = Path.Combine(app, "Data", selectedFolder, "best.pt");
            string targetDir = Path.Combine(app, "AI_Server");
            string target = Path.Combine(targetDir, "best.pt");

            try
            {
                if (!File.Exists(source))
                {
                    LogError($"Không tìm thấy best.pt trong: {selectedFolder}");
                    return;
                }

                Directory.CreateDirectory(targetDir);
                File.Copy(source, target, true);

                SetTopProgress($"Reloading model: {selectedFolder}...", indeterminate: true);
                LogInfo($"Copied best.pt from {selectedFolder} → AI_Server");

                using var resp = await _http.PostAsync("http://127.0.0.1:8000/reload_model", null);
                if (resp.IsSuccessStatusCode)
                {
                    SetTopProgress($"Model set: {selectedFolder}", indeterminate: false, value: 0);
                    LogOk($"Model updated: {selectedFolder}");
                }
                else
                {
                    SetTopProgress("Reload model error", indeterminate: false, value: 0);
                    LogError("reload_model failed: " + resp.StatusCode);
                }
            }
            catch (Exception ex)
            {
                SetTopProgress("Model error", indeterminate: false, value: 0);
                LogError("Model switch error: " + ex.Message);
            }
        }

    
        private async Task DetectAsync()
        {
            if (_isDetecting)
            {
                LogInfo("Detect đang chạy, vui lòng đợi...");
                return;
            }

            Bitmap? safeFrame = null;
            lock (_frameLock)
            {
                if (_currentFrame != null)
                    safeFrame = CopyBitmap(_currentFrame);
            }

            if (safeFrame == null)
            {
                LogError("Chưa có frame từ camera.");
                return;
            }

            DisableRightControls(true);

            var modelName = _cbModel.SelectedItem?.ToString() ?? "-";
            var ts = DateTime.Now;
            var sw = Stopwatch.StartNew();

            try
            {
                _isDetecting = true;

                EnterDetectedMode();
                SetTopProgress("Detecting...", indeterminate: true);
                LogInfo($"Detect started. Model={modelName}");

                var result = await DetectFromBitmapAsync(safeFrame);

                if (result == null)
                {
                    SetTopProgress("Detect finished (no result)", indeterminate: false, value: 0);
                    LogError("Detect trả về rỗng / image_base64 empty.");
                    return;
                }

            
                _lbOk.Text = result.ok_count.ToString();
                _lbNg.Text = result.ng_count.ToString();

                _totalOk += result.ok_count;
                _totalNg += result.ng_count;
                _lbTotalOk.Text = _totalOk.ToString();
                _lbTotalNg.Text = _totalNg.ToString();

                sw.Stop();

                AddHistoryRow(new DetectHistoryRow
                {
                    Timestamp = ts,
                    Model = modelName,
                    OK = result.ok_count,
                    NG = result.ng_count,
                });

                SetTopProgress($"Done • OK {result.ok_count} • NG {result.ng_count}", indeterminate: false, value: 100);
                LogOk($"Detect done. Model={modelName} OK={result.ok_count} NG={result.ng_count} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                SetTopProgress("Detect error", indeterminate: false, value: 0);
                LogError(ex.Message);
            }
            finally
            {
                _isDetecting = false;
                DisableRightControls(false);
                safeFrame.Dispose();
            }
        }

        private void AddHistoryRow(DetectHistoryRow row)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => AddHistoryRow(row)));
                return;
            }

            _history.Insert(0, row);

            if (_history.Count > 500)
                _history.RemoveAt(_history.Count - 1);

            UpdateHistoryEmptyState();
        }

        private static Bitmap CopyBitmap(Bitmap src)
        {
            var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(dst);
            g.DrawImage(src, 0, 0, src.Width, src.Height);
            return dst;
        }

        private async Task<DetectResult?> DetectFromBitmapAsync(Bitmap frame)
        {
            using var ms = new MemoryStream();
            frame.Save(ms, ImageFormat.Jpeg);
            ms.Position = 0;

            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(ms.ToArray()), "file", "frame.jpg");

            using var resp = await _http.PostAsync("http://127.0.0.1:8000/detect", form);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                LogError("Detect HTTP error: " + resp.StatusCode);
                return null;
            }

            var result = JsonSerializer.Deserialize<DetectResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result == null) return null;
            if (string.IsNullOrWhiteSpace(result.image_base64)) return null;

            byte[] bytes = Convert.FromBase64String(result.image_base64);
            using var ms2 = new MemoryStream(bytes);

            using var tmp = new Bitmap(ms2);
            var oldImg = _pbResult.Image;
            _pbResult.Image = new Bitmap(tmp); // ✅ clone
            oldImg?.Dispose();

            return result;
        }

        private void DisableRightControls(bool busy)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => DisableRightControls(busy)));
                return;
            }

            _btnDetect.Enabled = !busy;
            _btnRestartServer.Enabled = !busy;
            _cbModel.Enabled = !busy;
            if (_btnRefresh != null) _btnRefresh.Enabled = !busy;
        }

        private void SetTopProgress(string text, bool indeterminate, int value = 0)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetTopProgress(text, indeterminate, value)));
                return;
            }

            _lbProgText.Text = text;
            _progTop.Indeterminate = indeterminate;
            _progTop.Value = indeterminate ? 0 : Math.Clamp(value, 0, 100);
        }

        private void LogInfo(string msg) => EnqueueLog("INFO", msg, Theme.LogInfo);
        private void LogOk(string msg) => EnqueueLog("OK", msg, Theme.LogOk);
        private void LogError(string msg) => EnqueueLog("ERR", msg, Theme.LogErr);

        private void EnqueueLog(string tag, string msg, Color color)
        {
            _logQueue.Enqueue(new LogItem(tag, msg, color));
        }

        private void FlushLogBatch(int maxLines)
        {
            if (_rtbLog.IsDisposed || !_rtbLog.IsHandleCreated) return;
            if (_logQueue.IsEmpty) return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => FlushLogBatch(maxLines)));
                return;
            }

            int count = 0;
            while (count < maxLines && _logQueue.TryDequeue(out var item))
            {
                var line = $"{DateTime.Now:HH:mm:ss} [{item.Tag}] {item.Message}\n";
                _rtbLog.SelectionStart = _rtbLog.TextLength;
                _rtbLog.SelectionLength = 0;
                _rtbLog.SelectionColor = item.Color;
                _rtbLog.AppendText(line);
                _rtbLog.SelectionColor = Theme.Text;
                count++;
            }
            _rtbLog.ScrollToCaret();
        }

        private readonly record struct LogItem(string Tag, string Message, Color Color);

        private async void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            _isClosing = true;

            StopViewportAnimation();
            _logFlushTimer.Stop();

            StopCamera();
            StopPythonServer();

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                await client.PostAsync("http://127.0.0.1:8000/shutdown", null);
            }
            catch { }
        }

        private Image? LoadLogoSafe()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcnex", "Logo.png");
                if (!File.Exists(path)) return null;

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                ms.Position = 0;

                using var img = Image.FromStream(ms);
                return (Image)img.Clone();
            }
            catch { return null; }
        }

        public sealed class DetectResult
        {
            public int ok_count { get; set; }
            public int ng_count { get; set; }
            public string image_base64 { get; set; } = "";
        }

        public sealed class DetectHistoryRow
        {
            public DateTime Timestamp { get; set; }
            public string Model { get; set; } = "";
            public int OK { get; set; }
            public int NG { get; set; }
        }

        private void ConfigureHistoryGrid(DataGridView gv)
        {
            gv.SuspendLayout();

            gv.AutoGenerateColumns = false;
            gv.Columns.Clear();

            gv.ReadOnly = true;
            gv.AllowUserToAddRows = false;
            gv.AllowUserToDeleteRows = false;
            gv.AllowUserToResizeRows = false;
            gv.AllowUserToResizeColumns = false;
            gv.MultiSelect = false;
            gv.SelectionMode = DataGridViewSelectionMode.FullRowSelect;

            gv.RowHeadersVisible = false;
            gv.BorderStyle = BorderStyle.None;
            gv.BackgroundColor = Theme.Surface;

            gv.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            gv.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            gv.GridColor = Theme.Border;

            gv.EnableHeadersVisualStyles = false;
            gv.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            gv.ColumnHeadersHeight = 30;

            gv.RowTemplate.Height = 30;
            gv.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

            gv.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Theme.Track,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI Semibold", 9.5f),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 8, 0),
                WrapMode = DataGridViewTriState.False
            };

            gv.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 9.5f),
                SelectionBackColor = Color.FromArgb(220, 235, 255),
                SelectionForeColor = Theme.Text,
                Padding = new Padding(8, 0, 8, 0),
                WrapMode = DataGridViewTriState.False
            };

            gv.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(244, 246, 250),
                ForeColor = Theme.Text,
                SelectionBackColor = Color.FromArgb(220, 235, 255),
                SelectionForeColor = Theme.Text,
                Padding = new Padding(8, 0, 8, 0)
            };

            gv.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(DetectHistoryRow.Timestamp),
                HeaderText = "Time",
                FillWeight = 42,
                DefaultCellStyle = new DataGridViewCellStyle { Format = "HH:mm:ss", Padding = new Padding(8, 0, 8, 0) }
            });

            gv.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(DetectHistoryRow.Model),
                HeaderText = "Model",
                FillWeight = 30
            });

            gv.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(DetectHistoryRow.OK),
                HeaderText = "OK",
                FillWeight = 14,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    ForeColor = Theme.Ok,
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Padding = new Padding(8, 0, 8, 0)
                }
            });

            gv.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(DetectHistoryRow.NG),
                HeaderText = "NG",
                FillWeight = 14,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    ForeColor = Theme.Ng,
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Padding = new Padding(8, 0, 8, 0)
                }
            });

            gv.ResumeLayout();
        }

        private static class Theme
        {
            public static readonly Color Bg = Color.FromArgb(245, 246, 248);
            public static readonly Color Surface = Color.White;
            public static readonly Color Surface2 = Color.FromArgb(248, 249, 251);

            public static readonly Color Border = Color.FromArgb(220, 226, 235);
            public static readonly Color Shadow = Color.FromArgb(18, 0, 0, 0);

            public static readonly Color Text = Color.FromArgb(25, 33, 45);
            public static readonly Color Muted = Color.FromArgb(92, 105, 125);

            public static readonly Color Accent = Color.FromArgb(0, 122, 255);
            public static readonly Color AccentHover = Color.FromArgb(30, 142, 255);
            public static readonly Color AccentPressed = Color.FromArgb(0, 105, 220);

            public static readonly Color Btn = Color.FromArgb(243, 245, 248);
            public static readonly Color BtnHover = Color.FromArgb(235, 239, 245);
            public static readonly Color BtnPressed = Color.FromArgb(226, 233, 242);

            public static readonly Color InputBg = Color.White;

            public static readonly Color Track = Color.FromArgb(228, 234, 242);
            public static readonly Color ViewportBg = Color.FromArgb(245, 247, 250);

            public static readonly Color Ok = Color.FromArgb(0, 140, 90);
            public static readonly Color Ng = Color.FromArgb(220, 50, 70);

            public static readonly Color LogInfo = Color.FromArgb(70, 80, 95);
            public static readonly Color LogOk = Color.FromArgb(0, 130, 85);
            public static readonly Color LogErr = Color.FromArgb(200, 40, 55);
        }

        private static class UiPerf
        {
            public static void EnableDoubleBuffer(Control c)
            {
                if (c is null) return;

                try
                {
                    typeof(Control)
                        .GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.SetValue(c, true, null);

                    typeof(Control)
                        .GetMethod("SetStyle", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.Invoke(c, new object[]
                        {
                            ControlStyles.AllPaintingInWmPaint |
                            ControlStyles.UserPaint |
                            ControlStyles.OptimizedDoubleBuffer,
                            true
                        });

                    typeof(Control)
                        .GetMethod("UpdateStyles", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?.Invoke(c, null);
                }
                catch { }
            }
        }

        private sealed class RoundedPanel : Panel
        {
            public int CornerRadius { get; set; } = 16;
            public Color FillColor { get; set; } = Theme.Surface;
            public Color BorderColor { get; set; } = Theme.Border;
            public float BorderThickness { get; set; } = 1f;

            public bool SuppressRegionUpdate { get; set; } = false;

            private GraphicsPath? _cachedPath;
            private Rectangle _cachedRect;

            public RoundedPanel()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                BackColor = Color.Transparent;
                Margin = new Padding(0);
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                if (SuppressRegionUpdate) return;
                RefreshRegionNow();
            }

            public void RefreshRegionNow()
            {
                if (Width <= 2 || Height <= 2) return;

                var rect = new Rectangle(0, 0, Width, Height);
                if (rect == _cachedRect && _cachedPath != null) return;

                _cachedRect = rect;
                _cachedPath?.Dispose();
                _cachedPath = RoundRect(new Rectangle(0, 0, Width, Height), CornerRadius);
                Region = new Region(_cachedPath);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                if (rect.Width <= 2 || rect.Height <= 2) return;

                using (var sh = RoundRect(new Rectangle(2, 3, rect.Width - 2, rect.Height - 2), CornerRadius))
                using (var shBrush = new SolidBrush(Theme.Shadow))
                    e.Graphics.FillPath(shBrush, sh);

                using var path = RoundRect(rect, CornerRadius);

                var bottom = Color.FromArgb(
                    Math.Max(0, FillColor.R - 4),
                    Math.Max(0, FillColor.G - 4),
                    Math.Max(0, FillColor.B - 6));

                using (var br = new LinearGradientBrush(rect, FillColor, bottom, LinearGradientMode.Vertical))
                    e.Graphics.FillPath(br, path);

                using var pen = new Pen(Color.FromArgb(220, BorderColor), BorderThickness);
                e.Graphics.DrawPath(pen, path);
            }

            private static GraphicsPath RoundRect(Rectangle r, int radius)
            {
                var path = new GraphicsPath();
                int rr = Math.Max(0, Math.Min(radius, Math.Min(r.Width / 2, r.Height / 2)));
                int d = rr * 2;

                if (rr <= 0)
                {
                    path.AddRectangle(r);
                    path.CloseFigure();
                    return path;
                }

                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }

        private sealed class ModernButton : Button
        {
            public int CornerRadius { get; set; } = 14;
            public Color HoverBackColor { get; set; } = Theme.BtnHover;
            public Color PressedBackColor { get; set; } = Theme.BtnPressed;

            private bool _hover, _down;
            private Color _normal;

            public ModernButton()
            {
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                Height = 44;
                Cursor = Cursors.Hand;
                Padding = new Padding(12, 8, 12, 8);
                Margin = new Padding(0);

                _normal = BackColor;

                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; _down = false; Invalidate(); };
                MouseDown += (_, __) => { _down = true; Invalidate(); };
                MouseUp += (_, __) => { _down = false; Invalidate(); };
            }

            protected override void OnBackColorChanged(EventArgs e)
            {
                base.OnBackColorChanged(e);
                _normal = BackColor;
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                using var path = new GraphicsPath();
                int r = CornerRadius * 2;
                path.AddArc(0, 0, r, r, 180, 90);
                path.AddArc(Width - r, 0, r, r, 270, 90);
                path.AddArc(Width - r, Height - r, r, r, 0, 90);
                path.AddArc(0, Height - r, r, r, 90, 90);
                path.CloseFigure();
                Region = new Region(path);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                var bg = _normal;
                if (_down) bg = PressedBackColor;
                else if (_hover) bg = HoverBackColor;

                using var br = new SolidBrush(bg);
                e.Graphics.FillRectangle(br, ClientRectangle);

                using var pen = new Pen(Theme.Border, 1f);
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        private sealed class ModernProgressBar : Control
        {
            public int Minimum { get; set; } = 0;
            public int Maximum { get; set; } = 100;

            private int _value;
            public int Value
            {
                get => _value;
                set { _value = Math.Clamp(value, Minimum, Maximum); Invalidate(); }
            }

            public bool Indeterminate
            {
                get => _indeterminate;
                set
                {
                    _indeterminate = value;
                    if (_indeterminate) _timer.Start(); else _timer.Stop();
                    Invalidate();
                }
            }

            public int CornerRadius { get; set; } = 6;
            public Color TrackColor { get; set; } = Theme.Track;
            public Color ProgressColor { get; set; } = Theme.Accent;

            private bool _indeterminate;
            private readonly System.Windows.Forms.Timer _timer;
            private int _phase;

            public ModernProgressBar()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                Height = 10;
                Margin = new Padding(0);

                _timer = new System.Windows.Forms.Timer { Interval = 30 };
                _timer.Tick += (_, __) =>
                {
                    _phase = (_phase + 10) % Math.Max(1, Width + 120);
                    Invalidate();
                };
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                if (rect.Width <= 2 || rect.Height <= 2) return;

                using var trackPath = RoundRect(rect, CornerRadius);
                using var trackBrush = new SolidBrush(TrackColor);
                e.Graphics.FillPath(trackBrush, trackPath);

                if (Indeterminate)
                {
                    int segW = Math.Max(24, (int)(Width * 0.24));
                    int x = _phase - segW;
                    var segRect = new Rectangle(x, rect.Y, segW, rect.Height);
                    using var segPath = RoundRect(segRect, CornerRadius);
                    using var segBrush = new SolidBrush(ProgressColor);
                    e.Graphics.FillPath(segBrush, segPath);
                    return;
                }

                float pct = (Maximum <= Minimum) ? 0 : (float)(Value - Minimum) / (Maximum - Minimum);
                int fillW = (int)Math.Round(rect.Width * pct);
                if (fillW <= 0) return;

                var fillRect = new Rectangle(rect.X, rect.Y, fillW, rect.Height);
                using var fillPath = RoundRect(fillRect, CornerRadius);
                using var fillBrush = new SolidBrush(ProgressColor);
                e.Graphics.FillPath(fillBrush, fillPath);
            }

            private static GraphicsPath RoundRect(Rectangle r, int radius)
            {
                var path = new GraphicsPath();
                if (r.Width <= 0 || r.Height <= 0) return path;

                int rr = Math.Min(radius, Math.Min(r.Width / 2, r.Height / 2));
                int d = rr * 2;

                if (rr <= 0)
                {
                    path.AddRectangle(r);
                    path.CloseFigure();
                    return path;
                }

                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }
    }
}