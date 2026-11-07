using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PocketCast.Core.Video;

/// <summary>
/// 内置软件 H.264 解码器（Cisco OpenH264，随程序分发 openh264-*.dll）。
/// 用于系统里没有 Media Foundation H.264 解码器的情况（Windows N/KN、
/// 精简版 LTSC/Server 等 SKU 上没有 mfh264dec）。
/// 输入 Annex-B 访问单元，输出 I420 → 转 YUY2 后交给虚拟摄像头。
/// </summary>
public sealed class OpenH264Decoder : IDisposable
{
    private static readonly object LoadLock = new();
    private static IntPtr _module;
    private static bool _loadAttempted;
    private static IntPtr _createFn;
    private static IntPtr _destroyFn;

    private IntPtr _decoder;
    private IntPtr _vtbl;
    private IntPtr _decodeFrameNoDelay;
    private IntPtr _initialize;
    private IntPtr _uninitialize;
    private bool _disposed;

    private byte[] _yRow = Array.Empty<byte>();
    private byte[] _uRow = Array.Empty<byte>();
    private byte[] _vRow = Array.Empty<byte>();

    public string DecoderName { get; private set; } = "OpenH264（软件解码）";
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long DecodedFrames { get; private set; }
    public long FailedFrames { get; private set; }
    public string? LastError { get; private set; }
    public double LastDecodeMs { get; private set; }

    /// <summary>输出 YUY2 帧（stride = width * 2）。</summary>
    public event Action<byte[], int, int, int>? FrameDecoded;

    public bool Initialize()
    {
        try
        {
            if (_decoder != IntPtr.Zero) return true;
            if (!EnsureLoaded())
            {
                LastError = LastErrorStatic ?? "缺少 openh264-2.2.0-win64.dll（内置软件解码器不可用）";
                return false;
            }

            if (Marshal.GetDelegateForFunctionPointer<CreateDecoderFn>(_createFn)(out IntPtr decoder) != 0 || decoder == IntPtr.Zero)
            {
                LastError = "WelsCreateDecoder 失败";
                return false;
            }

            _decoder = decoder;
            _vtbl = Marshal.ReadIntPtr(decoder);
            _initialize = Marshal.ReadIntPtr(_vtbl, 0 * IntPtr.Size);           // ISVCDecoder::Initialize
            _uninitialize = Marshal.ReadIntPtr(_vtbl, 1 * IntPtr.Size);         // Uninitialize
            _decodeFrameNoDelay = Marshal.ReadIntPtr(_vtbl, 3 * IntPtr.Size);   // DecodeFrameNoDelay

            var param = default(SDecodingParam);
            param.eEcActiveIdc = 2;              // ERROR_CON_SLICE_COPY，丢包时做补偿
            param.uiTargetDqLayer = 0xFF;        // 解所有时域/空域层
            param.sVideoPropertySize = 8;        // sizeof(SVideoProperty)
            param.eVideoBsType = 0;              // VIDEO_BITSTREAM_AVC

            int hr = Marshal.GetDelegateForFunctionPointer<InitializeFn>(_initialize)(_decoder, ref param);
            if (hr != 0)
            {
                LastError = $"Initialize 返回 {hr}";
                Dispose();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Dispose();
            return false;
        }
    }

    /// <summary>送入一个 Annex-B 访问单元。</summary>
    public void Decode(byte[] annexB)
    {
        if (_disposed || annexB.Length == 0) return;
        if (_decoder == IntPtr.Zero && !Initialize()) { FailedFrames++; return; }

        var sw = Stopwatch.StartNew();
        try
        {
            var info = default(SBufferInfo);
            info.iWidth = Width;
            info.iHeight = Height;
            int state;
            unsafe
            {
                fixed (byte* src = annexB)
                {
                    IntPtr* ppDst = stackalloc IntPtr[3];
                    ppDst[0] = IntPtr.Zero; ppDst[1] = IntPtr.Zero; ppDst[2] = IntPtr.Zero;
                    state = Marshal.GetDelegateForFunctionPointer<DecodeFrameNoDelayFn>(_decodeFrameNoDelay)(
                        _decoder, src, annexB.Length, ppDst, ref info);
                }
            }

            if (info.iBufferStatus == 1 && info.pDst0 != IntPtr.Zero)
            {
                Width = info.iWidth;
                Height = info.iHeight;
                EmitI420(info);
            }
            else if ((state & 0x36) != 0)   // dsRefLost | dsBitstreamError | dsNoParamSets | dsRefListError
            {
                FailedFrames++;
                LastError = DescribeState(state);
            }
        }
        catch (Exception ex)
        {
            FailedFrames++;
            LastError = ex.Message;
        }
        finally
        {
            LastDecodeMs = sw.Elapsed.TotalMilliseconds;
        }
    }

    private static string DescribeState(int state)
    {
        if ((state & 0x10) != 0) return "码流里没有 SPS/PPS（参数集）";
        if ((state & 0x04) != 0) return "码流错误";
        if ((state & 0x02) != 0) return "参考帧丢失";
        if ((state & 0x40) != 0) return "参考列表错误";
        return $"解码状态 0x{state:X2}";
    }

    /// <summary>I420 → YUY2（每两个像素共享一组 U/V）。</summary>
    private void EmitI420(SBufferInfo info)
    {
        int w = info.iWidth, h = info.iHeight;
        int yStride = info.iStride0 > 0 ? info.iStride0 : w;
        int cStride = info.iStride1 > 0 ? info.iStride1 : (w + 1) / 2;
        int yuy2Stride = w * 2;
        var dst = new byte[yuy2Stride * h];

        if (_yRow.Length < w) { _yRow = new byte[w]; _uRow = new byte[(w + 1) / 2]; _vRow = new byte[(w + 1) / 2]; }

        for (int y = 0; y < h; y++)
        {
            Marshal.Copy(IntPtr.Add(info.pDst0, y * yStride), _yRow, 0, w);
            int cy = y / 2;
            int cw = (w + 1) / 2;
            Marshal.Copy(IntPtr.Add(info.pDst1, cy * cStride), _uRow, 0, cw);
            Marshal.Copy(IntPtr.Add(info.pDst2, cy * cStride), _vRow, 0, cw);

            int dRow = y * yuy2Stride;
            for (int x = 0; x < w; x += 2)
            {
                int c = x / 2;
                int dx = dRow + x * 2;
                dst[dx] = _yRow[x];
                dst[dx + 1] = _uRow[c];
                dst[dx + 2] = x + 1 < w ? _yRow[x + 1] : _yRow[x];
                dst[dx + 3] = _vRow[c];
            }
        }

        DecodedFrames++;
        FrameDecoded?.Invoke(dst, w, h, yuy2Stride);
    }

    private static bool EnsureLoaded()
    {
        lock (LoadLock)
        {
            if (_loadAttempted) return _module != IntPtr.Zero;
            _loadAttempted = true;

            string[] candidates =
            {
                "openh264-2.2.0-win64.dll",
                "openh264-2.1.1-win64.dll",
                "openh264-2.1.1-win32.dll",
                "openh264.dll",
            };
            foreach (var name in candidates)
            {
                foreach (var dir in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "vcam") })
                {
                    var full = Path.Combine(dir, name);
                    if (!File.Exists(full)) continue;
                    try
                    {
                        _module = NativeLibrary.Load(full);
                        break;
                    }
                    catch { }
                }
                if (_module == IntPtr.Zero)
                {
                    try { _module = NativeLibrary.Load(name); } catch { }
                }
                if (_module != IntPtr.Zero) break;
            }

            // 兜底：目录里任意 openh264*.dll
            if (_module == IntPtr.Zero)
            {
                try
                {
                    foreach (var f in Directory.GetFiles(AppContext.BaseDirectory, "openh264*.dll"))
                    {
                        try { _module = NativeLibrary.Load(f); break; } catch { }
                    }
                }
                catch { }
            }

            if (_module == IntPtr.Zero) return false;

            try
            {
                _createFn = NativeLibrary.GetExport(_module, "WelsCreateDecoder");
                _destroyFn = NativeLibrary.GetExport(_module, "WelsDestroyDecoder");
            }
            catch (Exception ex)
            {
                LastErrorStatic = ex.Message;
                _module = IntPtr.Zero;
            }
            return _module != IntPtr.Zero;
        }
    }

    internal static string? LastErrorStatic { get; private set; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_decoder != IntPtr.Zero && _uninitialize != IntPtr.Zero)
            {
                Marshal.GetDelegateForFunctionPointer<UninitializeFn>(_uninitialize)(_decoder);
                if (_destroyFn != IntPtr.Zero)
                    Marshal.GetDelegateForFunctionPointer<DestroyDecoderFn>(_destroyFn)(_decoder);
            }
        }
        catch { }
        _decoder = IntPtr.Zero;
    }

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Explicit)]
    private struct SDecodingParam
    {
        [FieldOffset(0)] public IntPtr pFileNameRestructed;
        [FieldOffset(8)] public uint uiCpuLoad;
        [FieldOffset(12)] public byte uiTargetDqLayer;
        [FieldOffset(16)] public int eEcActiveIdc;
        [FieldOffset(20)] public byte bParseOnly;
        [FieldOffset(24)] public uint sVideoPropertySize;
        [FieldOffset(28)] public int eVideoBsType;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct SBufferInfo
    {
        [FieldOffset(0)] public int iBufferStatus;
        [FieldOffset(8)] public ulong uiInBsTimeStamp;
        [FieldOffset(16)] public ulong uiOutYuvTimeStamp;
        [FieldOffset(24)] public int iWidth;
        [FieldOffset(28)] public int iHeight;
        [FieldOffset(32)] public int iFormat;
        [FieldOffset(36)] public int iStride0;
        [FieldOffset(40)] public int iStride1;
        [FieldOffset(48)] public IntPtr pDst0;
        [FieldOffset(56)] public IntPtr pDst1;
        [FieldOffset(64)] public IntPtr pDst2;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDecoderFn(out IntPtr ppDecoder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DestroyDecoderFn(IntPtr decoder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitializeFn(IntPtr pThis, ref SDecodingParam param);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UninitializeFn(IntPtr pThis);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate int DecodeFrameNoDelayFn(IntPtr pThis, byte* pSrc, int srcLen, IntPtr* ppDst, ref SBufferInfo pDstInfo);
}
