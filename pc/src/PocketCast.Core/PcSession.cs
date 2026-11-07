using System.Net;
using PocketCast.Core.Audio;
using PocketCast.Core.Latency;
using PocketCast.Core.Pairing;
using PocketCast.Core.Protocol;
using PocketCast.Core.Video;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using VideoClock = PocketCast.Core.Video.Clock;

namespace PocketCast.Core;

public sealed class PcSessionOptions
{
    public int SignalingPort { get; init; } = 8765;
    public string? PcName { get; init; }
    public bool PublishMdns { get; init; } = true;
}

public enum VideoMode { Idle, Live }

/// <summary>
/// 电脑端会话：信令 + WebRTC 接收 + 解码 + 写 VB-CABLE / 共享内存。
/// </summary>
public sealed class PcSession : IDisposable
{
    private readonly PcSessionOptions _options;
    private readonly SignalingServer _signaling;
    private readonly FrameBufferWriter _frames;
    private readonly H264Decoder _decoder = new();
    private readonly H264Depacketizer _h264 = new();
    private readonly OpusAudioDecoder _opus = new();
    private readonly VirtualCableAudioSink _sink = new();
    private readonly ClockSyncState _clock = new();
    private readonly ProbeAnalyzer _probe;
    private readonly MdnsPublisher? _mdns;
    private readonly System.Threading.Timer _idleTimer;
    private readonly System.Threading.Timer _probeTimer;

    private RTCPeerConnection? _pc;
    private PhoneLink? _link;
    private long _lastAudioMs = -10000;
    private long _lastVideoMs = -10000;
    private long _lastPacketMs;
    private long _audioPackets;
    private long _videoPackets;
    private long _lastStatsMs;
    private long _lastDecodedFrames;
    private long _lastFrameAdvanceMs;
    private long _lastPliMs;
    private bool _decoderReset;
    private long _lastRefreshMs;
    private bool _firstAudioLogged;
    private bool _audioVoiceLogged;
    private double _audioRms;
    private bool _firstVideoLogged;
    private bool _decoderErrorLogged;
    private ushort _audioSeq;
    private bool _disposed;

    public string PcName { get; }
    public IPAddress? LanIp { get; private set; }
    public string Token => _signaling.Token;
    public int Port => _options.SignalingPort;
    public VideoMode VideoState { get; private set; } = VideoMode.Idle;
    public bool AudioActive => _link?.AudioActive ?? false;
    public bool VideoActive => _link?.VideoActive ?? false;
    public bool PhoneConnected => _link != null;
    public string PhoneName => _link?.DeviceName ?? "";
    public string PhoneAddress => _link?.RemoteAddress ?? "";
    public string AudioDeviceName => _sink.DeviceName;
    public bool AudioDeviceReady => _sink.IsOpen;
    public ClockSyncState ClockSync => _clock;
    public ProbeAnalyzer Probe => _probe;
    public H264Depacketizer H264 => _h264;
    public H264Decoder Decoder => _decoder;
    public VirtualCableAudioSink AudioSink => _sink;
    public FrameBufferWriter Frames => _frames;

    public long LastPacketAgeMs => _lastPacketMs == 0 ? -1 : VideoClock.MsNow - _lastPacketMs;

    /// <summary>收到的音频 / 视频 RTP 包数量（诊断用）。</summary>
    public long AudioPackets => _audioPackets;
    public long VideoPackets => _videoPackets;

    /// <summary>VB-CABLE 是后来才装好（或刚重启）的，重新尝试打开音频输出。</summary>
    public bool TryReopenAudio()
    {
        if (_sink.IsOpen) return true;
        if (!_sink.Open()) return false;
        Log?.Invoke($"VB-CABLE 已就绪：{_sink.DeviceName}");
        StateChanged?.Invoke();
        return true;
    }

    public event Action? StateChanged;
    public event Action<string>? Log;
    public event Action<Latency.ProbeAnalyzer.Result>? ProbeCompleted;

    public PcSession(PcSessionOptions options)
    {
        _options = options;
        PcName = options.PcName ?? Environment.MachineName;
        _probe = new ProbeAnalyzer(_clock);
        _probe.Completed += r => { ProbeCompleted?.Invoke(r); StateChanged?.Invoke(); };

        _frames = new FrameBufferWriter();
        _decoder.DecoderResolved += name => Log?.Invoke("视频解码器：" + name);
        _decoder.FrameDecoded += (yuy2, w, h, stride) =>
        {
            try { _frames.Publish(yuy2, w, h, stride, live: true); } catch { }
            _probe.OnVideo(yuy2, w, h, stride, Clock.PreciseMs);
        };
        if (!_sink.Open()) Log?.Invoke("未找到 VB-CABLE：音频会解码但不输出，请先安装 VB-CABLE（见 README）");
        // 解码器在收到第一帧时按码流里的 SPS/PPS 懒初始化（见 MfH264Decoder.EnsureReady）

        LanIp = LanInfo.PrimaryIPv4();
        _signaling = new SignalingServer(options.SignalingPort);
        _signaling.PhoneConnected += OnPhoneConnected;
        _signaling.PhoneDisconnected += OnPhoneDisconnected;
        _signaling.MessageReceived += OnMessage;

        if (options.PublishMdns)
        {
            try
            {
                var addrs = LanInfo.AllIPv4();
                _mdns = new MdnsPublisher(PcName, options.SignalingPort, addrs);
                _mdns.Start();
            }
            catch (Exception ex) { Log?.Invoke("mDNS 广播失败：" + ex.Message); }
        }

        _idleTimer = new System.Threading.Timer(_ => IdleTick(), null, 33, 33);
        _probeTimer = new System.Threading.Timer(_ => { _probe.Poll(VideoClock.PreciseMs); }, null, 50, 50);
        Log?.Invoke($"信令端口 {Port}，局域网 IP {LanIp}");
    }

    public string QrPayload => LanIp is null
        ? $"{{\"v\":1,\"host\":\"127.0.0.1\",\"port\":{Port},\"token\":\"{Token}\",\"name\":\"{PcName}\"}}"
        : LanInfo.QrPayload(PcName, LanIp, Port, Token);

    public string RotateToken() => _signaling.RotateToken();

    /// <summary>一键断开当前手机（虚拟设备保持开机，继续输出静音/黑帧）。</summary>
    public void DisconnectPhone()
    {
        var link = _link;
        if (link is null) return;
        try { link.Send(SignalType.Bye, ("reason", "电脑端主动断开")); } catch { }
        try { link.Close(); } catch { }
    }

    private void OnPhoneConnected(PhoneLink link)
    {
        _link = link;
        _clock.Reset();
        _audioPackets = 0;
        _videoPackets = 0;
        _lastStatsMs = 0;
        _firstAudioLogged = false;
        _audioVoiceLogged = false;
        _audioRms = 0;
        _firstVideoLogged = false;
        _decoderErrorLogged = false;
        Log?.Invoke($"手机已连接：{link.DeviceName} ({link.RemoteAddress})");
        try { StartPeerConnection(link); }
        catch (Exception ex) { Log?.Invoke("创建 WebRTC 会话失败：" + ex.Message); }
        StateChanged?.Invoke();
    }

    private void StartPeerConnection(PhoneLink link)
    {
        var config = new RTCConfiguration { iceServers = new List<RTCIceServer>() };
        var pc = new RTCPeerConnection(config);
        _pc = pc;
        _audioSeq = 0;
        _h264.Reset();
        _lastDecodedFrames = 0;
        _lastFrameAdvanceMs = 0;
        _lastPliMs = 0;
        _lastRefreshMs = 0;
        _decoderReset = false;
        _decoderErrorLogged = false;

        var audioTrack = new MediaStreamTrack(new List<AudioFormat> { new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2) },
            MediaStreamStatusEnum.RecvOnly);
        pc.addTrack(audioTrack);

        var h264 = new VideoFormat(VideoCodecsEnum.H264, 102, 90000,
            "profile-level-id=42e01f;packetization-mode=1;level-asymmetry-allowed=1");
        var videoTrack = new MediaStreamTrack(new List<VideoFormat> { h264 }, MediaStreamStatusEnum.RecvOnly);
        pc.addTrack(videoTrack);

        pc.onicecandidate += cand =>
        {
            if (cand.candidate is null) return;
            link.Send(SignalType.Candidate, ("candidate", cand.candidate),
                ("sdpMid", cand.sdpMid ?? "0"), ("sdpMLineIndex", (int)cand.sdpMLineIndex));
        };

        pc.onconnectionstatechange += state =>
        {
            Log?.Invoke("WebRTC 连接状态：" + state);
            if (state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.disconnected)
                link.Send(SignalType.Bye, ("reason", state.ToString()));
            StateChanged?.Invoke();
        };

        pc.OnRtpPacketReceived += (ep, media, rtp) => OnRtp(media, rtp);

        var offer = pc.createOffer(new RTCOfferOptions());
        var setLocal = pc.setLocalDescription(offer);
        try { setLocal.Wait(1500); } catch { }
        link.Send(SignalType.Offer, ("sdp", offer.sdp));
        Log?.Invoke("已发送 SDP offer（H264/Opus，仅局域网）");
    }

    private void OnRtp(SDPMediaTypesEnum media, RTPPacket rtp)
    {
        long now = Clock.MsNow;
        _lastPacketMs = now;
        if (media == SDPMediaTypesEnum.audio)
        {
            _lastAudioMs = now;
            _audioPackets++;
            if (!_firstAudioLogged)
            {
                _firstAudioLogged = true;
                Log?.Invoke($"收到第一个音频 RTP 包（PT {rtp.Header.PayloadType}，{rtp.Payload.Length} 字节）");
            }
            int n = _opus.Decode(rtp.Payload, out var pcm);
            if (n > 0)
            {
                _sink.Write(pcm, n);
                _probe.OnAudio(pcm, n, Clock.PreciseMs);

                double rms = 0;
                for (int i = 0; i < n; i++) rms += pcm[i] * pcm[i];
                rms = Math.Sqrt(rms / n);
                _audioRms = _audioRms * 0.8 + rms * 0.2;
                if (!_audioVoiceLogged && _audioRms > 0.01)
                {
                    _audioVoiceLogged = true;
                    Log?.Invoke($"音频已出声：电平 {_audioRms:F2}，正在写入 {_sink.DeviceName}");
                }
            }
        }
        else if (media == SDPMediaTypesEnum.video)
        {
            _lastVideoMs = now;
            _videoPackets++;
            if (!_firstVideoLogged)
            {
                _firstVideoLogged = true;
                Log?.Invoke($"收到第一个视频 RTP 包（PT {rtp.Header.PayloadType}，{rtp.Payload.Length} 字节）");
            }
            _h264.OnSequence(rtp.Header.SequenceNumber);
            var au = _h264.Push(rtp.Payload, rtp.Header.MarkerBit != 0);
            if (au is null) return;
            _decoder.Decode(au);

            // 丢包后参考帧丢失时，解码器会一直吐不出画面，必须问手机要关键帧（RTCP PLI），
            // 否则画面会永久停住（实测停在 679 帧不再增长，而 RTP 仍在持续到达）。
            long frames = _decoder.DecodedFrames;
            if (frames != _lastDecodedFrames)
            {
                _lastDecodedFrames = frames;
                _lastFrameAdvanceMs = now;
                _decoderReset = false;
            }
            else if (_lastFrameAdvanceMs > 0 && now - _lastFrameAdvanceMs > 1000 && now - _lastPliMs > 1000)
            {
                if (now - _lastFrameAdvanceMs > 5000 && !_decoderReset)
                {
                    _decoderReset = true;
                    _decoder.Reset();
                    Log?.Invoke("画面停滞超过 5 秒，已重置解码器");
                }
                RequestKeyFrame(now);
                // PLI 有系统/实现差异，部分手机不收；停滞超过 3 秒再走信令让手机重启视频轨，
                // 重启编码器一定会产生新的 IDR。
                if (now - _lastFrameAdvanceMs > 3000 && now - _lastRefreshMs > 3000)
                {
                    _lastRefreshMs = now;
                    _link?.Send(SignalType.Keyframe);
                    Log?.Invoke("已通知手机重启视频编码（信令 keyframe）");
                }
            }

            if (_decoder.LastError is { } err && !_decoderErrorLogged)
            {
                _decoderErrorLogged = true;
                Log?.Invoke("H.264 解码异常：" + err);
            }
        }
    }

    /// <summary>通过 RTCP PLI 让手机立刻重发一个关键帧（IDR + SPS/PPS）。</summary>
    private void RequestKeyFrame(long now)
    {
        _lastPliMs = now;
        try
        {
            // SendRtcpFeedback 是 RTPSession 上的方法（MediaStreamTrack 没有）。
            // RTCPeerConnection 继承 RTPSession，音视频共用一条 RTP 通道，这里指定 video。
            _pc?.SendRtcpFeedback(SDPMediaTypesEnum.video, new RTCPFeedback(0u, 0u, PSFBFeedbackTypesEnum.PLI));
            Log?.Invoke("画面停滞，已向手机请求关键帧（PLI）");
        }
        catch (Exception ex)
        {
            Log?.Invoke("请求关键帧失败：" + ex.Message);
        }
    }

    private void OnMessage(PhoneLink link, Signal msg)
    {
        switch (msg.Type)
        {
            case SignalType.Answer:
            {
                var sdp = msg.Str("sdp");
                if (sdp is null || _pc is null) return;
                var res = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
                Log?.Invoke("已收到手机 answer：" + res);
                Log?.Invoke("手机 answer SDP：\n" + sdp);
                if (res != SetDescriptionResultEnum.OK)
                {
                    Log?.Invoke("本地 offer SDP：\n" + (_pc.localDescription?.ToString() ?? "(空)"));
                }
                break;
            }
            case SignalType.Candidate:
            {
                var cand = msg.Str("candidate");
                if (cand is null || _pc is null) return;
                _pc.addIceCandidate(new RTCIceCandidateInit
                {
                    candidate = cand,
                    sdpMid = msg.Str("sdpMid") ?? "0",
                    sdpMLineIndex = (ushort)(msg.Int("sdpMLineIndex") ?? 0)
                });
                break;
            }
            case SignalType.Control:
            {
                link.AudioActive = msg.Bool("audio") ?? link.AudioActive;
                link.VideoActive = msg.Bool("video") ?? link.VideoActive;
                link.RequestedHeight = msg.Int("height") ?? link.RequestedHeight;
                if (!link.VideoActive) SetVideoIdle("手机端关闭摄像头");
                if (!link.AudioActive) link.Send(SignalType.Ack, ("audio", false));
                Log?.Invoke($"手机端控制：麦克风={link.AudioActive} 摄像头={link.VideoActive} 分辨率={link.RequestedHeight}p");
                StateChanged?.Invoke();
                break;
            }
            case SignalType.Clock:
            {
                long t1 = msg.Long("t1") ?? 0;
                long now = Clock.MsNow;
                link.Send(SignalType.ClockAck, ("t1", t1), ("t2", now), ("t3", Clock.MsNow));
                break;
            }
            case SignalType.ClockResult:
            {
                double rtt = msg.Double("rtt") ?? 0;
                double offset = msg.Double("offset") ?? 0;
                _clock.Update(rtt, offset);
                Log?.Invoke($"时钟同步：RTT {rtt:F1}ms 偏移 {offset:F1}ms");
                StateChanged?.Invoke();
                break;
            }
            case SignalType.Probe:
            {
                long t0 = msg.Long("t0") ?? 0;
                if (!_clock.Valid) { link.Send(SignalType.ProbeResult, ("error", "时钟未同步")); return; }
                _probe.Arm(t0);
                Log?.Invoke("收到手机闪光/蜂鸣测试，开始测量");
                break;
            }
            case SignalType.Stats:
            {
                link.PhoneFps = msg.Double("fps") ?? 0;
                link.PhoneKbps = msg.Double("kbps") ?? 0;
                link.RequestedHeight = msg.Int("height") ?? link.RequestedHeight;
                StateChanged?.Invoke();
                break;
            }
            case SignalType.Bye:
                link.Send(SignalType.Bye, ("reason", "bye"));
                link.Close();
                break;
        }
    }

    private void OnPhoneDisconnected()
    {
        Log?.Invoke("手机已断开");
        _link = null;
        try { _pc?.close(); } catch { }
        _pc = null;
        _clock.Reset();
        SetVideoIdle("手机已断开");
        StateChanged?.Invoke();
    }

    private void SetVideoIdle(string reason)
    {
        VideoState = VideoMode.Idle;
        Log?.Invoke("视频转为空闲黑帧：" + reason);
    }

    private void IdleTick()
    {
        if (_disposed) return;
        try
        {
            long now = Clock.MsNow;
            if (_link != null && now - _lastStatsMs >= 5000)
            {
                _lastStatsMs = now;
                Log?.Invoke($"媒体统计：音频 RTP {_audioPackets} 包（解码 {_opus.PacketsDecoded} / 错误 {_opus.DecodeErrors} / 电平 {_audioRms:F2} / 写入 {_sink.SamplesWritten} 采样）" +
                            $" / 视频 RTP {_videoPackets} 包 / " +
                            $"解码 {_decoder.DecodedFrames} 帧 / 最近包 {(_lastPacketMs == 0 ? -1 : now - _lastPacketMs)} ms / " +
                            $"PC 状态 {(_pc is null ? "无" : _pc.connectionState.ToString())} / " +
                            $"解码器 {(_decoder.DecodedFrames > 0 ? "正常" : _decoder.LastError ?? "等待首帧")}");
            }
            bool liveVideo = VideoActive && (now - _lastVideoMs) < 800 && _decoder.DecodedFrames > 0;
            if (liveVideo)
            {
                VideoState = VideoMode.Live;
            }
            else
            {
                VideoState = VideoMode.Idle;
                _frames.PublishIdle();
            }

            // 音频停止时由 BufferedWaveProvider 的 ReadFully 自动补静音，
            // 不再手动写静音，避免把多余数据堆进缓冲、拉高启动延迟。
        }
        catch (Exception ex) { Log?.Invoke("空闲帧写入失败：" + ex.Message); }
    }

    public string[] Diagnostics()
    {
        return new[]
        {
            $"手机连接: {(PhoneConnected ? PhoneName + " " + _link?.RemoteAddress : "无")}",
            $"音频设备: {(_sink.IsOpen ? _sink.DeviceName : "未打开")} {_sink.MixFormat}",
            $"H.264: {_decoder.DecoderName} 解码 {_decoder.DecodedFrames} 帧 / 失败 {_decoder.FailedFrames}",
            $"RTP: 视频包 {_h264.PacketsReceived} 丢包 {_h264.PacketsLost} / Opus {_opus.PacketsDecoded} 包",
            $"共享内存: {FrameBufferWriter.MapName} {VideoState}",
            $"时钟同步: {(_clock.Valid ? $"RTT {_clock.RoundTripMs:F1}ms 偏移 {_clock.OffsetMs:F1}ms" : "未同步")}",
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _idleTimer.Dispose();
        _probeTimer.Dispose();
        try { _pc?.close(); } catch { }
        _signaling.Dispose();
        _mdns?.Dispose();
        _frames.Dispose();
        _decoder.Dispose();
        _opus.Dispose();
        _sink.Dispose();
    }
}
