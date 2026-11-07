using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PocketCast.Core;
using PocketCast.Core.Pairing;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Orientation = System.Windows.Controls.Orientation;
using FontFamily = System.Windows.Media.FontFamily;

namespace PocketCast.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        app.Run(new MainWindow());
    }
}

public sealed class MainWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x16));
    private static readonly Brush Card = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x22));
    private static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x58, 0xA6, 0xFF));
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22));
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketCast", "pc.log");

    private readonly PcSession _session;
    private readonly Image _qrImage = new();
    private readonly TextBlock _ipText = new();
    private readonly TextBlock _tokenText = new();
    private readonly TextBlock _statusText = new();
    private readonly TextBlock _hintText = new();
    private readonly TextBox _logBox = new();
    private Button _disconnectBtn = new();
    private string _vcamText = "检测中";
    private string? _shownToken;
    private string? _audioNote;
    private bool _audioInstallLaunched;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _audioTimer;
    private WinForms.NotifyIcon? _tray;
    private bool _reallyExit;

    public MainWindow()
    {
        Title = "PocketCast 电脑端";
        Width = 480;
        Height = 830;
        Background = Bg;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _session = new PcSession(new PcSessionOptions
        {
            SignalingPort = 8765,
            PublishMdns = true
        });

        BuildUi();
        LoadQr();
        RefreshQrAddress();

        _session.Log += AppendLog;
        _session.StateChanged += () => Dispatcher.Invoke(RefreshStatus);
        _session.ProbeCompleted += r => AppendLog(r.ToString());

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();

        // VB-CABLE 装好（或刚重启）后自动接上音频输出，不用重开程序
        _audioTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _audioTimer.Tick += (_, _) => AudioTick();
        _audioTimer.Start();

        SetupTray();
        AppendLog("电脑端已启动。手机扫码或手动输入 IP + 配对码即可连接。");
        AppendLog($"配对码：{_session.Token}  地址：{_session.LanIp}:{_session.Port}");
        AppendLog($"系统：{Environment.OSVersion.VersionString}；日志文件：{LogPath}");

        // 首次启动自动安装虚拟摄像头（写 HKCU，无需管理员）
        var driver = VirtualCameraInstaller.Ensure();
        _vcamText = driver.StatusText;
        AppendLog(driver.LogText);

        GuideAudioDriver();
    }

    /// <summary>首次启动检查虚拟声卡：缺失时询问是否自动下载官方安装包。</summary>
    private void GuideAudioDriver()
    {
        if (_session.AudioDeviceReady)
        {
            AppendLog($"音频输出设备：{_session.AudioDeviceName}（已就绪）");
            return;
        }

        AppendLog("音频输出设备：未找到 VB-CABLE（虚拟麦克风需要它）。");
        var answer = WinForms.MessageBox.Show(
            "未检测到虚拟声卡 VB-CABLE，虚拟麦克风需要它才能工作。\n\n" +
            "是否现在下载并打开官方安装程序？\n" +
            "（安装需要管理员权限，装完后需要重启电脑）",
            "PocketCast · 安装虚拟声卡",
            WinForms.MessageBoxButtons.YesNo,
            WinForms.MessageBoxIcon.Question);
        if (answer == WinForms.DialogResult.Yes)
        {
            InstallAudioDriver();
        }
        else
        {
            AppendLog("已跳过：之后可点「安装 VB-CABLE」按钮，或从 https://vb-audio.com/Cable/ 手动安装。");
        }
    }

    private void InstallAudioDriver()
    {
        if (_audioInstallLaunched) return;
        _audioInstallLaunched = true;
        _audioNote = "准备安装…";
        AppendLog("开始准备 VB-CABLE 安装程序…");
        Task.Run(() =>
        {
            var r = VirtualCableInstaller.Install(AppendLog);
            Dispatcher.BeginInvoke(() =>
            {
                if (r.Outcome != VirtualCableInstaller.Outcome.Started) _audioInstallLaunched = false;
                _audioNote = r.Outcome switch
                {
                    VirtualCableInstaller.Outcome.Started => "安装中，等待完成…",
                    VirtualCableInstaller.Outcome.Cancelled => "已取消安装",
                    _ => "安装失败"
                };
                AppendLog(r.Message);
                RefreshStatus();
            });
        });
    }

    /// <summary>每 5 秒检查一次：设备出现了就接上，驱动装完但没重启就提示重启。</summary>
    private void AudioTick()
    {
        if (_session.AudioDeviceReady)
        {
            _audioNote = null;
            _audioTimer.Stop();
            return;
        }

        if (_session.TryReopenAudio())
        {
            _audioNote = null;
            AppendLog($"已检测到 VB-CABLE：{_session.AudioDeviceName}，虚拟麦克风已就绪。");
            _audioTimer.Stop();
            RefreshStatus();
            return;
        }

        if (_audioInstallLaunched && VirtualCableInstaller.IsDriverInstalled())
        {
            _audioInstallLaunched = false;
            _audioNote = "已安装，重启电脑后生效";
            AppendLog("VB-CABLE 驱动已安装完成：请重启电脑，之后虚拟麦克风即可使用。");
            RefreshStatus();
        }
    }

    // ---------- UI ----------

    private void BuildUi()
    {
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        root.Children.Add(TitleLine());
        root.Children.Add(SubLine());
        root.Children.Add(PairCard());
        root.Children.Add(StatusCard());
        root.Children.Add(ButtonRow());
        root.Children.Add(LogCard());

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        };
        Content = scroll;
    }

    private UIElement TitleLine()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        sp.Children.Add(MakeText("PocketCast", 24, Text, true));
        var badge = MakeText("手机 → 虚拟麦克风 / 摄像头", 12, Accent);
        badge.Margin = new Thickness(10, 8, 0, 0);
        sp.Children.Add(badge);
        return sp;
    }

    private UIElement SubLine()
    {
        var tb = MakeText($"电脑：{Environment.MachineName}    仅局域网 · 不上传公网", 12, Muted);
        tb.Margin = new Thickness(0, 0, 0, 14);
        return tb;
    }

    private UIElement PairCard()
    {
        var card = new Border
        {
            Background = Card,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 16, 18, 16)
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };

        _qrImage.Width = 200;
        _qrImage.Height = 200;
        _qrImage.Stretch = Stretch.Uniform;
        row.Children.Add(_qrImage);

        var right = new StackPanel { Margin = new Thickness(18, 4, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        right.Children.Add(MakeText("用手机 App 扫码连接", 15, Text, true));
        _ipText.FontFamily = new FontFamily("Consolas");
        _ipText.FontSize = 19;
        _ipText.Foreground = Accent;
        _ipText.Margin = new Thickness(0, 10, 0, 4);
        right.Children.Add(_ipText);

        _tokenText.FontFamily = new FontFamily("Consolas");
        _tokenText.FontSize = 13;
        _tokenText.Foreground = Muted;
        _tokenText.Margin = new Thickness(0, 0, 0, 0);
        right.Children.Add(_tokenText);

        row.Children.Add(right);
        card.Child = row;
        return card;
    }

    private UIElement StatusCard()
    {
        var card = new Border
        {
            Background = Card,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 14, 18, 14),
            Margin = new Thickness(0, 14, 0, 0)
        };

        _statusText.FontFamily = new FontFamily("Consolas");
        _statusText.FontSize = 13;
        _statusText.Foreground = Text;
        _statusText.LineHeight = 24;

        _hintText.FontSize = 12;
        _hintText.Foreground = Muted;
        _hintText.TextWrapping = TextWrapping.Wrap;
        _hintText.Margin = new Thickness(0, 8, 0, 0);
        _hintText.Text = "提醒：手机端只有把麦克风/摄像头开关打开时才真正采集；关闭后立即释放硬件。虚拟设备常驻，空闲时输出静音与纯黑帧。";

        var sp = new StackPanel();
        sp.Children.Add(_statusText);
        sp.Children.Add(_hintText);
        card.Child = sp;
        return card;
    }

    private UIElement ButtonRow()
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };

        var qr = MakeButton("重新生成二维码", () => { _session.RotateToken(); LoadQr(); RefreshQrAddress(); AppendLog("已生成新的配对码，旧二维码失效"); });
        var copy = MakeButton("复制配对信息", () =>
        {
            var text = $"PocketCast 电脑 {Environment.MachineName}\nIP: {_session.LanIp}\n端口: {_session.Port}\n配对码: {_session.Token}";
            try { Clipboard.SetText(text); AppendLog("配对信息已复制到剪贴板"); } catch { }
        });
        _disconnectBtn = MakeButton("断开手机", () => { _session.DisconnectPhone(); AppendLog("已请求断开手机"); });
        var clear = MakeButton("清空日志", () => _logBox.Clear());

        foreach (var b in new[] { qr, copy, _disconnectBtn, clear }) wrap.Children.Add(b);
        if (!_session.AudioDeviceReady)
            wrap.Children.Add(MakeButton("安装 VB-CABLE", InstallAudioDriver));
        return wrap;
    }

    private UIElement LogCard()
    {
        var card = new Border
        {
            Background = Card,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 14, 0, 0)
        };

        _logBox.Height = 170;
        _logBox.IsReadOnly = true;
        _logBox.AcceptsReturn = true;
        _logBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _logBox.Background = Card;
        _logBox.Foreground = Muted;
        _logBox.BorderThickness = new Thickness(0);
        _logBox.FontFamily = new FontFamily("Consolas");
        _logBox.FontSize = 12;

        card.Child = _logBox;
        return card;
    }

    private static TextBlock MakeText(string text, double size, Brush brush, bool bold = false)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = brush,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
        };
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var b = new System.Windows.Controls.Button
        {
            Content = text,
            Height = 34,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 0, 8, 8),
            Background = new SolidColorBrush(Color.FromRgb(0x21, 0x26, 0x2D)),
            Foreground = Text,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3D)),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    // ---------- 数据刷新 ----------

    private void LoadQr()
    {
        try
        {
            var png = QrCodeFactory.Png(_session.QrPayload, 6);
            var img = new BitmapImage();
            using var ms = new MemoryStream(png);
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            _qrImage.Source = img;
        }
        catch (Exception ex)
        {
            AppendLog("二维码生成失败：" + ex.Message);
        }
    }

    private void RefreshQrAddress()
    {
        _ipText.Text = $"{_session.LanIp?.ToString() ?? "未获取到局域网 IP"}:{_session.Port}";
        _tokenText.Text = $"配对码：{_session.Token}（扫码一次即失效）";
    }

    private void RefreshStatus()
    {
        // token 在手机配对成功时会在服务端轮换（一次性），这里发现变了就立刻重画二维码，
        // 否则界面上还是一张已经失效的旧码，手机再扫就会「配对码错误或已失效」。
        if (_shownToken != _session.Token)
        {
            _shownToken = _session.Token;
            LoadQr();
            RefreshQrAddress();
        }

        var s = _session;
        var sb = new System.Text.StringBuilder();

        sb.Append(s.PhoneConnected ? "● " : "○ ")
          .Append("手机：")
          .Append(s.PhoneConnected ? $"{s.PhoneName}  {s.PhoneAddress}" : "未连接")
          .Append("      配对码：").Append(s.Token).Append('\n');

        sb.Append("● 麦克风：").Append(s.AudioActive ? "开（推送 Opus 48kHz 单声道）" : "关（已释放手机麦克风）").Append('\n');
        sb.Append("● 摄像头：").Append(s.VideoActive ? "开（推送 H.264 视频）" : "关（已释放手机摄像头）").Append('\n');
        sb.Append("● 虚拟摄像头：").Append(_vcamText).Append('\n');
        sb.Append("● 音频设备：")
          .Append(s.AudioDeviceReady ? s.AudioDeviceName : _audioNote ?? "未找到 VB-CABLE（可点「安装 VB-CABLE」）")
          .Append('\n');
        sb.Append("● 视频画面：").Append(s.VideoState == VideoMode.Live ? "实时" : "空闲黑帧")
          .Append("，H.264 解码 ").Append(s.Decoder.DecodedFrames).Append(" 帧\n");
        sb.Append("● 收到媒体：音频 RTP ").Append(s.AudioPackets).Append(" 包 · 视频 RTP ")
          .Append(s.VideoPackets).Append(" 包\n");
        sb.Append("● 延迟：")
          .Append(s.ClockSync.Valid ? $"RTT {s.ClockSync.RoundTripMs:F1} ms" : "未同步")
          .Append("     RTP 丢包 ").Append(s.H264.PacketsLost).Append(" / ").Append(s.H264.PacketsReceived);
        if (s.Probe.LastResult is { } r) sb.Append("\n● 最近实测：").Append(r.ToString());

        _statusText.Text = sb.ToString();
        _disconnectBtn.IsEnabled = s.PhoneConnected;

        if (_tray != null)
            _tray.Text = s.PhoneConnected ? $"PocketCast · 已连接 {s.PhoneName}" : "PocketCast · 等待手机连接";
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(message));
            return;
        }
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _logBox.AppendText(line + Environment.NewLine);
        _logBox.ScrollToEnd();

        // 同时落盘，方便排查（超过 2MB 自动重写）
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch { }
    }

    // ---------- 托盘 ----------

    private void SetupTray()
    {
        try
        {
            _tray = new WinForms.NotifyIcon
            {
                Icon = BuildTrayIcon(),
                Visible = true,
                Text = "PocketCast · 等待手机连接"
            };
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("显示窗口", null, (_, _) => ShowFromTray());
            menu.Items.Add("断开手机", null, (_, _) => _session.DisconnectPhone());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => ExitApp());
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => ShowFromTray();
        }
        catch (Exception ex)
        {
            AppendLog("托盘图标创建失败：" + ex.Message);
        }
    }

    private static Drawing.Icon BuildTrayIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x1F, 0x6F, 0xEB));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var fg = new Drawing.SolidBrush(Drawing.Color.White);
            using var font = new Drawing.Font("Segoe UI", 17, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            var fmt = new Drawing.StringFormat
            {
                Alignment = Drawing.StringAlignment.Center,
                LineAlignment = Drawing.StringAlignment.Center
            };
            g.DrawString("P", font, fg, new Drawing.RectangleF(0, 0, 32, 30), fmt);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide();
            AppendLog("已最小化到托盘，虚拟设备仍在工作。");
            return;
        }

        _timer.Stop();
        try { _session.Dispose(); } catch { }
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.OnClosing(e);
        Application.Current.Shutdown();
    }
}
