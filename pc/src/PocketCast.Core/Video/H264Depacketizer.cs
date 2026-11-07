using System.Diagnostics;

namespace PocketCast.Core.Video;

/// <summary>RFC 6184 H.264 RTP 解包（单 NAL / STAP-A / FU-A），输出 Annex-B 访问单元。</summary>
public sealed class H264Depacketizer
{
    private readonly List<byte[]> _nals = new();
    private byte[]? _sps;
    private byte[]? _pps;
    private byte[]? _fuNal;
    private bool _fuStarted;

    public long PacketsReceived { get; private set; }
    public long PacketsLost { get; private set; }
    private ushort? _lastSeq;

    public void OnSequence(ushort seq)
    {
        if (_lastSeq.HasValue)
        {
            int diff = (ushort)(seq - _lastSeq.Value);
            if (diff > 1 && diff < 100) PacketsLost += diff - 1;
        }
        _lastSeq = seq;
    }

    /// <summary>压入一个 RTP 负载；marker=true 时输出完整的一帧（Annex-B）。</summary>
    public byte[]? Push(ReadOnlySpan<byte> payload, bool marker)
    {
        PacketsReceived++;
        if (payload.Length == 0) return null;
        int nalType = payload[0] & 0x1F;

        switch (nalType)
        {
            case 24: // STAP-A
            {
                int i = 1;
                while (i + 2 <= payload.Length)
                {
                    int size = (payload[i] << 8) | payload[i + 1];
                    i += 2;
                    if (size <= 0 || i + size > payload.Length) break;
                    StoreNal(payload.Slice(i, size));
                    i += size;
                }
                break;
            }
            case 28: // FU-A
            {
                if (payload.Length < 2) break;
                byte fuHeader = payload[1];
                bool start = (fuHeader & 0x80) != 0;
                bool end = (fuHeader & 0x40) != 0;
                byte nalHeader = (byte)((payload[0] & 0xE0) | (fuHeader & 0x1F));
                if (start)
                {
                    _fuNal = new byte[payload.Length + 1];
                    _fuNal[0] = nalHeader;
                    payload.Slice(2).CopyTo(_fuNal.AsSpan(1));
                    _fuStarted = true;
                }
                else if (_fuStarted && _fuNal is not null)
                {
                    int old = _fuNal.Length;
                    var grown = new byte[old + payload.Length - 2];
                    Buffer.BlockCopy(_fuNal, 0, grown, 0, old);
                    payload.Slice(2).CopyTo(grown.AsSpan(old));
                    _fuNal = grown;
                }
                if (end && _fuStarted && _fuNal is not null)
                {
                    StoreNal(_fuNal);
                    _fuNal = null;
                    _fuStarted = false;
                }
                break;
            }
            case 7:
            case 8:
            case 1:
            case 5:
            {
                StoreNal(payload);
                break;
            }
            default:
                break; // SEI(6) 等丢弃
        }

        if (!marker) return null;
        if (_nals.Count == 0) return null;
        return BuildAccessUnit();
    }

    private void StoreNal(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 1) return;
        int type = nal[0] & 0x1F;
        var copy = nal.ToArray();
        if (type == 7) _sps = copy;
        else if (type == 8) _pps = copy;
        _nals.Add(copy);
    }

    private byte[] BuildAccessUnit()
    {
        bool hasIdr = _nals.Any(n => (n[0] & 0x1F) == 5);
        bool hasSps = _nals.Any(n => (n[0] & 0x1F) == 7);
        var list = new List<byte[]>();
        if (hasIdr && !hasSps)
        {
            if (_sps is not null) list.Add(_sps);
            if (_pps is not null) list.Add(_pps);
        }
        list.AddRange(_nals);

        int total = list.Sum(n => n.Length + 4);
        var outBuf = new byte[total];
        int w = 0;
        foreach (var n in list)
        {
            outBuf[w++] = 0; outBuf[w++] = 0; outBuf[w++] = 0; outBuf[w++] = 1;
            Buffer.BlockCopy(n, 0, outBuf, w, n.Length);
            w += n.Length;
        }
        _nals.Clear();
        return outBuf;
    }

    public void Reset()
    {
        _nals.Clear();
        _fuNal = null;
        _fuStarted = false;
        _lastSeq = null;
    }
}

/// <summary>连续延迟统计（环形窗口）。</summary>
public sealed class LatencyWindow
{
    private readonly double[] _samples;
    private int _index;
    private int _count;

    public LatencyWindow(int capacity = 300) => _samples = new double[capacity];

    public void Add(double ms)
    {
        _samples[_index] = ms;
        _index = (_index + 1) % _samples.Length;
        if (_count < _samples.Length) _count++;
    }

    public double Median
    {
        get
        {
            if (_count == 0) return 0;
            var copy = _samples.Take(_count).OrderBy(x => x).ToArray();
            return copy[copy.Length / 2];
        }
    }

    public double P95
    {
        get
        {
            if (_count == 0) return 0;
            var copy = _samples.Take(_count).OrderBy(x => x).ToArray();
            return copy[Math.Min(copy.Length - 1, (int)Math.Floor(copy.Length * 0.95))];
        }
    }

    public int Count => _count;
}

public static class Clock
{
    private static readonly long StartTicks = Stopwatch.GetTimestamp();
    public static long MsNow => (long)((Stopwatch.GetTimestamp() - StartTicks) * 1000.0 / Stopwatch.Frequency);
    public static double PreciseMs => (Stopwatch.GetTimestamp() - StartTicks) * 1000.0 / Stopwatch.Frequency;
}
