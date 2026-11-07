using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PocketCast.Core.Audio;

/// <summary>
/// 把解码后的 PCM 实时写入 VB-CABLE 的播放端（"CABLE Input"）。
/// 不写任何驱动，会议软件把 "CABLE Output" 选为麦克风即可。
/// </summary>
public sealed class VirtualCableAudioSink : IDisposable
{
    private WasapiOut? _out;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider? _chain;
    private MMDevice? _device;
    private string _mixInfo = "";
    private bool _disposed;

    public string DeviceName { get; private set; } = "";
    public bool IsOpen => _out != null && _out.PlaybackState == PlaybackState.Playing;
    public bool DeviceFound => _device != null;
    public string MixFormat => _mixInfo;
    public long SamplesWritten { get; private set; }
    public long FramesDropped { get; private set; }

    public static readonly string[] PreferredNameKeys = { "CABLE Input", "VoiceMeeter Input", "VB-Audio" };

    /// <summary>系统里是否存在可用的虚拟声卡（VB-CABLE / VoiceMeeter）。</summary>
    public static bool IsCablePresent()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return FindCableDevice(enumerator) != null;
        }
        catch
        {
            return false;
        }
    }

    public static MMDevice? FindCableDevice(MMDeviceEnumerator enumerator)
    {
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        foreach (var key in PreferredNameKeys)
        {
            var hit = devices.FirstOrDefault(d => d.FriendlyName.Contains(key, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return null;
    }

    public bool Open()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            _device = FindCableDevice(enumerator);
            if (_device is null) return false;
            DeviceName = _device.FriendlyName;

            var mix = _device.AudioClient.MixFormat;
            // VB-CABLE / VoiceMeeter 的混音格式是 WAVE_FORMAT_EXTENSIBLE(float32)，
            // 这时 mix.Encoding 是 Extensible 而不是 IeeeFloat。之前按 Encoding 判断会退化成
            // 16bit PCM 送进 shared 模式，WASAPI 不报错但完全没有声音（虚拟麦克风录到全 0）。
            bool isFloat = mix.BitsPerSample == 32;
            _mixInfo = $"{mix.SampleRate}Hz {mix.Channels}ch {(isFloat ? "float32" : "pcm16")}";

            _buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(OpusAudioDecoder.SampleRate, 1))
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(400),
                // 没有新数据时补静音：虚拟麦克风流不能断，也不能让 WASAPI 欠载后停摆
                ReadFully = true
            };

            ISampleProvider chain = new MonoToStereoProvider(_buffer.ToSampleProvider());
            if (mix.SampleRate != OpusAudioDecoder.SampleRate)
                chain = new WdlResamplingSampleProvider(chain, mix.SampleRate);

            IWaveProvider waveOut = isFloat
                ? chain.ToWaveProvider()
                : chain.ToWaveProvider16();

            _chain = chain;
            _out = new WasapiOut(_device, AudioClientShareMode.Shared, true, 40);
            _out.Init(waveOut);
            _out.Play();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Audio] 打开 VB-CABLE 失败：{ex.Message}");
            Close();
            return false;
        }
    }

    /// <summary>写入 48kHz 单声道 float PCM。</summary>
    public void Write(float[] pcm, int samples)
    {
        if (_buffer is null || samples <= 0) return;
        if (_buffer.BufferedDuration.TotalMilliseconds > 150)
        {
            // 手机与声卡时钟略有漂移时丢弃积压，避免延迟越积越大
            FramesDropped += samples;
            return;
        }
        var bytes = new byte[samples * 4];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        _buffer.AddSamples(bytes, 0, bytes.Length);
        SamplesWritten += samples;
    }

    public void Close()
    {
        try { _out?.Stop(); } catch { }
        try { _out?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        _out = null;
        _device = null;
        _buffer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }
}

/// <summary>单声道 → 立体声（复制声道）。</summary>
public sealed class MonoToStereoProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float[] _temp = Array.Empty<float>();

    public MonoToStereoProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        if (_temp.Length < frames) _temp = new float[frames * 2];
        int read = _source.Read(_temp, 0, frames);
        for (int i = 0; i < read; i++)
        {
            buffer[offset + i * 2] = _temp[i];
            buffer[offset + i * 2 + 1] = _temp[i];
        }
        return read * 2;
    }
}
