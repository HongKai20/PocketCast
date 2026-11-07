namespace PocketCast.Core.Video;

/// <summary>
/// H.264 解码入口：优先用系统 Media Foundation 解码器（硬件/低功耗），
/// 系统里没有（Windows N/KN、精简 LTSC 等）时自动回退到内置 OpenH264 软件解码。
/// </summary>
public sealed class H264Decoder : IDisposable
{
    private MfH264Decoder _system = new();
    private OpenH264Decoder _software = new();
    private bool _resolved;
    private bool _useSystem;
    private int _resolveAttempts;
    private bool _disposed;
    private string? _systemError;

    public H264Decoder()
    {
        _system.FrameDecoded += Forward;
        _software.FrameDecoded += Forward;
    }

    /// <summary>
    /// 重新选解码器并清空状态（丢包后长时间无法恢复时调用，
    /// 让下一帧的 SPS/PPS 重新初始化解码器）。
    /// </summary>
    public void Reset()
    {
        var oldSystem = _system;
        var oldSoftware = _software;
        _system = new MfH264Decoder();
        _software = new OpenH264Decoder();
        _system.FrameDecoded += Forward;
        _software.FrameDecoded += Forward;
        oldSystem.Dispose();
        oldSoftware.Dispose();
        _resolved = false;
        _useSystem = false;
        _resolveAttempts = 0;
        _systemError = null;
    }

    private void Forward(byte[] yuy2, int width, int height, int stride)
        => FrameDecoded?.Invoke(yuy2, width, height, stride);

    public event Action<byte[], int, int, int>? FrameDecoded;

    /// <summary>实际启用的解码器确定后触发一次（用于写日志 / UI）。</summary>
    public event Action<string>? DecoderResolved;

    public string DecoderName => _useSystem ? _system.DecoderName : _software.DecoderName;
    public long DecodedFrames => _useSystem ? _system.DecodedFrames : _software.DecodedFrames;
    public long FailedFrames => _useSystem ? _system.FailedFrames : _software.FailedFrames;
    public double LastDecodeMs => _useSystem ? _system.LastDecodeMs : _software.LastDecodeMs;
    public int Width => _useSystem ? _system.Width : _software.Width;
    public int Height => _useSystem ? _system.Height : _software.Height;
    public bool UsingSystemDecoder => _useSystem;

    public string? LastError
    {
        get
        {
            if (!_resolved) return _systemError;
            if (_useSystem) return _system.LastError;
            return _software.LastError ?? _systemError;
        }
    }

    /// <summary>送入一个 Annex-B 访问单元（首帧时自动选解码器，SPS/PPS 从码流里取）。</summary>
    public void Decode(byte[] annexB)
    {
        if (_disposed || annexB.Length == 0) return;

        if (!_resolved)
        {
            _resolveAttempts++;
            if (_system.EnsureReady(annexB))
            {
                _resolved = true;
                _useSystem = true;
                DecoderResolved?.Invoke($"{_system.DecoderName}（系统解码器）");
            }
            else
            {
                _systemError = _system.LastError;
                if (_software.Initialize())
                {
                    _resolved = true;
                    _useSystem = false;
                    DecoderResolved?.Invoke($"{_software.DecoderName}，已替代不可用的系统解码器");
                }
                else if (_resolveAttempts >= 3)
                {
                    _resolved = true;   // 两种都不可用，别每帧重复尝试
                    DecoderResolved?.Invoke("不可用：" + (_software.LastError ?? _systemError ?? "未知原因"));
                }
            }
        }

        if (!_resolved) return;
        if (_useSystem) _system.Decode(annexB);
        else _software.Decode(annexB);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _system.Dispose();
        _software.Dispose();
    }
}
