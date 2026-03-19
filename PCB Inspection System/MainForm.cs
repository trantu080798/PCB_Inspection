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

        private long _totalOk;
        private long _totalNg;

        private readonly BindingList<DetectHistoryRow> _history = new();
        private DataGridView _gvHistory = null!;

        private readonly ConcurrentQueue<LogItem> _logQueue = new();
        private readonly System.Windows.Forms.Timer _logFlushTimer = new() { Interval = 80 };
        private RichTextBox _rtbLog = null!;

        private bool _lCtrlDown, _rCtrlDown, _ctrlComboFired;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;

        private Panel _shell = null!;
        private Panel _titleBar = null!;
        private Label _lbWindowTitle = null!;
        private ModernButton _btnWinMin = null!;
        private ModernButton _btnWinMax = null!;
        private ModernButton _btnWinClose = null!;

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

        // --- Thêm biến cho 2 nút mới ---
        private ModernButton _btnCapture = null!;
        private ModernButton _btnCollect = null!;

        private ModernButton _btnRefresh = null!;
        private Label _lbHistoryEmpty = null!;

        private Label _lbOk = null!;
        private Label _lbNg = null!;
        private Label _lbTotalOk = null!;
        private Label _lbTotalNg = null!;
        private DetectResult Result = null;
        private DetectResultLR ResultLF = null;

        private System.Windows.Forms.Timer? _viewAnimTimer;
        private readonly Stopwatch _viewAnimSw = new();
        private Rectangle _liveFrom, _liveTo, _resFrom, _resTo;
        private bool _hideResultAfterAnim;
        private int total_objects_detected;
        private bool isLRimage = false;
        private int roiW = 1920;
        private int roiH = 1080;
        private int startX;   // 320
        private int startY;   // 180

        Rectangle roi = new Rectangle();
        private int cameraDevice = 0;
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        public MainForm()
        {
            Text = "PCB_Inspection";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1280, 760);

            FormBorderStyle = FormBorderStyle.None;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            DoubleBuffered = true;
            KeyPreview = true;

            BackColor = Theme.AppBorder;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 10f);

            WindowState = FormWindowState.Maximized;
            MaximizedBounds = Screen.FromControl(this).WorkingArea;

            BuildUI();

            UiPerf.EnableDoubleBuffer(this);
            UiPerf.EnableDoubleBuffer(_shell);
            UiPerf.EnableDoubleBuffer(_root);
            UiPerf.EnableDoubleBuffer(_viewportHost);
            UiPerf.EnableDoubleBuffer(_viewportCard);
            UiPerf.EnableDoubleBuffer(_rightCard);

            _logFlushTimer.Tick += (_, __) => FlushLogBatch(80);
            _logFlushTimer.Start();

            KeyDown += MainForm_KeyDown;
            KeyUp += MainForm_KeyUp;
            Resize += (_, __) =>
            {
                ApplyViewportLayout(animated: false);
                UpdateWindowButtons();
            };
            FormClosing += OnFormClosing;

            Shown += async (_, __) =>
            {
                WindowState = FormWindowState.Maximized;
                MaximizedBounds = Screen.FromControl(this).WorkingArea;
                UpdateWindowButtons();

                _detectedMode = false;
                ApplyViewportLayout(animated: false);

                LogInfo("App started. Mode: LIVE full.");
                SetTopProgress("Initializing...", indeterminate: true);

                StartCamera();
                LoadModels();

                await RestartServerAsync();
                SetTopProgress("Ready", indeterminate: false, value: 0);
            };
        }
        private void MainForm_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.ControlKey) return;
            UpdateCtrlStates();
            if (_lCtrlDown && _rCtrlDown && !_ctrlComboFired)
            {
                _ctrlComboFired = true;
                e.Handled = true;
                _ = RunDetectShortcutAsync();
            }
        }

        private void WireDragMove(Control c)
        {
            c.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            };

            c.DoubleClick += (_, __) => ToggleMaximize();
        }

        private async Task RunDetectShortcutAsync()
        {
            if (!_isDetecting)
                if (isLRimage)
                    await DetectLRAsync();
                else
                    await DetectAsync();
        }

        private void MainForm_KeyUp(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.ControlKey) return;
            UpdateCtrlStates();
            if (!(_lCtrlDown && _rCtrlDown))
                _ctrlComboFired = false;
        }

        private void UpdateCtrlStates()
        {
            _lCtrlDown = (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0;
            _rCtrlDown = (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;
        }
        private void BuildUI()
        {
            SuspendLayout();
            Controls.Clear();

            _shell = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Bg,
                Padding = new Padding(1)
            };
            Controls.Add(_shell);

            BuildWindowChrome();
            BuildContent();

            ResumeLayout(true);
        }

        private void BuildWindowChrome()
        {
            _titleBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 0,
                BackColor = Theme.WindowEdge,
                Padding = new Padding(0),
                Visible = false
            };
            _shell.Controls.Add(_titleBar);
        }

        private void BuildContent()
        {
            _root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Bg,
                Padding = new Padding(16, 12, 16, 14),
                ColumnCount = 2,
                RowCount = 3,
                Margin = new Padding(0)
            };
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400f)); // TĂNG TỪ 360 LÊN 400 ĐỂ CHỐNG LẸM
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78f));
            _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120f));
            _shell.Controls.Add(_root);
            _root.BringToFront();

            BuildTopBar();
            BuildViewport();
            BuildRightPanel();
            BuildLogPanel();

            _root.Resize += (_, __) => UpdateResponsiveLayout();
            UpdateResponsiveLayout();
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

            _panelResult = ViewportPanel("RESULT", "AI annotated image", out _pbResult);
            _panelLive = ViewportPanel("LIVE CAMERA", "Realtime stream", out _pbLive);

            _viewportHost.Controls.Add(_panelResult);
            _viewportHost.Controls.Add(_panelLive);
            _panelResult.Visible = false;
        }
        private RoundedPanel ViewportPanel(string title, string subtitle, out PictureBox pictureBox)
        {
            var card = new RoundedPanel
            {
                Dock = DockStyle.None,
                FillColor = Theme.Surface2,
                BorderColor = Theme.Border,
                BorderThickness = 1f,
                CornerRadius = 18,
                Padding = new Padding(12),
                Margin = new Padding(0)
            };

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var lbTitle = new Label
            {
                Dock = DockStyle.Fill,
                Text = title,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI Semibold", 10f),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Margin = new Padding(0)
            };

            var lbSub = new Label
            {
                Dock = DockStyle.Fill,
                Text = subtitle,
                ForeColor = Theme.Muted,
                Font = new Font("Segoe UI", 8.5f),
                TextAlign = ContentAlignment.MiddleRight,
                AutoEllipsis = true,
                Margin = new Padding(0)
            };

            header.Controls.Add(lbTitle, 0, 0);
            header.Controls.Add(lbSub, 1, 0);

            var frame = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                FillColor = Theme.Surface,
                BorderColor = Theme.Border,
                BorderThickness = 1f,
                CornerRadius = 14,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };

            pictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Surface,
                SizeMode = PictureBoxSizeMode.Zoom,
                Margin = new Padding(0)
            };

            frame.Controls.Add(pictureBox);

            root.Controls.Add(header, 0, 0);
            root.Controls.Add(frame, 0, 1);
            card.Controls.Add(root);

            return card;
        }

        private void BuildTopBar()
        {
            _topBar = Card(Theme.Surface);
            _topBar.CornerRadius = 18;
            _topBar.Padding = new Padding(16, 12, 16, 12);
            _topBar.Margin = new Padding(0);
            _root.Controls.Add(_topBar, 0, 0);
            _root.SetColumnSpan(_topBar, 2);

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132f));
            _topBar.Controls.Add(grid);

            var brandWrap = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            brandWrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            brandWrap.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60f));
            brandWrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            var logoHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 12, 0),
                Padding = new Padding(0)
            };

            _pbLogo = new PictureBox
            {
                Size = new Size(42, 24),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Anchor = AnchorStyles.None,
                Image = LoadLogoSafe()
            };
            logoHost.Controls.Add(_pbLogo);
            logoHost.Resize += (_, __) =>
            {
                _pbLogo.Left = Math.Max(0, (logoHost.ClientSize.Width - _pbLogo.Width) / 2);
                _pbLogo.Top = Math.Max(0, (logoHost.ClientSize.Height - _pbLogo.Height) / 2);
            };

            var titleWrap = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0, 1, 0, 0)
            };
            titleWrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
            titleWrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 18f));

            titleWrap.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "PCB Inspection System",
                Font = new Font("Segoe UI Semibold", 14f),
                ForeColor = Theme.Text,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Margin = new Padding(0)
            }, 0, 0);

            titleWrap.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "Realtime camera inspection · AI detection",
                ForeColor = Theme.Muted,
                Font = new Font("Segoe UI", 8.8f),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Margin = new Padding(0)
            }, 0, 1);

            brandWrap.Controls.Add(logoHost, 0, 0);
            brandWrap.Controls.Add(titleWrap, 1, 0);

            var statusWrap = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                FillColor = Theme.Surface2,
                BorderColor = Theme.Border,
                BorderThickness = 1f,
                CornerRadius = 14,
                Padding = new Padding(12, 8, 12, 8),
                Margin = new Padding(8, 0, 8, 0)
            };

            var statusGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            statusGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 11f));
            statusGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 18f));
            statusGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 8f));

            statusGrid.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "SYSTEM STATUS",
                ForeColor = Theme.Muted,
                Font = new Font("Segoe UI Semibold", 7.3f),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Margin = new Padding(0)
            }, 0, 0);

            _lbProgText = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Ready",
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI Semibold", 9f),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Margin = new Padding(0)
            };
            statusGrid.Controls.Add(_lbProgText, 0, 1);

            _progTop = new ModernProgressBar
            {
                Dock = DockStyle.Fill,
                Height = 8,
                CornerRadius = 4,
                TrackColor = Theme.Track,
                ProgressColor = Theme.Accent,
                Margin = new Padding(0)
            };
            statusGrid.Controls.Add(_progTop, 0, 2);
            statusWrap.Controls.Add(statusGrid);

            var buttonWrap = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                FillColor = Theme.Surface2,
                BorderColor = Theme.Border,
                BorderThickness = 1f,
                CornerRadius = 14,
                Padding = new Padding(8),
                Margin = new Padding(0)
            };

            var buttonGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 5,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            buttonGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            buttonGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32f));
            buttonGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6f));
            buttonGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32f));
            buttonGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6f));
            buttonGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32f));

            _btnWinMin = new ModernButton
            {
                Text = "–",
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                BackColor = Theme.ChromeBtn,
                HoverBackColor = Theme.ChromeBtnHover,
                PressedBackColor = Theme.ChromeBtnPressed,
                ForeColor = Theme.Text,
                CornerRadius = 10,
                Font = new Font("Segoe UI Semibold", 8.5f),
                Padding = new Padding(0)
            };
            _btnWinMin.Click += (_, __) => WindowState = FormWindowState.Minimized;

            _btnWinMax = new ModernButton
            {
                Text = "□",
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                BackColor = Theme.ChromeBtn,
                HoverBackColor = Theme.ChromeBtnHover,
                PressedBackColor = Theme.ChromeBtnPressed,
                ForeColor = Theme.Text,
                CornerRadius = 10,
                Font = new Font("Segoe UI Symbol", 8f, FontStyle.Bold),
                Padding = new Padding(0)
            };
            _btnWinMax.Click += (_, __) => ToggleMaximize();

            _btnWinClose = new ModernButton
            {
                Text = "✕",
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                BackColor = Theme.CloseBtn,
                HoverBackColor = Theme.CloseBtnHover,
                PressedBackColor = Theme.CloseBtnPressed,
                ForeColor = Color.White,
                CornerRadius = 10,
                Font = new Font("Segoe UI Symbol", 8f, FontStyle.Bold),
                Padding = new Padding(0)
            };
            _btnWinClose.Click += (_, __) => Close();

            buttonGrid.Controls.Add(_btnWinMin, 0, 0);
            buttonGrid.Controls.Add(_btnWinMax, 2, 0);
            buttonGrid.Controls.Add(_btnWinClose, 4, 0);
            buttonWrap.Controls.Add(buttonGrid);

            grid.Controls.Add(brandWrap, 0, 0);
            grid.Controls.Add(statusWrap, 1, 0);
            grid.Controls.Add(buttonWrap, 2, 0);

            WireDragMove(_topBar);
            WireDragMove(brandWrap);
            WireDragMove(titleWrap);
            WireDragMove(logoHost);
        }

        private void BuildRightPanel()
        {
            _rightCard = Card(Theme.Surface);
            _rightCard.Padding = new Padding(14);
            _rightCard.MinimumSize = new Size(320, 0);
            _root.Controls.Add(_rightCard, 1, 1);

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 12, // Tăng lên 12 dòng để chứa nút Capture/Collect
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 18f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 10f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 10f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 10f)); // Spacer
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f)); // Dành cho nút Capture/Collect
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 14f)); // Spacer
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 130f)); // Card Current Detect
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 14f)); // Spacer
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // Total History tự co giãn
            _rightCard.Controls.Add(grid);

            grid.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "Model",
                ForeColor = Theme.Muted,
                Font = new Font("Segoe UI Semibold", 8.8f),
                TextAlign = ContentAlignment.BottomLeft,
                Margin = new Padding(0)
            }, 0, 0);

            var comboHost = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                FillColor = Theme.InputBg,
                BorderColor = Theme.InputBorder,
                BorderThickness = 1f,
                CornerRadius = 12,
                Padding = new Padding(10, 4, 10, 4),
                Margin = new Padding(0)
            };

            _cbModel = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI Semibold", 9.8f),
                BackColor = Theme.InputBg,
                ForeColor = Theme.Text,
                FlatStyle = FlatStyle.Flat,
                IntegralHeight = false,
                DropDownHeight = 240,
                Margin = new Padding(0)
            };
            _cbModel.SelectedIndexChanged += async (_, __) => await OnModelChangedAsync();
            comboHost.Controls.Add(_cbModel);
            grid.Controls.Add(comboHost, 0, 1);

            _btnRestartServer = new ModernButton
            {
                Dock = DockStyle.Fill,
                Text = "Restart AI Server",
                BackColor = Theme.Btn,
                HoverBackColor = Theme.BtnHover,
                PressedBackColor = Theme.BtnPressed,
                ForeColor = Theme.Text,
                CornerRadius = 12,
                Font = new Font("Segoe UI Semibold", 9.2f),
                Margin = new Padding(0)
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
                ForeColor = Color.White,
                CornerRadius = 14,
                Font = new Font("Segoe UI Semibold", 10f),
                Margin = new Padding(0)
            };
            _btnDetect.Click += async (_, __) =>
            {
                if (isLRimage)
                    await DetectLRAsync();
                else
                    await DetectAsync();
            };
            grid.Controls.Add(_btnDetect, 0, 5);

            var actionWrap = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            actionWrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            actionWrap.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8f)); // Khoảng cách
            actionWrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

            _btnCapture = new ModernButton
            {
                Dock = DockStyle.Fill,
                Text = "Capture",
                BackColor = Theme.Btn,
                HoverBackColor = Theme.BtnHover,
                PressedBackColor = Theme.BtnPressed,
                ForeColor = Theme.Text,
                CornerRadius = 12,
                Font = new Font("Segoe UI Semibold", 9.5f),
                Margin = new Padding(0)
            };
            _btnCapture.Click += BtnCapture_Click;

            _btnCollect = new ModernButton
            {
                Dock = DockStyle.Fill,
                Text = "Collect",
                BackColor = Theme.Btn,
                HoverBackColor = Theme.BtnHover,
                PressedBackColor = Theme.BtnPressed,
                ForeColor = Theme.Text,
                CornerRadius = 12,
                Font = new Font("Segoe UI Semibold", 9.5f),
                Margin = new Padding(0)
            };
            _btnCollect.Click += BtnCollect_Click;

            actionWrap.Controls.Add(_btnCapture, 0, 0);
            actionWrap.Controls.Add(_btnCollect, 2, 0);

            grid.Controls.Add(actionWrap, 0, 7); // Chèn vào hàng thứ 7
            // ------------------------------------------------

            var cardCurrent = Card(Theme.Surface2);
            cardCurrent.CornerRadius = 15;
            cardCurrent.Padding = new Padding(12);
            cardCurrent.Dock = DockStyle.Fill;
            cardCurrent.Margin = new Padding(0);

            var curWrap = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            curWrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 18f));
            curWrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            curWrap.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "CURRENT DETECT",
                ForeColor = Theme.Muted,
                Font = new Font("Segoe UI Semibold", 8.4f),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            }, 0, 0);

            var curStats = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            curStats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            curStats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

            var curOk = MetricChip("OK", Theme.Ok, out _lbOk);
            var curNg = MetricChip("NG", Theme.Ng, out _lbNg);
            curOk.Margin = new Padding(0, 0, 6, 0);
            curNg.Margin = new Padding(6, 0, 0, 0);
            curStats.Controls.Add(curOk, 0, 0);
            curStats.Controls.Add(curNg, 1, 0);

            curWrap.Controls.Add(curStats, 0, 1);
            cardCurrent.Controls.Add(curWrap);
            grid.Controls.Add(cardCurrent, 0, 9); // Chuyển sang hàng 9

            var cardTotalHistory = Card(Theme.Surface2);
            cardTotalHistory.CornerRadius = 15;
            cardTotalHistory.Padding = new Padding(14);
            cardTotalHistory.Dock = DockStyle.Fill;
            cardTotalHistory.Margin = new Padding(0);

            var wrap = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
            wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 10f));
            wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 90f));
            wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 12f));
            wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 1f));
            wrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 0, 0),
                Padding = new Padding(0)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90f));

            header.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "TOTAL + HISTORY",
                ForeColor = Theme.Muted,
                Font = new Font("Segoe UI Semibold", 8.8f),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 10, 0)
            }, 0, 0);

            _btnRefresh = new ModernButton
            {
                Dock = DockStyle.Fill,
                Text = "Clear",
                CornerRadius = 11,
                BackColor = Theme.Btn,
                HoverBackColor = Theme.BtnHover,
                PressedBackColor = Theme.BtnPressed,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI Semibold", 8.2f),
                Margin = new Padding(10, 0, 0, 0),
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
                Margin = new Padding(0),
                Padding = new Padding(0, 2, 0, 0)
            };
            chips.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            chips.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

            var totOk = MetricChip("Total OK", Theme.Ok, out _lbTotalOk);
            var totNg = MetricChip("Total NG", Theme.Ng, out _lbTotalNg);
            totOk.Margin = new Padding(0, 0, 8, 0);
            totNg.Margin = new Padding(8, 0, 0, 0);
            chips.Controls.Add(totOk, 0, 0);
            chips.Controls.Add(totNg, 1, 0);

            var divider = new Panel
            {
                Dock = DockStyle.Fill,
                Height = 1,
                BackColor = Theme.Border,
                Margin = new Padding(0)
            };

            var historyHost = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                FillColor = Theme.Surface,
                BorderColor = Theme.Border,
                BorderThickness = 1f,
                CornerRadius = 14,
                Padding = new Padding(6),
                Margin = new Padding(0, 10, 0, 0)
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
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ScrollBars = ScrollBars.Vertical,
                Margin = new Padding(0)
            };
            UiPerf.EnableDoubleBuffer(_gvHistory);
            ConfigureHistoryGrid(_gvHistory);
            _gvHistory.DataSource = _history;

            _lbHistoryEmpty = new Label
            {
                AutoSize = true,
                Text = "No history yet",
                ForeColor = Theme.Muted,
                Font = new Font("Segoe UI", 9f),
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
            wrap.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) }, 0, 1);
            wrap.Controls.Add(chips, 0, 2);
            wrap.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) }, 0, 3);
            wrap.Controls.Add(divider, 0, 4);
            wrap.Controls.Add(historyHost, 0, 5);

            cardTotalHistory.Controls.Add(wrap);
            grid.Controls.Add(cardTotalHistory, 0, 11); // Chuyển sang hàng 11
        }

        private async void BtnCollect_Click(object? sender, EventArgs e)
        {
            Bitmap? safeFrame = null;
            try
            {
                DisableRightControls(true);
                SetTopProgress("Collecting...", indeterminate: true);
                LogInfo("Đang tiến hành Collect dữ liệu...");

                if (_cbModel.SelectedItem == null)
                {
                    LogError("Chưa chọn model nào.");
                    MessageBox.Show("Vui lòng chọn một model trước khi Collect!", "Cảnh báo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                string selectedModel = _cbModel.SelectedItem.ToString() ?? "";
                lock (_frameLock)
                {
                    if (_currentFrame != null)
                        safeFrame = CopyBitmap(_currentFrame);
                }

                if (safeFrame == null)
                {
                    LogError("Chưa có frame từ camera để Collect.");
                    MessageBox.Show("Chưa có hình ảnh từ camera!", "Cảnh báo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

 
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string collectDirPath = Path.Combine(appDir, "Data", selectedModel, "Collect");
                string imageDirPath = Path.Combine(collectDirPath, "Image");
                string logDirPath = Path.Combine(collectDirPath, "Log");

                if (!Directory.Exists(collectDirPath)) Directory.CreateDirectory(collectDirPath);
                if (!Directory.Exists(imageDirPath)) Directory.CreateDirectory(imageDirPath);
                if (!Directory.Exists(logDirPath)) Directory.CreateDirectory(logDirPath);

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string baseFileName = $"capture_{timestamp}";

                string imageFilePath = Path.Combine(imageDirPath, $"{baseFileName}.png");
                string logFilePath = Path.Combine(logDirPath, $"{baseFileName}.txt");
                string image_base64 = null; 
                string label_lines = null ; 
                if (isLRimage )
                {
                    if (ResultLF == null) return;
                    image_base64 = ResultLF.image_base64;
                    label_lines  = ResultLF.label_lines;

                }
                else
                {
                    if (Result == null) return;
                    image_base64 = Result.image_base64;
                    label_lines = Result.label_lines;
                }
               
                string timeString = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

               
                await Task.Run(() =>
                {
               
                    safeFrame.Save(imageFilePath, ImageFormat.Png);

                    string csvContent = "Time,Model,Image_Base64,Label_Lines\n" +
                                        $"{timeString},{selectedModel},{image_base64},{label_lines}";

           
                    File.WriteAllText(logFilePath, csvContent);
                });

                MessageBox.Show($"Collect thành công!\nẢnh: {baseFileName}.png\nLog: {baseFileName}.csv", "Collect", MessageBoxButtons.OK, MessageBoxIcon.Information);
                LogOk($"Collect xong. Đã lưu vào Data/{selectedModel}/Collect/");
            }
            catch (Exception ex)
            {
                LogError("Lỗi khi Collect: " + ex.Message);
                SetTopProgress("Collect error", indeterminate: false, value: 0);
                MessageBox.Show($"Lỗi khi Collect dữ liệu:\n{ex.Message}", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                safeFrame?.Dispose();
                DisableRightControls(false);
                SetTopProgress("Ready", indeterminate: false, value: 0);
            }
        }

        private async void BtnCapture_Click(object? sender, EventArgs e)
        {
            Bitmap? safeFrame = null;
            try
            {
                DisableRightControls(true);
                SetTopProgress("Capturing...", indeterminate: true);
                LogInfo("Đang tiến hành Capture...");
                if (_cbModel.SelectedItem == null)
                {
                    LogError("Chưa chọn model nào.");
                    MessageBox.Show("Vui lòng chọn một model trước khi Capture!", "Cảnh báo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                string selectedModel = _cbModel.SelectedItem.ToString() ?? "";
                lock (_frameLock)
                {
                    if (_currentFrame != null)
                        safeFrame = CopyBitmap(_currentFrame);
                }

                if (safeFrame == null)
                {
                    LogError("Chưa có frame từ camera để Capture.");
                    MessageBox.Show("Chưa có hình ảnh từ camera!", "Cảnh báo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string captureDirPath = Path.Combine(appDir, "Data", selectedModel, "Capture");

                if (!Directory.Exists(captureDirPath))
                {
                    Directory.CreateDirectory(captureDirPath);
                    LogInfo($"Đã tạo thư mục mới: {captureDirPath}");
                }
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string fileName = $"capture_{timestamp}.png";
                string filePath = Path.Combine(captureDirPath, fileName);

      
                await Task.Run(() =>
                {
                    safeFrame.Save(filePath, ImageFormat.Png);
                });

              
                MessageBox.Show($"Đã lưu ảnh thành công:\n{fileName}", "Capture", MessageBoxButtons.OK, MessageBoxIcon.Information);
                LogOk($"Capture thành công lưu tại: {fileName}");
            }
            catch (Exception ex)
            {
                LogError("Lỗi khi Capture: " + ex.Message);
                SetTopProgress("Capture error", indeterminate: false, value: 0);
                MessageBox.Show($"Lỗi khi lưu ảnh:\n{ex.Message}", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                safeFrame?.Dispose(); 
                DisableRightControls(false);
                SetTopProgress("Ready", indeterminate: false, value: 0);
            }
        }

        private RoundedPanel MetricChip(string title, Color valueColor, out Label valueLabel)
        {
            var chip = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                FillColor = Theme.MetricBg,
                BorderColor = Theme.MetricBorder,
                BorderThickness = 1f,
                CornerRadius = 14,
               
                Padding = new Padding(12, 4, 12, 4), 
                MinimumSize = new Size(0, 60)
            };

            var g = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            g.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));
            g.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            g.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = title,
                ForeColor = Theme.Muted,
                Font = new Font("Segoe UI Semibold", 8.6f),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Margin = new Padding(0)
            }, 0, 0);

            valueLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "0",
                ForeColor = valueColor,
                Font = new Font("Segoe UI Semibold", 15.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            g.Controls.Add(valueLabel, 0, 1);

            chip.Controls.Add(g);
            return chip;
        }

        private void BuildLogPanel()
        {
            _logCard = Card(Theme.Surface);
            _logCard.Padding = new Padding(12);
            _logCard.Margin = new Padding(0);
            _root.Controls.Add(_logCard, 0, 2);
            _root.SetColumnSpan(_logCard, 2);

            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            header.Controls.Add(new Label
            {
                Dock = DockStyle.Left,
                Width = 140,
                Text = "System Log",
                Font = new Font("Segoe UI Semibold", 9.4f),
                ForeColor = Theme.Text,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            });

            var wrap = Card(Theme.Surface2);
            wrap.Dock = DockStyle.Fill;
            wrap.CornerRadius = 12;
            wrap.Padding = new Padding(10);
            wrap.Margin = new Padding(0);

            _rtbLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Surface2,
                ForeColor = Theme.Text,
                Font = new Font("Consolas", 8.6f),
                DetectUrls = false,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };

            wrap.Controls.Add(_rtbLog);
            _logCard.Controls.Add(wrap);
            _logCard.Controls.Add(header);
        }

        private void ToggleMaximize()
        {
            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
                Bounds = new Rectangle(120, 80, 1440, 900);
            }
            else
            {
                MaximizedBounds = Screen.FromControl(this).WorkingArea;
                WindowState = FormWindowState.Maximized;
            }
            UpdateWindowButtons();
        }

        private void UpdateWindowButtons()
        {
            if (_btnWinMax == null) return;
            _btnWinMax.Text = WindowState == FormWindowState.Maximized ? "❐" : "□";
        }

        private RoundedPanel Card(Color fill) => new RoundedPanel
        {
            FillColor = fill,
            BorderColor = Theme.Border,
            CornerRadius = 18,
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
                const int durationMs = 220;
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
            if (_viewAnimTimer == null) return;
            _viewAnimTimer.Stop();
            _viewAnimTimer.Dispose();
            _viewAnimTimer = null;
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
                int device_num = 0;
                var param_path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AI_Server", "camera_parameter.txt");
                if (!File.Exists(param_path))
                {
                    LogError("Không thấy AI_Server/camera_parameter.txt");
                    return;
                }
                else
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(param_path);
                        if (int.TryParse(lines[3], out int devNum) && devNum >= 0 && devNum < _videoDevices.Count)
                        {
                            device_num = devNum;
                        }
                        else
                        {
                            LogError("Invalid camera number in camera_parameter.txt. Using default camera 0.");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError("Error reading camera_parameter.txt: " + ex.Message);
                    }
                }
                _videoSource = new VideoCaptureDevice(_videoDevices[device_num].MonikerString);
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
                raw.RotateFlip(RotateFlipType.Rotate180FlipNone);
                Bitmap roiBitmap = raw.Clone(roi, PixelFormat.Format24bppRgb);  //cut toi
                storeCopy = new Bitmap(1280, 720, PixelFormat.Format24bppRgb);  // copy de dua vao ai va hien thi, tranh loi xung dot khi camera cap nhat lien tuc resize de dua ve 1280x720
                using (var g = Graphics.FromImage(storeCopy))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(roiBitmap, 0, 0, 1280, 720);
                }
                roiBitmap.Dispose();
                lock (_frameLock)
                {
                    var old = _currentFrame;
                    _currentFrame = storeCopy;
                    storeCopy = null;
                    old?.Dispose();
                }

                _displayFrameCounter++;
                if (_displayFrameCounter % 3 != 0) return;
                using (var g = Graphics.FromImage(raw))
                {
                    using (Pen pen = new Pen(Color.Red, 5))
                    {
                        g.DrawRectangle(pen, roi);
                        g.DrawLine(pen, (roi.Left + roi.Right) / 2, roi.Top, (roi.Left + roi.Right) / 2, roi.Bottom);
                    }
                }
                displayFrame = new Bitmap(640, 360, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(displayFrame))
                {
                    g.InterpolationMode = InterpolationMode.Low;

                    g.DrawImage(raw, 0, 0, 640, 360);
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
                        FileName = "python",
                        Arguments = $"\"{serverPy}\"",
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
            string sourceDir = Path.Combine(app, "Data", selectedFolder);
            string targetDir = Path.Combine(app, "AI_Server");

            try
            {
                string bestModel = Path.Combine(sourceDir, "best.pt");

                // 1. Kiểm tra file model bắt buộc
                if (!File.Exists(bestModel))
                {
                    LogError("Không tìm thấy mô hình AI, vui lòng kiểm tra lại");
                    return;
                }

                // 2. Tạo thư mục đích nếu chưa có
                Directory.CreateDirectory(targetDir);

                // 3. Copy toàn bộ file
                foreach (var file in Directory.GetFiles(sourceDir))
                {
                    string fileName = Path.GetFileName(file);
                    string destFile = Path.Combine(targetDir, fileName);

                    // overwrite = true -> ghi đè file cũ
                    File.Copy(file, destFile, true);
                }

                SetTopProgress($"Reloading model: {selectedFolder}...", indeterminate: true);
                LogInfo($"Copied all files from {selectedFolder} → AI_Server");

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
            var param_path = Path.Combine(app, "AI_Server", "camera_parameter.txt");
            if (!File.Exists(param_path))
            {
                LogError("Không thấy AI_Server/camera_parameter.txt");
                return;
            }
            else
            {
                try
                {
                    string[] lines = File.ReadAllLines(param_path);
                    int total_object = 0;
                    if (int.TryParse(lines[0], out roiW) && int.TryParse(lines[1], out roiH))
                    {
                        startX = (2560 - roiW) / 2;   // 320
                        startY = (1440 - roiH) / 2;   // 180
                        roi = new Rectangle(startX, startY, roiW, roiH);
                        LogInfo($"Camera parameters - Width: {roiW}, Height: {roiH}");
                        StopCamera();
                        StartCamera();
                    }
                    else
                    {
                        LogError("Invalid camera parameters in camera_parameter.txt");
                    }
                    if (int.TryParse(lines[2], out total_object))
                    {
                        total_objects_detected = total_object;
                        LogInfo($"Camera parameter - Total Object: {total_object}");
                    }
                    if (lines.Count() >= 5 && lines[4] == "lr")
                    {
                        isLRimage = true;
                    }
                    else
                    {
                        isLRimage = false;
                    }
                }
                catch (Exception ex)
                {
                    LogError("Error reading camera_parameter.txt: " + ex.Message);
                }
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

                Result = await  DetectFromBitmapAsync(safeFrame);
                if (Result == null)
                {
                    SetTopProgress("Detect finished (no result)", indeterminate: false, value: 0);
                    LogError("Detect trả về rỗng / image_base64 empty.");
                    return;
                }
                if (Result.ok_count + Result.ng_count != total_objects_detected)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        SetTopProgress($"Re-detecting... Attempt {i + 2}/3", indeterminate: true);
                        LogInfo($"Số lượng đối tượng phát hiện không đúng (OK={Result.ok_count} NG={Result.ng_count}), thử lại lần {i + 2}/3...");
                        Result = await DetectFromBitmapAsync(safeFrame);
                        if (Result != null && Result.ok_count + Result.ng_count == total_objects_detected)
                            break;
                    }
                }
                if (Result.ok_count + Result.ng_count != total_objects_detected)
                {
                    MessageBox.Show($"Cảnh báo: Số lượng đối tượng phát hiện không đúng, vui lòng kiểm tra lại môi trường test.", "Cảnh báo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _isDetecting = false;
                    return;
                }

                _lbOk.Text = Result.ok_count.ToString();
                _lbNg.Text = Result.ng_count.ToString();

                _totalOk += Result.ok_count;
                _totalNg += Result.ng_count;
                _lbTotalOk.Text = _totalOk.ToString();
                _lbTotalNg.Text = _totalNg.ToString();

                sw.Stop();
                AddHistoryRow(new DetectHistoryRow
                {
                    Timestamp = ts,
                    Model = modelName,
                    OK = Result.ok_count,
                    NG = Result.ng_count,
                });

                SetTopProgress($"Done • OK {Result.ok_count} • NG {Result.ng_count}", indeterminate: false, value: 100);
                LogOk($"Detect done. Model={modelName} OK={Result.ok_count} NG={Result.ng_count} ({sw.ElapsedMilliseconds} ms)");
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

        private async Task DetectLRAsync()
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

                var result = await DetectLRFromBitmapAsync(safeFrame);
                ResultLF = result;

                if (result == null)
                {
                    SetTopProgress("Detect finished (no result)", indeterminate: false, value: 0);
                    LogError("Detect trả về rỗng / image_base64 empty.");
                    return;
                }
                if ((result.right_ok + result.right_ng) != total_objects_detected / 2 && (result.left_ok + result.left_ng) != total_objects_detected / 2)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        SetTopProgress($"Re-detecting... Attempt {i + 2}/3", indeterminate: true);
                        result = await DetectLRFromBitmapAsync(safeFrame);
                        if (result != null && result.right_ok + result.right_ng + result.left_ok + result.left_ng != total_objects_detected)
                            break;
                    }
                }
                if ((result.right_ok + result.right_ng) != total_objects_detected / 2 && (result.left_ok + result.left_ng) != total_objects_detected / 2)
                {
                    MessageBox.Show($"Cảnh báo: Số lượng đối tượng phát hiện không đúng, vui lòng kiểm tra lại môi trường test.", "Cảnh báo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _isDetecting = false;
                    return;
                }

                int total_OK = result.right_ok + result.left_ok;
                int total_NG = result.right_ng + result.left_ng;
                _lbOk.Text = total_OK.ToString();
                _lbNg.Text = total_NG.ToString();

                _totalOk += total_OK;
                _totalNg += total_NG;
                _lbTotalOk.Text = _totalOk.ToString();
                _lbTotalNg.Text = _totalNg.ToString();

                sw.Stop();
                AddHistoryRow(new DetectHistoryRow
                {
                    Timestamp = ts,
                    Model = modelName,
                    OK = total_OK,
                    NG = total_NG,
                });

                SetTopProgress($"Done • OK {total_OK} • NG {total_NG}", indeterminate: false, value: 100);
                LogOk($"Detect done. Model={modelName} OK={total_OK} NG={total_NG} ({sw.ElapsedMilliseconds} ms)");
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
        private async Task<DetectResult?> DetectFromBitmapAsync(Bitmap frame)
        {
            using var ms = new MemoryStream();
            frame.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(ms.ToArray()), "file", "frame.png");

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

            if (result == null || string.IsNullOrWhiteSpace(result.image_base64))
                return null;

            byte[] bytes = Convert.FromBase64String(result.image_base64);
            using var ms2 = new MemoryStream(bytes);
            using var tmp = new Bitmap(ms2);
            var oldImg = _pbResult.Image;
            _pbResult.Image = new Bitmap(tmp);
            oldImg?.Dispose();
            return result;
        }
        private async Task<DetectResultLR?> DetectLRFromBitmapAsync(Bitmap frame)
        {
            using var ms = new MemoryStream();
            frame.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(ms.ToArray()), "file", "frame.png");

            using var resp = await _http.PostAsync("http://127.0.0.1:8000/detect_lr", form);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                LogError("Detect HTTP error: " + resp.StatusCode);
                return null;
            }

            var result = JsonSerializer.Deserialize<DetectResultLR>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result == null || string.IsNullOrWhiteSpace(result.image_base64))
                return null;

            byte[] bytes = Convert.FromBase64String(result.image_base64);
            using var ms2 = new MemoryStream(bytes);
            using var tmp = new Bitmap(ms2);
            var oldImg = _pbResult.Image;
            _pbResult.Image = new Bitmap(tmp);
            oldImg?.Dispose();
            return result;
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
            if (_btnCapture != null) _btnCapture.Enabled = !busy; // Vô hiệu hoá Capture khi busy
            if (_btnCollect != null) _btnCollect.Enabled = !busy; // Vô hiệu hoá Collect khi busy
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

        private void ResetTotalsAndHistory()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(ResetTotalsAndHistory));
                return;
            }

            _totalOk = 0;
            _totalNg = 0;
            _lbTotalOk.Text = "0";
            _lbTotalNg.Text = "0";
            _history.Clear();
            UpdateHistoryEmptyState();
            LogInfo("Refresh: cleared TOTAL + HISTORY.");
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

        private void LogInfo(string msg) => EnqueueLog("INFO", msg, Theme.LogInfo);
        private void LogOk(string msg) => EnqueueLog("OK", msg, Theme.LogOk);
        private void LogError(string msg) => EnqueueLog("ERR", msg, Theme.LogErr);

        private void EnqueueLog(string tag, string msg, Color color)
        {
            _logQueue.Enqueue(new LogItem(tag, msg, color));
        }

        private void FlushLogBatch(int maxLines)
        {
            if (_rtbLog.IsDisposed || !_rtbLog.IsHandleCreated || _logQueue.IsEmpty) return;
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
            catch
            {
                return null;
            }
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
            gv.ColumnHeadersHeight = 28;
            gv.RowTemplate.Height = 28;
            gv.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

            gv.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Theme.GridHeader,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI Semibold", 8.8f),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 8, 0),
                WrapMode = DataGridViewTriState.False
            };

            gv.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 8.8f),
                SelectionBackColor = Theme.RowSelect,
                SelectionForeColor = Theme.Text,
                Padding = new Padding(8, 0, 8, 0),
                WrapMode = DataGridViewTriState.False
            };

            gv.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Theme.RowAlt,
                ForeColor = Theme.Text,
                SelectionBackColor = Theme.RowSelect,
                SelectionForeColor = Theme.Text,
                Padding = new Padding(8, 0, 8, 0)
            };

            gv.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(DetectHistoryRow.Timestamp),
                HeaderText = "Time",
                FillWeight = 34,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Format = "HH:mm:ss",
                    Padding = new Padding(8, 0, 8, 0)
                }
            });

            gv.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(DetectHistoryRow.Model),
                HeaderText = "Model",
                FillWeight = 34
            });

            gv.Columns.Add(new DataGridViewTextBoxColumn
            {
                DataPropertyName = nameof(DetectHistoryRow.OK),
                HeaderText = "OK",
                FillWeight = 16,
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
                FillWeight = 16,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    ForeColor = Theme.Ng,
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    Padding = new Padding(8, 0, 8, 0)
                }
            });

            gv.ResumeLayout();
        }


        public sealed class DetectResult
        {
            public int ok_count { get; set; }
            public int ng_count { get; set; }
            public string image_base64 { get; set; } = "";
            public string label_lines { get; set; } = "";
        }
        public sealed class DetectResultLR
        {
            public int left_ok { get; set; }
            public int left_ng { get; set; }
            public int right_ok { get; set; }
            public int right_ng { get; set; }
            public string image_base64 { get; set; } = "";
            public string label_lines { get; set; } = "";
        }

        public sealed class DetectHistoryRow
        {
            public DateTime Timestamp { get; set; }
            public string Model { get; set; } = "";
            public int OK { get; set; }
            public int NG { get; set; }
        }

        private readonly record struct LogItem(string Tag, string Message, Color Color);

        private static class Theme
        {
            public static readonly Color AppBorder = Color.FromArgb(14, 29, 56);
            public static readonly Color WindowEdge = Color.FromArgb(11, 24, 46);
            public static readonly Color Bg = Color.FromArgb(232, 240, 250);
            public static readonly Color Surface = Color.FromArgb(246, 250, 255);
            public static readonly Color Surface2 = Color.FromArgb(238, 245, 255);
            public static readonly Color Border = Color.FromArgb(196, 214, 235);
            public static readonly Color Shadow = Color.FromArgb(22, 10, 35, 72);
            public static readonly Color Text = Color.FromArgb(18, 37, 65);
            public static readonly Color Muted = Color.FromArgb(87, 112, 145);
            public static readonly Color Accent = Color.FromArgb(24, 94, 188);
            public static readonly Color AccentHover = Color.FromArgb(34, 108, 208);
            public static readonly Color AccentPressed = Color.FromArgb(20, 80, 164);
            public static readonly Color Btn = Color.FromArgb(223, 236, 252);
            public static readonly Color BtnHover = Color.FromArgb(209, 228, 249);
            public static readonly Color BtnPressed = Color.FromArgb(193, 218, 246);
            public static readonly Color ChromeBtn = Color.FromArgb(224, 236, 250);
            public static readonly Color ChromeBtnHover = Color.FromArgb(210, 227, 247);
            public static readonly Color ChromeBtnPressed = Color.FromArgb(192, 213, 240);
            public static readonly Color CloseBtn = Color.FromArgb(219, 73, 93);
            public static readonly Color CloseBtnHover = Color.FromArgb(235, 88, 107);
            public static readonly Color CloseBtnPressed = Color.FromArgb(194, 58, 76);
            public static readonly Color InputBg = Color.FromArgb(255, 255, 255);
            public static readonly Color InputBorder = Color.FromArgb(145, 182, 226);
            public static readonly Color Track = Color.FromArgb(206, 222, 242);
            public static readonly Color ViewportBg = Color.FromArgb(226, 236, 248);
            public static readonly Color MetricBg = Color.FromArgb(247, 250, 255);
            public static readonly Color MetricBorder = Color.FromArgb(194, 213, 235);
            public static readonly Color GridHeader = Color.FromArgb(218, 232, 248);
            public static readonly Color RowAlt = Color.FromArgb(242, 247, 255);
            public static readonly Color RowSelect = Color.FromArgb(204, 225, 252);
            public static readonly Color Ok = Color.FromArgb(15, 145, 108);
            public static readonly Color Ng = Color.FromArgb(214, 61, 92);
            public static readonly Color LogInfo = Color.FromArgb(68, 94, 126);
            public static readonly Color LogOk = Color.FromArgb(17, 130, 100);
            public static readonly Color LogErr = Color.FromArgb(198, 54, 82);
        }

        private static class UiPerf
        {
            public static void EnableDoubleBuffer(Control c)
            {
                if (c is null) return;
                try
                {
                    typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(c, true, null);
                    typeof(Control).GetMethod("SetStyle", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(c, new object[]
                    {
                        ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer,
                        true
                    });
                    typeof(Control).GetMethod("UpdateStyles", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(c, null);
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
            public bool SuppressRegionUpdate { get; set; }

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
                if (SuppressRegionUpdate || !IsHandleCreated || Width < 8 || Height < 8) return;

                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || !IsHandleCreated || SuppressRegionUpdate || Width < 8 || Height < 8) return;
                    RefreshRegionNow();
                }));
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                if (!SuppressRegionUpdate && Width > 8 && Height > 8)
                    RefreshRegionNow();
            }

            public void RefreshRegionNow()
            {
                if (Width <= 2 || Height <= 2) return;
                var rect = new Rectangle(0, 0, Width, Height);
                if (rect == _cachedRect && _cachedPath != null) return;
                _cachedRect = rect;
                _cachedPath?.Dispose();
                _cachedPath = RoundRect(rect, CornerRadius);
                Region = new Region(_cachedPath);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                if (rect.Width <= 2 || rect.Height <= 2) return;

                using var path = RoundRect(rect, CornerRadius);
                using (var br = new SolidBrush(FillColor))
                    e.Graphics.FillPath(br, path);

                using var pen = new Pen(BorderColor, BorderThickness);
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

            private bool _hover;
            private bool _down;
            private Color _normal;

            public ModernButton()
            {
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                Height = 40;
                Cursor = Cursors.Hand;
                Padding = new Padding(0);
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
                using var path = CreateRoundPath(new Rectangle(0, 0, Width, Height), CornerRadius);
                Region = new Region(path);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                var bg = _normal;
                if (!Enabled) bg = ControlPaint.Light(_normal, 0.12f);
                else if (_down) bg = PressedBackColor;
                else if (_hover) bg = HoverBackColor;

                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using var path = CreateRoundPath(rect, CornerRadius);
                using var br = new SolidBrush(bg);
                using var pen = new Pen(Theme.Border, 1f);

                e.Graphics.FillPath(br, path);
                e.Graphics.DrawPath(pen, path);

                string drawText = Text == "–" ? "−" : Text;
                var textRect = new Rectangle(rect.X, rect.Y - 1, rect.Width, rect.Height + 1);
                TextRenderer.DrawText(
                    e.Graphics,
                    drawText,
                    Font,
                    textRect,
                    Enabled ? ForeColor : ControlPaint.Dark(ForeColor),
                    TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine |
                    TextFormatFlags.NoPadding |
                    TextFormatFlags.EndEllipsis);
            }

            private static GraphicsPath CreateRoundPath(Rectangle rect, int radius)
            {
                var path = new GraphicsPath();
                int r = Math.Max(0, Math.Min(radius, Math.Min(rect.Width / 2, rect.Height / 2)));
                if (r <= 0)
                {
                    path.AddRectangle(rect);
                    path.CloseFigure();
                    return path;
                }

                int d = r * 2;
                path.AddArc(rect.X, rect.Y, d, d, 180, 90);
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }

        private void UpdateResponsiveLayout()
        {
            if (_root == null) return;

            int width = ClientSize.Width;
            int height = ClientSize.Height;
            bool compact = width < 1500;
            bool shortScreen = height < 860;

            _root.Padding = compact ? new Padding(12, 10, 12, 12) : new Padding(16, 12, 16, 14);
            // CẬP NHẬT Ở ĐÂY LÊN 360 / 400 ĐỂ CHỐNG LẸM (thay vì 320 / 360 cũ)
            _root.ColumnStyles[1].Width = compact ? 360f : 400f;
            _root.RowStyles[0].Height = compact ? 72f : 78f;
            _root.RowStyles[2].Height = shortScreen ? 104f : (compact ? 112f : 120f);

            if (_topBar != null)
                _topBar.Padding = compact ? new Padding(14, 10, 14, 10) : new Padding(16, 12, 16, 12);

            if (_rightCard != null)
                _rightCard.Padding = compact ? new Padding(12) : new Padding(14);

            if (_viewportCard != null)
                _viewportCard.Padding = compact ? new Padding(10) : new Padding(12);

            PositionHistoryEmptyLabel();
            ApplyViewportLayout(animated: false);
        }

        private sealed class ModernProgressBar : Control
        {
            public int Minimum { get; set; }
            public int Maximum { get; set; } = 100;

            private int _value;
            public int Value
            {
                get => _value;
                set { _value = Math.Clamp(value, Minimum, Maximum); Invalidate(); }
            }

            private bool _indeterminate;
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