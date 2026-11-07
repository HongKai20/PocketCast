using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PocketCast.Core.Audio;
using PocketCast.Core.Video;

namespace PocketCast.SelfTest;

/// <summary>
/// 本机管线自测：共享内存回环、Opus 编解码、VB-CABLE、H.264 解码器。
/// 端到端（手机 ↔ 电脑）延迟需要在真实设备上用 App 里的「延迟自检」测，方法见 docs/延迟测试报告.md。
/// </summary>
internal static class Program
{
    private static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("PocketCast 本机管线自测");
        Console.WriteLine(new string('-', 68));
        PrintEnvironment();
        Console.WriteLine();

        var shared = TestSharedMemory();
        var opus = TestOpus();
        TestAudioDevice();
        TestVideoDecoder();

        Console.WriteLine(new string('-', 68));
        Console.WriteLine("汇总");
        Console.WriteLine($"  共享内存 送帧 → 虚拟摄像头可读   中位 {shared.Median:F1} µs / P95 {shared.P95:F1} µs / 最大 {shared.Max:F1} µs");
        Console.WriteLine($"  共享内存 写入吞吐                {shared.MBps:F0} MB/s（720p YUY2 ≈ {720 * 1280 * 2 / 1024.0 / 1024:F1} MB/帧 × 30fps）");
        Console.WriteLine($"  Opus 48kHz 单声道 10ms 编+解     {opus.PerFrameUs:F1} µs/帧，占 10ms 预算 {opus.CpuPercent:F2}%");
        Console.WriteLine($"  Opus 压缩后码率                  {opus.Kbps:F1} kbps（目标 64 kbps）");
        Console.WriteLine();
        Console.WriteLine("说明：以上为本机管线的真实测量值；端到端延迟还包含采集、编码、网络、缓冲，");
        Console.WriteLine("      需在真实手机上用 App「延迟自检」（闪光 + 1kHz 蜂鸣）测量。");
    }

    private static void PrintEnvironment()
    {
        Console.WriteLine($"  CPU            {Environment.ProcessorCount} 逻辑核");
        Console.WriteLine($"  操作系统       {RuntimeInformation.OSDescription}");
        Console.WriteLine($"  .NET           {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"  进程架构       {RuntimeInformation.ProcessArchitecture}");
    }

    private readonly record struct SharedResult(double Median, double P95, double Max, double MBps, int Count);

    private static SharedResult TestSharedMemory()
    {
        Console.Write("  共享内存回环 … ");
        const int width = 1280, height = 720;
        const int bulkFrames = 300;
        const int pingPong = 200;

        using var writer = new FrameBufferWriter();
        var frame = new byte[width * height * 2];
        for (int i = 0; i < frame.Length; i += 2)
        {
            frame[i] = (byte)(32 + ((i / 2) % 200));
            frame[i + 1] = 0x80;
        }

        using var map = MemoryMappedFile.OpenExisting(FrameBufferWriter.MapName, MemoryMappedFileRights.Read);
        using var acc = map.CreateViewAccessor(0, FrameBufferWriter.TotalSize, MemoryMappedFileAccess.Read);

        // 1) 吞吐：连续写 300 帧 1280x720 YUY2
        ulong baseCount = acc.ReadUInt64(56);
        var bulk = Stopwatch.StartNew();
        for (int i = 0; i < bulkFrames; i++) writer.Publish(frame, width, height, width * 2);
        bulk.Stop();
        double mbps = bulkFrames * (width * height * 2.0) / (1024 * 1024) / Math.Max(bulk.Elapsed.TotalSeconds, 1e-6);

        // 2) 可见延迟：送帧后立刻读 frameCount，测「送帧 → 虚拟摄像头可读到新帧」的耗时
        var latencies = new List<double>(pingPong);
        var spin = new SpinWait();
        for (int i = 0; i < pingPong; i++)
        {
            ulong expected = baseCount + bulkFrames + (ulong)i + 1;
            long t0 = Stopwatch.GetTimestamp();
            writer.Publish(frame, width, height, width * 2);
            while (acc.ReadUInt64(56) != expected) spin.SpinOnce();
            long t1 = Stopwatch.GetTimestamp();
            latencies.Add((t1 - t0) * 1_000_000.0 / Stopwatch.Frequency);
        }

        var sorted = latencies.OrderBy(x => x).ToList();
        double median = sorted[sorted.Count / 2];
        double p95 = sorted[(int)(sorted.Count * 0.95)];
        double max = sorted[^1];

        Console.WriteLine($"完成：{sorted.Count} 次样本，吞吐 {mbps:F0} MB/s");
        return new SharedResult(median, p95, max, mbps, sorted.Count);
    }

    private readonly record struct OpusResult(double PerFrameUs, double CpuPercent, double Kbps);

    private static OpusResult TestOpus()
    {
        Console.Write("  Opus 编解码 … ");
        const int sampleRate = 48000;
        const int frameSamples = 480;         // 10ms
        const int iterations = 2000;

        var encoder = OpusCodecFactory.CreateEncoder(sampleRate, 1,
            OpusApplication.OPUS_APPLICATION_VOIP, (TextWriter?)null);
        encoder.Bitrate = 64000;
        encoder.UseInbandFEC = true;
        encoder.Complexity = 10;

        using var decoder = new OpusAudioDecoder();

        var pcm = new short[frameSamples];
        var encoded = new byte[2048];
        int totalBytes = 0;
        int encodedFrames = 0;

        for (int i = 0; i < 50; i++) EncodeDecode(0);   // 预热

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) EncodeDecode(i);
        sw.Stop();

        double perFrameUs = sw.Elapsed.TotalMicroseconds / iterations;
        double cpu = perFrameUs / 10000.0 * 100.0;
        double kbps = encodedFrames > 0 ? totalBytes * 8.0 / (encodedFrames * 0.010) / 1000.0 : 0;
        Console.WriteLine($"完成：{iterations} 帧");
        return new OpusResult(perFrameUs, cpu, kbps);

        void EncodeDecode(int seed)
        {
            for (int i = 0; i < frameSamples; i++)
            {
                double t = (seed * frameSamples + i) / (double)sampleRate;
                pcm[i] = (short)(12000 * Math.Sin(2 * Math.PI * 1000 * t));
            }
            int n = encoder.Encode(pcm.AsSpan(), frameSamples, encoded.AsSpan(), encoded.Length);
            if (n > 0)
            {
                totalBytes += n;
                encodedFrames++;
                decoder.Decode(encoded.AsSpan(0, n).ToArray(), out _);
            }
        }
    }

    private static void TestAudioDevice()
    {
        Console.Write("  VB-CABLE 音频设备 … ");
        try
        {
            using var sink = new VirtualCableAudioSink();
            if (!sink.Open())
            {
                Console.WriteLine("未找到 VB-CABLE / VoiceMeeter（请先安装 VB-CABLE 才能在会议软件里选麦克风）");
                return;
            }
            Console.WriteLine($"已就绪：{sink.DeviceName}（{sink.MixFormat}）");
            TestCableLoopback(sink);
        }
        catch (Exception ex)
        {
            Console.WriteLine("检测失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 回环验证：往 CABLE Input 写 1 秒 440Hz 正弦，同时从 CABLE Output 采集。
    /// 采集不到信号说明写入格式/驱动有问题（会议软件那边同样会录到全 0）。
    /// </summary>
    private static void TestCableLoopback(VirtualCableAudioSink sink)
    {
        Console.Write("  VB-CABLE 回环（写 CABLE Input → 采 CABLE Output）… ");
        try
        {
            using var en = new MMDeviceEnumerator();
            var capDev = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
            if (capDev is null)
            {
                Console.WriteLine("未找到 CABLE Output 采集端，跳过");
                return;
            }

            long peak = 0, samples = 0;
            bool isFloat = capDev.AudioClient.MixFormat.BitsPerSample == 32;
            using var cap = new WasapiCapture(capDev, true, 20);
            cap.DataAvailable += (_, e) =>
            {
                if (isFloat)
                    for (int i = 0; i + 4 <= e.BytesRecorded; i += 4)
                    {
                        long v = (long)(Math.Abs(BitConverter.ToSingle(e.Buffer, i)) * 32768);
                        if (v > peak) peak = v;
                        samples++;
                    }
                else
                    for (int i = 0; i + 2 <= e.BytesRecorded; i += 2)
                    {
                        long v = Math.Abs((long)BitConverter.ToInt16(e.Buffer, i));
                        if (v > peak) peak = v;
                        samples++;
                    }
            };
            cap.StartRecording();

            var tone = new float[OpusAudioDecoder.FrameSamples10Ms];
            for (int k = 0; k < 100; k++)
            {
                for (int i = 0; i < tone.Length; i++)
                    tone[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * (k * tone.Length + i) / OpusAudioDecoder.SampleRate);
                sink.Write(tone, tone.Length);
                Thread.Sleep(10);
            }
            Thread.Sleep(250);
            try { cap.StopRecording(); } catch { }
            sink.Close();

            Console.WriteLine(peak > 3280
                ? $"有信号：峰值 {peak}/32768，采集 {samples} 样本"
                : $"无信号：峰值 {peak}/32768，采集 {samples} 样本（虚拟声卡输出异常）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("回环失败：" + ex.Message);
        }
    }

    private static void TestVideoDecoder()
    {
        Console.Write("  系统 H.264 解码器 … ");
        try
        {
            using var decoder = new MfH264Decoder();
            Console.WriteLine(decoder.Initialize()
                ? $"可用：{decoder.DecoderName}"
                : $"不可用：{decoder.LastError}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("检测失败：" + ex.Message);
        }

        Console.Write("  内置 OpenH264 解码器 … ");
        try
        {
            using var decoder = new OpenH264Decoder();
            Console.WriteLine(decoder.Initialize()
                ? $"可用：{decoder.DecoderName}"
                : $"不可用：{decoder.LastError}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("检测失败：" + ex.Message);
        }
    }
}
