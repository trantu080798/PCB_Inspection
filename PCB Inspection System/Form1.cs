using AForge.Video;
using AForge.Video.DirectShow;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;
using System.IO;


namespace PCB_Inspection_System
{
    public partial class Form1 : Form
    {
        private Process? _pythonProcess;
        private FilterInfoCollection? videoDevices;
        private VideoCaptureDevice? videoSource;

        private Bitmap? currentFrame;
        private bool is_detecting = false;
        private int _displayFrameCounter = 0;
        private bool _isClosing = false;
        public Form1()
        {
            InitializeComponent();
            StartCamera();
        }
        private void StartCamera()
        {
            videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);

            if (videoDevices.Count == 0)
            {
                MessageBox.Show("Không tìm thấy webcam.");
                return;
            }

            videoSource = new VideoCaptureDevice(videoDevices[0].MonikerString);

            // Chọn độ phân giải cao nhất có thể (ưu tiên 2560x1440)
            foreach (var cap in videoSource.VideoCapabilities)
            {
                if (cap.FrameSize.Width == 2560 && cap.FrameSize.Height == 1440)
                {
                    videoSource.VideoResolution = cap;
                    break;
                }
            }
            videoSource.NewFrame -= VideoSource_NewFrame;
            videoSource.NewFrame += VideoSource_NewFrame;
            videoSource.Start();
        }
        private void VideoSource_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
           
            Bitmap frame = (Bitmap)eventArgs.Frame.Clone();
            lock (this)
            {
                currentFrame?.Dispose();
                currentFrame = frame;
            }

            _displayFrameCounter++;

            if (_displayFrameCounter % 3 != 0 )
                return;   // bỏ 2 frame, chỉ hiển thị frame thứ 3
            Bitmap displayFrame = new Bitmap(640, 480);
            using (Graphics g = Graphics.FromImage(displayFrame))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
                g.DrawImage(frame, 0, 0, 640, 480);
            }
            if (_isClosing || pictureBox2 == null || pictureBox2.IsDisposed || !pictureBox2.IsHandleCreated)
            {
                displayFrame.Dispose();
                return;
            }
            // Cập nhật UI (phải Invoke vì đang ở thread khác)
            try
            {
                pictureBox2.BeginInvoke(new Action(() =>
                {
                    if (pictureBox2.IsDisposed) return;

                    pictureBox2.Image?.Dispose();
                    pictureBox2.Image = displayFrame;
                }));
            }
            catch
            {
                displayFrame.Dispose();
            }
        }
        private void StopCamera()
        {
            if (videoSource != null)
            {
                videoSource.NewFrame -= VideoSource_NewFrame;

                if (videoSource.IsRunning)
                {
                    videoSource.SignalToStop();
                    videoSource.WaitForStop();
                }

                videoSource = null;
            }
        }
        private void StartPythonServer()
        {
            StopPythonServer(); // đảm bảo không có tiến trình cũ

            string exePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "AI_Server",
                "server.py"
            );

            string pythonExe = "python";

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{exePath}\"",
                WorkingDirectory = Path.GetDirectoryName(exePath),
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _pythonProcess = Process.Start(psi);

            // Đợi server khởi động (2-3 giây)
            Thread.Sleep(3000);
            //http://127.0.0.1:8000/docs
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
        private async Task DetectImage(string imagePath)
        {
            using var client = new HttpClient();

            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(File.ReadAllBytes(imagePath));
            form.Add(fileContent, "file", "image.jpg");

            var response = await client.PostAsync("http://127.0.0.1:8000/detect", form);

            var jsonString = await response.Content.ReadAsStringAsync();

            var result = JsonSerializer.Deserialize<DetectResult>(jsonString);

            MessageBox.Show($"OK: {result.ok_count} | NG: {result.ng_count}");

            byte[] imageBytes = Convert.FromBase64String(result.image_base64);

            using (var ms = new MemoryStream(imageBytes))
            {
                Bitmap bmp = new Bitmap(ms);
                pictureBox1.Image = new Bitmap(bmp);
            }
        }
        private async Task DetectFromBitmap(Bitmap frame)
        {
            is_detecting = true;
            lb_Status.Text = "Detecting";
            using var client = new HttpClient();

            using var ms = new MemoryStream();
            frame.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);

            ms.Position = 0;

            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(ms.ToArray()), "file", "frame.jpg");

            var response = await client.PostAsync("http://127.0.0.1:8000/detect", form);

            var jsonString = await response.Content.ReadAsStringAsync();

            var result = JsonSerializer.Deserialize<DetectResult>(jsonString);
            if (string.IsNullOrEmpty(result.image_base64))
            {
                return;
            }
            lblOK.Text = $"OK: {result.ok_count}";
            lblNG.Text = $"NG: {result.ng_count}";
            MessageBox.Show($"OK: {result.ok_count} | NG: {result.ng_count}");
            byte[] imageBytes = Convert.FromBase64String(result.image_base64);

            using var ms2 = new MemoryStream(imageBytes);

            if (pictureBox1.Image != null)
                pictureBox1.Image.Dispose();

            pictureBox1.Image = new Bitmap(ms2);
            is_detecting = false;
            lb_Status.Text = "Idle";
        }
        private async Task DetectCapture(Bitmap bmp)
        {
            using var client = new HttpClient();

            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); // PNG giữ chất lượng
            ms.Position = 0;

            using var form = new MultipartFormDataContent();
            var fileContent = new StreamContent(ms);
            fileContent.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");

            form.Add(fileContent, "file", "capture.png");

            var response = await client.PostAsync(
                "http://127.0.0.1:8000/detect",
                form
            );

            if (!response.IsSuccessStatusCode)
            {
                MessageBox.Show("Server error");
                return;
            }

            var jsonString = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<DetectResult>(jsonString);

            MessageBox.Show($"OK: {result.ok_count} | NG: {result.ng_count}");

            byte[] imageBytes = Convert.FromBase64String(result.image_base64);

            using (var ms1 = new MemoryStream(imageBytes))
            {
                Bitmap bmp1 = new Bitmap(ms1);
                pictureBox1.Image = new Bitmap(bmp1);
            }
        }
        private async void btn_detect_Click(object sender, EventArgs e)
        {
            //await DetectImage("test.png");
            if (is_detecting)
            {
                MessageBox.Show("Đang xử lý, vui lòng đợi.");
                return;
            }
            if (currentFrame == null)
            {
                MessageBox.Show("Chưa có frame.");
                return;
            }
            Bitmap safeFrame;

            lock (this)
            {
                safeFrame = (Bitmap)currentFrame.Clone();
            }

            await DetectFromBitmap(safeFrame);
        }

        private void btn_load_Click(object sender, EventArgs e)
        {
            StartPythonServer();
            MessageBox.Show("AI Server restarted.");
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            btn_load.PerformClick();
            LoadModels();
        }
        private void LoadModels()
        {
            // Lấy đường dẫn thư mục thực thi (bin\Debug hoặc bin\Release)
            string appPath = AppDomain.CurrentDomain.BaseDirectory;
            string dataPath = Path.Combine(appPath, "Data");

            if (Directory.Exists(dataPath))
            {
                // Lấy danh sách đường dẫn các thư mục con
                string[] subDirectories = Directory.GetDirectories(dataPath);

                // Chỉ lấy tên thư mục (bỏ phần đường dẫn dài) để đưa vào ComboBox
                List<string> folderNames = new List<string>();
                foreach (string dir in subDirectories)
                {
                    folderNames.Add(Path.GetFileName(dir));
                }

                cb_choose_Model.DataSource = folderNames;
            }
            else
            {
                MessageBox.Show("Không tìm thấy thư mục Data!", "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void cb_choose_Model_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cb_choose_Model.SelectedItem == null) return;

            string selectedFolder = cb_choose_Model.SelectedItem.ToString();
            string appPath = AppDomain.CurrentDomain.BaseDirectory;

            // Đường dẫn file nguồn: Data\{Thư mục chọn}\best.pt
            string sourceFile = Path.Combine(appPath, "Data", selectedFolder, "best.pt");

            // Đường dẫn thư mục đích: AI_Server
            string targetDir = Path.Combine(appPath, "AI_Server");
            string targetFile = Path.Combine(targetDir, "best.pt");

            try
            {
                // Kiểm tra nếu thư mục AI_Server chưa tồn tại thì tạo mới
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                // Kiểm tra file best.pt có tồn tại trong thư mục đã chọn không
                if (File.Exists(sourceFile))
                {
                    // Copy và ghi đè (true)
                    File.Copy(sourceFile, targetFile, true);
                    using (var client = new HttpClient())
                    {
                        var response = await client.PostAsync("http://127.0.0.1:8000/reload_model", null);
                        if (response.IsSuccessStatusCode)
                        {
                            MessageBox.Show($"Đã cập nhật model: {selectedFolder}");
                        }
                    }
                }
                else
                {
                    MessageBox.Show($"Không tìm thấy file best.pt trong thư mục {selectedFolder}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Có lỗi xảy ra khi copy file: " + ex.Message);
            }
        }

        private async void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            _isClosing = true;
            StopCamera();
            try
            {
                StopPythonServer();
                using (var client = new HttpClient())
                {
                    // Set timeout thấp để không chờ đợi lâu nếu server đã tắt
                    client.Timeout = TimeSpan.FromSeconds(2);
                    await client.PostAsync("http://127.0.0.1:8000/shutdown", null);
                }
            }
            catch { /* Bỏ qua lỗi nếu server không phản hồi */ }
        }
    }
    public class DetectResult
    {
        public int ok_count { get; set; }
        public int ng_count { get; set; }
        public string image_base64 { get; set; }
    }
}
