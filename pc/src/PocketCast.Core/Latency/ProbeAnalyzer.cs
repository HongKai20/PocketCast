namespace PocketCast.Core.Latency;

/// <summary>手机时钟 → 电脑时钟偏移（NTP 式四时间戳估算，局域网误差通常 &lt;2ms）。</summary>
public sealed class ClockSyncState
{
    public double OffsetMs { get; private set; }   // pc_ms = phone_ms + OffsetMs
    public double RoundTripMs { get; private set; }
    public bool Valid { get; private set; }

    public void Update(double rttMs, double offsetMs)
    {
        RoundTripMs = rttMs;
        OffsetMs = offsetMs;
        if (rttMs > 0 && rttMs < 50) Valid = true;
    }

    public double ToPcMs(long phoneMs) => phoneMs + OffsetMs;

    public void Reset()
    {
        Valid = false;
        OffsetMs = 0;
        RoundTripMs = 0;
    }
}

/// <summary>
/// 自动延迟测量：手机在 t0 同时闪光 + 播放 1kHz 蜂鸣，电脑在解码输出端检测到
/// 亮度跳变 / 音频能量跳变，两者时间差 = 手机采集编码 + 传输 + 解码 + 缓冲 的端到端延迟。
/// </summary>
public sealed class ProbeAnalyzer
{
    public sealed class Result
    {
        public double AudioMs { get; set; } = double.NaN;
        public double VideoMs { get; set; } = double.NaN;
        public long T0 { get; set; }

        public override string ToString()
        {
            string a = double.IsNaN(AudioMs) ? "音频 未检测到" : $"音频 {AudioMs:F0} ms";
            string v = double.IsNaN(VideoMs) ? "视频 未检测到" : $"视频 {VideoMs:F0} ms";
            return $"端到端延迟：{a} / {v}";
        }
    }

    private readonly ClockSyncState _clock;
    private double _expectedPcMs = -1;
    private double _searchDeadlineMs;
    private double _audioDetectMs = double.NaN;
    private double _videoDetectMs = double.NaN;
    private long _phoneT0;
    private volatile bool _active;
    private double _audioBaseline;
    private double _lumaBaseline;

    public event Action<Result>? Completed;

    /// <summary>最近一次测量结果（用于界面常驻显示）。</summary>
    public Result? LastResult { get; private set; }

    public ProbeAnalyzer(ClockSyncState clock) => _clock = clock;

    public bool Active => _active;

    public void Arm(long phoneT0)
    {
        _phoneT0 = phoneT0;
        _expectedPcMs = _clock.ToPcMs(phoneT0);
        _searchDeadlineMs = _expectedPcMs + 1200;
        _audioDetectMs = double.NaN;
        _videoDetectMs = double.NaN;
        _audioBaseline = double.NaN;
        _lumaBaseline = double.NaN;
        _active = true;
    }

    public void Cancel() => _active = false;

    public void OnAudio(float[] pcm, int samples, double pcMs)
    {
        if (!_active || !double.IsNaN(_audioDetectMs)) return;
        if (pcMs < _expectedPcMs - 5 || pcMs > _searchDeadlineMs) return;

        double rms = 0;
        for (int i = 0; i < samples; i++) rms += pcm[i] * pcm[i];
        rms = Math.Sqrt(rms / Math.Max(1, samples));

        if (double.IsNaN(_audioBaseline)) { _audioBaseline = rms; return; }
        if (rms > Math.Max(0.12, _audioBaseline * 4) && rms > 0.12)
            _audioDetectMs = pcMs - _expectedPcMs;
        else
            _audioBaseline = _audioBaseline * 0.8 + rms * 0.2;
    }

    public void OnVideo(byte[] yuy2, int width, int height, int stride, double pcMs)
    {
        if (!_active || !double.IsNaN(_videoDetectMs)) return;
        if (pcMs < _expectedPcMs - 5 || pcMs > _searchDeadlineMs) return;

        double sum = 0; int n = 0;
        int stepX = Math.Max(2, width / 64);
        for (int y = 0; y < height; y += 8)
        {
            int row = y * stride;
            for (int x = 0; x < width; x += stepX * 2)
            {
                int idx = row + x * 2;
                if (idx + 1 >= yuy2.Length) break;
                sum += yuy2[idx];
                n++;
            }
        }
        double mean = n > 0 ? sum / n : 0;
        if (double.IsNaN(_lumaBaseline)) { _lumaBaseline = mean; return; }
        if (mean > 190 && mean > _lumaBaseline + 40) _videoDetectMs = pcMs - _expectedPcMs;
        else _lumaBaseline = _lumaBaseline * 0.8 + mean * 0.2;
    }

    /// <summary>每 50ms 调用一次，超时或两项都命中后给出结果。</summary>
    public void Poll(double pcMs)
    {
        if (!_active) return;
        bool audioDone = !double.IsNaN(_audioDetectMs);
        bool videoDone = !double.IsNaN(_videoDetectMs);
        if ((audioDone && videoDone) || pcMs > _searchDeadlineMs)
        {
            _active = false;
            var result = new Result { AudioMs = _audioDetectMs, VideoMs = _videoDetectMs, T0 = _phoneT0 };
            LastResult = result;
            Completed?.Invoke(result);
        }
    }
}
