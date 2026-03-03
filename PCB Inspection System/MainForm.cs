using AForge.Video;
using AForge.Video.DirectShow;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
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

        private Bitmap? _currentFrame;
        private readonly object _frameLock = new();
        private volatile bool _isDetecting;
        private int _displayFrameCounter;
        private bool _isClosing;

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

   
        private TableLayoutPanel _root = new();

       
        private RoundedPanel _topBar = new();
        private PictureBox _pbLogo = new();
        private Label _lbTitle = new();
        private Label _lbSub = new();
        private Label _lbProgText = new();
        private ModernProgressBar _progTop = new();

      
        private RoundedPanel _viewportCard = new();
        private Panel _viewportHost = new();
        private RoundedPanel _panelLive = new();
        private RoundedPanel _panelResult = new();
        private PictureBox _pbLive = new();
        private PictureBox _pbResult = new();
        private bool _detectedMode;

       
        private RoundedPanel _rightCard = new();
        private ComboBox _cbModel = new();
        private ModernButton _btnRestartServer = new();
        private ModernButton _btnDetect = new();
        private Label _lbOk = new();
        private Label _lbNg = new();

     
        private RoundedPanel _logCard = new();
        private RichTextBox _rtbLog = new();

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
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));

         
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
            _topBar.Dock = DockStyle.Fill;
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
                Margin = new Padding(0)
            };
            _pbLogo.Image = LoadLogoSafe();
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

            _lbTitle = new Label
            {
                Dock = DockStyle.Fill,
                Text = "PCB Inspection System",
                Font = new Font("Segoe UI Semibold", 16f),
                ForeColor = Theme.Text,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _lbSub = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Live Camera • AI Detect • Industrial UI",
                ForeColor = Theme.Muted,
                TextAlign = ContentAlignment.MiddleLeft
            };

            titleStack.Controls.Add(_lbTitle, 0, 0);
            titleStack.Controls.Add(_lbSub, 0, 1);
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
            _viewportCard.Dock = DockStyle.Fill;
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
            _rightCard.Dock = DockStyle.Fill;
            _rightCard.Padding = new Padding(12);
            _root.Controls.Add(_rightCard, 1, 1);

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 11,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };

        
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); // model label
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); // combo
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); // restart
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 56)); // detect
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 125)); // summary
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));

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

            var summary = Card(Theme.Surface2);
            summary.CornerRadius = 14;
            summary.Padding = new Padding(12);

            var sgrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                BackColor = Color.Transparent,
                Margin = new Padding(0)
            };
            sgrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            sgrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            sgrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            sgrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            sgrid.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "Result Summary",
                ForeColor = Theme.Muted,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            sgrid.SetColumnSpan(sgrid.Controls[sgrid.Controls.Count - 1], 2);

            _lbOk = new Label
            {
                Dock = DockStyle.Fill,
                Text = "OK: 0",
                Font = new Font("Segoe UI Semibold", 12f),
                ForeColor = Theme.Ok,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _lbNg = new Label
            {
                Dock = DockStyle.Fill,
                Text = "NG: 0",
                Font = new Font("Segoe UI Semibold", 12f),
                ForeColor = Theme.Ng,
                TextAlign = ContentAlignment.MiddleLeft
            };

            sgrid.Controls.Add(_lbOk, 0, 1);
            sgrid.Controls.Add(_lbNg, 1, 1);

            summary.Controls.Add(sgrid);
            grid.Controls.Add(summary, 0, 7);
        }

        private void BuildLogPanel()
        {
            _logCard = Card(Theme.Surface);
            _logCard.Dock = DockStyle.Fill;
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
            header.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
            
                ForeColor = Theme.Muted,
                TextAlign = ContentAlignment.MiddleRight
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
            Rectangle resultTarget;

            if (!_detectedMode)
            {
                // LIVE full
                liveTarget = bounds;
                // RESULT đẩy ra ngoài (ẩn)
                resultTarget = new Rectangle(bounds.Right + gap, bounds.Y, Math.Max(10, bounds.Width / 5), bounds.Height);
            }
            else
            {
                // RESULT left big, LIVE right small
                int rightW = Math.Clamp((int)(bounds.Width * 0.30), 280, 460);
                int leftW = bounds.Width - rightW - gap;
                leftW = Math.Max(520, leftW);

                if (leftW + rightW + gap > bounds.Width)
                {
                    rightW = Math.Clamp(bounds.Width - 520 - gap, 240, 420);
                    leftW = bounds.Width - rightW - gap;
                }

                resultTarget = new Rectangle(bounds.X, bounds.Y, leftW, bounds.Height);
                liveTarget = new Rectangle(bounds.Right - rightW, bounds.Y, rightW, bounds.Height);
            }

            if (!animated)
            {
                _panelLive.Bounds = liveTarget;
                _panelResult.Bounds = resultTarget;
                _panelResult.Visible = _detectedMode;

         
                _panelLive.BringToFront();
                return;
            }

            _panelResult.Visible = true;

            Animator.AnimateRect(_panelLive, "live",
                () => _panelLive.Bounds, r => _panelLive.Bounds = r,
                liveTarget, 220, Animator.EaseOutCubic);

            Animator.AnimateRect(_panelResult, "result",
                () => _panelResult.Bounds, r => _panelResult.Bounds = r,
                resultTarget, 220, Animator.EaseOutCubic,
                completed: () =>
                {
                    if (!_detectedMode) _panelResult.Visible = false;
                });

            _panelLive.BringToFront();
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
            Bitmap frame = (Bitmap)e.Frame.Clone();

            lock (_frameLock)
            {
                _currentFrame?.Dispose();
                _currentFrame = frame;
            }

            _displayFrameCounter++;
            if (_displayFrameCounter % 3 != 0) return; // giảm tải

            Bitmap displayFrame = new Bitmap(640, 480);
            using (Graphics g = Graphics.FromImage(displayFrame))
            {
                g.InterpolationMode = InterpolationMode.Low;
                g.DrawImage(frame, 0, 0, 640, 480);
            }

            if (_isClosing || _pbLive.IsDisposed || !_pbLive.IsHandleCreated)
            {
                displayFrame.Dispose();
                return;
            }

            try
            {
                _pbLive.BeginInvoke(new Action(() =>
                {
                    if (_pbLive.IsDisposed) { displayFrame.Dispose(); return; }
                    _pbLive.Image?.Dispose();
                    _pbLive.Image = displayFrame;
                }));
            }
            catch
            {
                displayFrame.Dispose();
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
            }
            catch { }
        }

 
        private async Task RestartServerAsync()
        {
            try
            {
                DisableRightControls(true);

                SetTopProgress("Starting AI server...", indeterminate: true);
                LogInfo("Restart AI server...");

                await StartPythonServerAsync();

                SetTopProgress("AI server ready", indeterminate: false, value: 0);
                LogOk("AI server ready.");
            }
            catch (Exception ex)
            {
                SetTopProgress("AI server error", indeterminate: false, value: 0);
                LogError(ex.Message);
            }
            finally
            {
                DisableRightControls(false);
            }
        }

        private async Task StartPythonServerAsync()
        {
            StopPythonServer();

            string workDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AI_Server");
            string scriptPath = Path.Combine(workDir, "server.py");

            if (!File.Exists(scriptPath))
                throw new FileNotFoundException("Không tìm thấy server.py", scriptPath);

            var psi = new ProcessStartInfo
            {
                FileName = "python",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
         //   psi.ArgumentList.Add("-3.11");
            psi.ArgumentList.Add(scriptPath);

            _pythonProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

            _pythonProcess.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) LogInfo("[PY] " + e.Data);
            };
            _pythonProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) LogError("[PY-ERR] " + e.Data);
            };

            if (!_pythonProcess.Start())
                throw new InvalidOperationException("Không start được Python process.");

            _pythonProcess.BeginOutputReadLine();
            _pythonProcess.BeginErrorReadLine();

            await WaitForServerAsync("http://127.0.0.1:8000/docs", timeoutMs: 9000);
        }

        private static async Task WaitForServerAsync(string url, int timeoutMs)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1000) };
            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    using var resp = await http.GetAsync(url);
                    if ((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 500)
                        return;
                }
                catch { }
                await Task.Delay(300);
            }

            throw new TimeoutException("Python server chưa khởi động kịp trong thời gian cho phép.");
        }

        private void StopPythonServer()
        {
            try
            {
                if (_pythonProcess != null && !_pythonProcess.HasExited)
                {
                    _pythonProcess.Kill(true);
                    _pythonProcess.Dispose();
                    _pythonProcess = null;
                }
            }
            catch { }
        }


        private void LoadModels()
        {
            try
            {
                string dataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
                if (!Directory.Exists(dataPath))
                {
                    LogError("Không tìm thấy thư mục Data!");
                    return;
                }

                var names = Directory.GetDirectories(dataPath)
                                     .Select(Path.GetFileName)
                                     .Where(x => !string.IsNullOrWhiteSpace(x))
                                     .ToList();

                _cbModel.DataSource = names;
                if (names.Count > 0) _cbModel.SelectedIndex = 0;

                LogOk($"Loaded {names.Count} model folders.");
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
                    safeFrame = (Bitmap)_currentFrame.Clone();
            }

            if (safeFrame == null)
            {
                LogError("Chưa có frame từ camera.");
                return;
            }

            DisableRightControls(true);

            try
            {
                _isDetecting = true;

                EnterDetectedMode();
                SetTopProgress("Detecting...", indeterminate: true);
                LogInfo("Detect started...");

                var result = await DetectFromBitmapAsync(safeFrame);

                if (result == null)
                {
                    SetTopProgress("Detect finished (no result)", indeterminate: false, value: 0);
                    LogError("Detect trả về rỗng / image_base64 empty.");
                    return;
                }

                _lbOk.Text = $"OK: {result.ok_count}";
                _lbNg.Text = $"NG: {result.ng_count}";

                SetTopProgress($"Done • OK {result.ok_count} • NG {result.ng_count}", indeterminate: false, value: 100);
                LogOk($"Detect done. OK={result.ok_count}, NG={result.ng_count}");
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

            _pbResult.Image?.Dispose();
            _pbResult.Image = new Bitmap(ms2);

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
        }

        private async void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            _isClosing = true;
            StopCamera();
            StopPythonServer();

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                await client.PostAsync("http://127.0.0.1:8000/shutdown", null);
            }
            catch { }
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

        private void LogInfo(string msg) => AppendLog("INFO", msg, Theme.LogInfo);
        private void LogOk(string msg) => AppendLog("OK", msg, Theme.LogOk);
        private void LogError(string msg) => AppendLog("ERR", msg, Theme.LogErr);

        private void AppendLog(string tag, string msg, Color color)
        {
            if (_rtbLog.IsDisposed) return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => AppendLog(tag, msg, color)));
                return;
            }

            var line = $"{DateTime.Now:HH:mm:ss} [{tag}] {msg}\n";

            _rtbLog.SelectionStart = _rtbLog.TextLength;
            _rtbLog.SelectionLength = 0;
            _rtbLog.SelectionColor = color;
            _rtbLog.AppendText(line);
            _rtbLog.SelectionColor = Theme.Text;
            _rtbLog.ScrollToCaret();
        }

        private Image? LoadLogoSafe()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AI_Server", "logo.png");
                if (File.Exists(path)) return Image.FromFile(path);
            }
            catch { }
            return null;
        }


        private static class Theme
        {
            public static readonly Color Bg = Color.FromArgb(245, 246, 248);
            public static readonly Color Surface = Color.White;
            public static readonly Color Surface2 = Color.FromArgb(248, 249, 251);

            public static readonly Color Border = Color.FromArgb(220, 226, 235);
            public static readonly Color Shadow = Color.FromArgb(24, 0, 0, 0);

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


        private sealed class RoundedPanel : Panel
        {
            public int CornerRadius { get; set; } = 16;
            public Color FillColor { get; set; } = Theme.Surface;
            public Color BorderColor { get; set; } = Theme.Border;
            public float BorderThickness { get; set; } = 1f;

            public RoundedPanel()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                BackColor = Color.Transparent;
                Margin = new Padding(0);
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                if (Width <= 2 || Height <= 2) return;

                using var path = RoundRect(new Rectangle(0, 0, Width, Height), CornerRadius);
                Region = new Region(path);
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

                _timer = new System.Windows.Forms.Timer { Interval = 15 };
                _timer.Tick += (_, __) =>
                {
                    _phase = (_phase + 6) % Math.Max(1, Width + 120);
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
                    int segW = Math.Max(28, (int)(Width * 0.24));
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

        private static class Animator
        {
            private static readonly Dictionary<(Control, string), Action> _running = new();

            public static void Stop(Control c, string name)
            {
                if (_running.TryGetValue((c, name), out var cancel))
                {
                    cancel();
                    _running.Remove((c, name));
                }
            }

            public static void AnimateRect(
                Control c, string name,
                Func<Rectangle> get, Action<Rectangle> set,
                Rectangle to, int durationMs,
                Func<float, float> easing,
                Action? completed = null)
            {
                Stop(c, name);

                var from = get();
                if (from == to) { completed?.Invoke(); return; }

                var sw = Stopwatch.StartNew();
                var timer = new System.Windows.Forms.Timer { Interval = 15 };

                void Cancel()
                {
                    timer.Stop();
                    timer.Dispose();
                }

                _running[(c, name)] = Cancel;

                timer.Tick += (_, __) =>
                {
                    float t = (float)sw.ElapsedMilliseconds / durationMs;
                    if (t >= 1f) t = 1f;

                    float k = easing(t);
                    set(Lerp(from, to, k));

                    if (t >= 1f)
                    {
                        Cancel();
                        _running.Remove((c, name));
                        completed?.Invoke();
                    }
                };

                timer.Start();
            }

            private static Rectangle Lerp(Rectangle a, Rectangle b, float t)
            {
                int x = a.X + (int)Math.Round((b.X - a.X) * t);
                int y = a.Y + (int)Math.Round((b.Y - a.Y) * t);
                int w = a.Width + (int)Math.Round((b.Width - a.Width) * t);
                int h = a.Height + (int)Math.Round((b.Height - a.Height) * t);
                return new Rectangle(x, y, w, h);
            }

            public static float EaseOutCubic(float t) => 1f - (float)Math.Pow(1f - t, 3);
        }

    
        public sealed class DetectResult
        {
            public int ok_count { get; set; }
            public int ng_count { get; set; }
            public string image_base64 { get; set; } = "";
        }
    }
}