using Concentus;

namespace PocketCast.Core.Audio;

/// <summary>
/// Opus 解码：手机端 AudioRecord 48kHz 单声道 10ms 帧 → float PCM。
/// </summary>
public sealed class OpusAudioDecoder : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int FrameSamples10Ms = 480;
    private const int MaxFrameSamples = 960;

    private readonly IOpusDecoder _decoder;
    private readonly float[] _pcm = new float[MaxFrameSamples];
    private bool _disposed;

    public long PacketsDecoded { get; private set; }
    public long DecodeErrors { get; private set; }

    public OpusAudioDecoder()
    {
        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
        _decoder.Gain = 0;
    }

    /// <summary>解码一个 Opus 包，返回 48kHz 单声道 float 样本数。</summary>
    public int Decode(byte[] packet, out float[] pcm)
    {
        pcm = _pcm;
        if (_disposed || packet.Length == 0) return 0;
        try
        {
            int n = _decoder.Decode(packet.AsSpan(), _pcm.AsSpan(), MaxFrameSamples);
            if (n > 0) PacketsDecoded++;
            return Math.Max(0, n);
        }
        catch
        {
            DecodeErrors++;
            return 0;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _decoder.Dispose(); } catch { }
    }
}
