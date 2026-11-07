using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PocketCast.Core.Video;

/// <summary>
/// Media Foundation H.264 解码器（系统自带，无额外依赖）。
/// 输入 Annex-B 访问单元，输出 NV12/YUY2，再统一转成 YUY2 供虚拟摄像头共享内存使用。
/// </summary>
public sealed class MfH264Decoder : IDisposable
{
    private static bool _mfStarted;
    private static readonly object MfLock = new();

    private IMFTransform? _transform;
    private IMFMediaType? _outputType;
    private int _width, _height;
    private int _outStride;
    private Guid _outSubtype;
    private int _bufferSize;
    private long _inputTime;
    private bool _disposed;
    private bool _firstFrameLogged;

    public string DecoderName { get; private set; } = "未初始化";
    private byte[]? _seqHeader;
    public int Width => _width;
    public int Height => _height;
    public long DecodedFrames { get; private set; }
    public long FailedFrames { get; private set; }
    public string? LastError { get; private set; }
    public double LastDecodeMs { get; private set; }

    /// <summary>输出 YUY2 帧（stride 可能大于 width*2）。</summary>
    public event Action<byte[], int, int, int>? FrameDecoded;

    public bool Initialize()
    {
        try
        {
            EnsureMfStarted();
            var input = new MftRegisterTypeInfo
            {
                MajorType = MFMediaType_Video,
                Subtype = MFVideoFormat_H264
            };
            int hr = MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER,
                MFT_ENUM_FLAG_SORTANDFILTER, ref input, IntPtr.Zero,
                out IntPtr activates, out uint count);
            if (hr != 0) { LastError = $"MFTEnumEx 失败 0x{hr:X8}"; return false; }
            if (count == 0)
            {
                LastError = "系统里找不到 H.264 解码器（Windows N/KN 版需要安装 Media Feature Pack）";
                return false;
            }

            var attempts = new System.Text.StringBuilder();
            for (uint i = 0; i < count; i++)
            {
                IntPtr pAct = Marshal.ReadIntPtr(activates, (int)i * IntPtr.Size);
                var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pAct);
                try
                {
                    var iid = IID_IMFTransform;
                    if (activate.ActivateObject(ref iid, out IMFTransform t) == 0 && t != null)
                    {
                        UnlockAsync(t);
                        if (TryConfigure(t))
                        {
                            _transform = t;
                            DecoderName = ReadFriendlyName(activate) ?? "系统 H.264 解码器 (Media Foundation)";
                            return true;
                        }
                        Marshal.ReleaseComObject(t);
                    }
                }
                catch (Exception ex) { attempts.Append(ex.Message).Append(" / "); }
                finally { Marshal.ReleaseComObject(activate); }
            }
            LastError = $"枚举到 {count} 个候选解码器，但没有一个能按 H.264 输入配置" +
                        (attempts.Length > 0 ? "（" + attempts + "）" : "");
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    private bool TryConfigure(IMFTransform transform)
    {
        var inType = CreateMediaType(MFMediaType_Video, MFVideoFormat_H264);
        if (_seqHeader is { Length: > 0 })
        {
            // 系统解码器优先从 MF_MT_MPEG_SEQUENCE_HEADER 读 SPS/PPS；没有则依赖码流内联参数集
            var shKey = MF_MT_MPEG_SEQUENCE_HEADER;
            inType.SetBlob(ref shKey, _seqHeader, (uint)_seqHeader.Length);
        }
        if (transform.SetInputType(0, inType, 0) != 0) return false;

        foreach (var sub in new[] { MFVideoFormat_NV12, MFVideoFormat_YUY2, MFVideoFormat_RGB32 })
        {
            for (uint i = 0; ; i++)
            {
                if (transform.GetOutputAvailableType(0, i, out IMFMediaType mt) != 0) break;
                var key = MF_MT_SUBTYPE;
                var got = Guid.Empty;
                mt.GetGUID(ref key, out got);
                if (got == sub && transform.SetOutputType(0, mt, 0) == 0)
                {
                    _outputType = mt;
                    _outSubtype = sub;
                    break;
                }
                Marshal.ReleaseComObject(mt);
            }
            if (_outputType != null) break;
        }
        if (_outputType is null) return false;

        var sizeKey = MF_MT_FRAME_SIZE;
        _outputType.GetUINT64(ref sizeKey, out ulong packedSize);
        _width = (int)(packedSize & 0xFFFFFFFF);
        _height = (int)(packedSize >> 32);
        if (_width <= 0 || _height <= 0) { _width = 1280; _height = 720; }
        var strideKey = MF_MT_DEFAULT_STRIDE;
        _outputType.GetUINT32(ref strideKey, out uint defStride);
        _outStride = (int)defStride;

        MftOutputStreamInfo info = default;
        if (transform.GetOutputStreamInfo(0u, ref info) != 0) return false;
        _bufferSize = (int)Math.Max(info.cbSize, _width * _height * 4);
        _providesSamples = (info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;

        transform.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
        transform.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
        return true;
    }

    private bool _providesSamples;

    private static IMFMediaType CreateMediaType(Guid major, Guid sub)
    {
        MFCreateMediaType(out IMFMediaType mt).ThrowIfFailed();
        mt.SetGUID(MF_MT_MAJOR_TYPE, major);
        mt.SetGUID(MF_MT_SUBTYPE, sub);
        return mt;
    }

    /// <summary>送入一个 Annex-B 访问单元。</summary>
    public void Decode(byte[] annexB)
    {
        if (_disposed || annexB.Length == 0) return;
        if (_transform is null && !EnsureReady(annexB))
        {
            FailedFrames++;
            return;
        }
        var sw = Stopwatch.StartNew();
        try
        {
            MFCreateSample(out IMFSample sample).ThrowIfFailed();
            MFCreateMemoryBuffer(annexB.Length, out IMFMediaBuffer buffer).ThrowIfFailed();
            buffer.Lock(out IntPtr ptr, out _, out _);
            Marshal.Copy(annexB, 0, ptr, annexB.Length);
            buffer.Unlock();
            buffer.SetCurrentLength((uint)annexB.Length);
            sample.AddBuffer(buffer);
            _inputTime += 333333; // 30fps，单位 100ns
            sample.SetSampleTime(_inputTime);

            int hr = _transform.ProcessInput(0u, sample, 0u);
            Marshal.ReleaseComObject(buffer);
            Marshal.ReleaseComObject(sample);
            if (hr != 0 && hr != unchecked((int)0x80070005)) // E_ACCESSDENIED 表示需要更多数据
                FailedFrames++;

            DrainOutput();
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

    private void DrainOutput()
    {
        for (int guard = 0; guard < 4; guard++)
        {
            IMFSample? outSample = null;
            IntPtr samplePtr = IntPtr.Zero;
            if (!_providesSamples)
            {
                MFCreateSample(out outSample).ThrowIfFailed();
                MFCreateMemoryBuffer(_bufferSize, out IMFMediaBuffer buf).ThrowIfFailed();
                outSample.AddBuffer(buf);
                Marshal.ReleaseComObject(buf);
                samplePtr = Marshal.GetIUnknownForObject(outSample);
            }

            var data = new MftOutputDataBuffer { StreamId = 0, Sample = samplePtr };
            int hr = _transform!.ProcessOutput(0u, 1u, ref data, out _);

            if (hr == MFT_OUTPUT_STREAM_NEED_MORE_INPUT || hr == 0x80070005)
            {
                if (samplePtr != IntPtr.Zero) Marshal.Release(samplePtr);
                if (outSample != null) Marshal.ReleaseComObject(outSample);
                return;
            }
            if (hr == MFT_OUTPUT_STREAM_HAS_GAP)
            {
                if (samplePtr != IntPtr.Zero) Marshal.Release(samplePtr);
                if (outSample != null) Marshal.ReleaseComObject(outSample);
                continue;
            }
            if (hr != 0)
            {
                if (samplePtr != IntPtr.Zero) Marshal.Release(samplePtr);
                if (outSample != null) Marshal.ReleaseComObject(outSample);
                FailedFrames++;
                return;
            }

            IntPtr producedPtr = data.Sample;
            var produced = (IMFSample)Marshal.GetObjectForIUnknown(producedPtr);
            try
            {
                produced.ConvertToContiguousBuffer(out IMFMediaBuffer cb).ThrowIfFailed();
                cb.Lock(out IntPtr ptr, out _, out uint len);
                try
                {
                    EmitFrame(ptr, (int)len);
                }
                finally
                {
                    cb.Unlock();
                    Marshal.ReleaseComObject(cb);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(produced);
                if (samplePtr != IntPtr.Zero) Marshal.Release(samplePtr);
                if (outSample != null) Marshal.ReleaseComObject(outSample);
            }
        }
    }

    private void EmitFrame(IntPtr src, int length)
    {
        int w = _width, h = _height;
        int yuy2Stride = w * 2;
        var dst = new byte[yuy2Stride * h];

        if (_outSubtype == MFVideoFormat_YUY2)
        {
            int stride = _outStride > 0 ? _outStride : length / h;
            for (int y = 0; y < h; y++)
            {
                int off = y * stride;
                if (off + yuy2Stride > length) break;
                Marshal.Copy(IntPtr.Add(src, off), dst, y * yuy2Stride, yuy2Stride);
            }
        }
        else if (_outSubtype == MFVideoFormat_NV12)
        {
            int stride = _outStride > 0 ? _outStride : length / (h * 3 / 2);
            int uvOffset = stride * h;
            for (int y = 0; y < h; y++)
            {
                int yRow = y * stride;
                int uvRow = uvOffset + (y / 2) * stride;
                int dRow = y * yuy2Stride;
                for (int x = 0; x < w; x += 2)
                {
                    int dx = dRow + x * 2;
                    if (dx + 3 >= dst.Length) break;
                    byte y0 = Marshal.ReadByte(src, yRow + x);
                    byte y1 = Marshal.ReadByte(src, yRow + Math.Min(x + 1, w - 1));
                    int uvIdx = uvRow + ((x / 2) * 2);
                    byte u = Marshal.ReadByte(src, uvIdx);
                    byte v = Marshal.ReadByte(src, uvIdx + 1);
                    dst[dx] = y0; dst[dx + 1] = u; dst[dx + 2] = y1; dst[dx + 3] = v;
                }
            }
        }
        else // RGB32
        {
            int stride = _outStride > 0 ? Math.Abs(_outStride) : w * 4;
            for (int y = 0; y < h; y++)
            {
                int sRow = y * stride;
                int dRow = y * yuy2Stride;
                for (int x = 0; x < w; x += 2)
                {
                    int dx = dRow + x * 2;
                    if (dx + 3 >= dst.Length || sRow + (x + 1) * 4 + 2 >= length) break;
                    byte b0 = Marshal.ReadByte(src, sRow + x * 4);
                    byte g0 = Marshal.ReadByte(src, sRow + x * 4 + 1);
                    byte r0 = Marshal.ReadByte(src, sRow + x * 4 + 2);
                    byte b1 = Marshal.ReadByte(src, sRow + (x + 1) * 4);
                    byte g1 = Marshal.ReadByte(src, sRow + (x + 1) * 4 + 1);
                    byte r1 = Marshal.ReadByte(src, sRow + (x + 1) * 4 + 2);
                    byte yn0 = (byte)Math.Clamp((0.299 * r0 + 0.587 * g0 + 0.114 * b0), 0, 255);
                    byte yn1 = (byte)Math.Clamp((0.299 * r1 + 0.587 * g1 + 0.114 * b1), 0, 255);
                    byte u = (byte)Math.Clamp((-0.169 * r0 - 0.331 * g0 + 0.5 * b0) + 128, 0, 255);
                    byte v = (byte)Math.Clamp((0.5 * r0 - 0.419 * g0 - 0.081 * b0) + 128, 0, 255);
                    dst[dx] = yn0; dst[dx + 1] = u; dst[dx + 2] = yn1; dst[dx + 3] = v;
                }
            }
        }

        DecodedFrames++;
        if (!_firstFrameLogged)
        {
            _firstFrameLogged = true;
            Console.WriteLine($"[MF] 首帧解码成功 {w}x{h} {_outSubtype} -> YUY2");
        }
        FrameDecoded?.Invoke(dst, w, h, yuy2Stride);
    }

    private static void EnsureMfStarted()
    {
        lock (MfLock)
        {
            if (_mfStarted) return;
            CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
            MFStartup(0x00020070, 0).ThrowIfFailed();
            _mfStarted = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _transform?.ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero);
            _transform?.ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero);
        }
        catch { }
        if (_transform != null) Marshal.ReleaseComObject(_transform);
        if (_outputType != null) Marshal.ReleaseComObject(_outputType);
        _transform = null;
    }

    /// <summary>首次解码前按实际码流里的 SPS/PPS 初始化（参数集由手机侧码流携带）。</summary>
    public bool EnsureReady(byte[] annexB)
    {
        if (_transform != null) return true;
        _seqHeader ??= ExtractParameterSets(annexB);
        return Initialize();
    }

    private static void UnlockAsync(IMFTransform transform)
    {
        try
        {
            if (transform.GetAttributes(out IMFAttributes? attrs) == 0 && attrs is not null)
            {
                var key = MF_TRANSFORM_ASYNC_UNLOCK;
                attrs.SetUINT32(ref key, 1u);   // 解锁后异步 MFT 也能同步调用 ProcessInput/ProcessOutput
                Marshal.ReleaseComObject(attrs);
            }
        }
        catch { }
    }

    private static string? ReadFriendlyName(IMFActivate activate)
    {
        try
        {
            var key = MF_ATTRIBUTE_FRIENDLY_NAME;
            if (activate.GetStringLength(ref key, out uint len) != 0 || len == 0) return null;
            if (activate.GetString(ref key, out string name, len + 1) != 0) return null;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch { return null; }
    }

    /// <summary>从 Annex-B 码流里挑出 SPS(7)/PPS(8)，拼成解码器需要的序列头。</summary>
    private static byte[]? ExtractParameterSets(byte[] data)
    {
        var list = new List<byte>();
        int i = 0;
        while (i + 4 <= data.Length)
        {
            int sc = -1, scLen = 0;
            for (int j = i; j + 3 < data.Length; j++)
            {
                if (data[j] != 0 || data[j + 1] != 0) continue;
                if (data[j + 2] == 1) { sc = j; scLen = 3; break; }
                if (data[j + 2] == 0 && data[j + 3] == 1) { sc = j; scLen = 4; break; }
            }
            if (sc < 0) break;

            int nalStart = sc + scLen;
            int nalEnd = data.Length;
            for (int j = nalStart; j + 3 < data.Length; j++)
            {
                if (data[j] != 0 || data[j + 1] != 0) continue;
                if (data[j + 2] == 1 || (data[j + 2] == 0 && data[j + 3] == 1)) { nalEnd = j; break; }
            }
            if (nalStart < nalEnd)
            {
                int type = data[nalStart] & 0x1F;
                if (type == 7 || type == 8)
                {
                    list.Add(0); list.Add(0); list.Add(0); list.Add(1);
                    for (int k = nalStart; k < nalEnd; k++) list.Add(data[k]);
                }
            }
            i = nalEnd;
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    // ---------------- P/Invoke ----------------
    private const int MFT_OUTPUT_STREAM_NEED_MORE_INPUT = unchecked((int)0x800D0001);
    private const int MFT_OUTPUT_STREAM_HAS_GAP = unchecked((int)0x800D0002);
    private const int COINIT_MULTITHREADED = 0;
    private const uint MFT_ENUM_FLAG_SYNCMFT = 0x1;
    private const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x40;
    private const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    private const uint MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    private const uint MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
    private const uint MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000004;
    private const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;

    private static readonly Guid MFT_CATEGORY_VIDEO_DECODER = new("F79EAC7D-E545-4387-BDEE-D647D7BDE42A");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_YUY2 = new("32595559-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid IID_IMFTransform = new("BFBECA5A-5A35-4507-A1F4-6B5B3184A3E5");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new("5fb7ee00-8f9c-45e0-8bd5-ade9a4d5e6e2");
    private static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("f81a699a-649a-497d-8c73-29f8edaaed55");
    private static readonly Guid MF_ATTRIBUTE_FRIENDLY_NAME = new("314ffbae-5b41-4c95-9c24-4ed586bece4e");

    [StructLayout(LayoutKind.Sequential)]
    private struct MftRegisterTypeInfo { public Guid MajorType; public Guid Subtype; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputDataBuffer
    {
        public uint StreamId;
        public IntPtr Sample;
        public uint Status;
        public IntPtr Events;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputStreamInfo
    {
        public uint dwFlags;
        public uint cbSize;
        public uint cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftInputStreamInfo
    {
        public uint dwFlags;
        public uint cbSize;
        public uint cbAlignment;
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int dwFlags);
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IMFMediaType ppMFType);
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateSample(out IMFSample ppIMFSample);
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMemoryBuffer(int cbMaxLength, out IMFMediaBuffer ppBuffer);
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFTEnumEx(Guid guidCategory, uint flags, ref MftRegisterTypeInfo pInputType,
        IntPtr pOutputType, out IntPtr pppMFTActivate, out uint pnumMFTActivate);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid guidKey, out IntPtr pValue, out uint pcbData);
        [PreserveSig] int GetItemType(ref Guid guidKey, out uint pType);
        [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value);
        [PreserveSig] int Compare(IMFAttributes pAttrs, uint matchType);
        [PreserveSig] int GetUINT32(ref Guid guidKey, out uint value);
        [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong value);
        [PreserveSig] int GetDouble(ref Guid guidKey, out double value);
        [PreserveSig] int GetGUID(ref Guid guidKey, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid guidKey, out uint length);
        [PreserveSig] int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string value, uint length);
        [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize(ref Guid guidKey, out uint size);
        [PreserveSig] int GetBlob(ref Guid guidKey, [Out] byte[] value, uint size, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr value, out uint size);
        [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value, uint size);
        [PreserveSig] int DeleteItem(ref Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid guidKey, uint value);
        [PreserveSig] int SetUINT64(ref Guid guidKey, ulong value);
        [PreserveSig] int SetDouble(ref Guid guidKey, double value);
        [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid value);
        [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid guidKey, [MarshalAs(UnmanagedType.LPArray)] byte[] value, uint size);
        [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object unknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, ref Guid guidKey, out IntPtr value, out uint size);
        [PreserveSig] int CopyAllItems(IMFAttributes dest);
    }

    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType : IMFAttributes
    {
        [PreserveSig] int GetMajorType(out Guid guidMajorType);
        [PreserveSig] int IsCompressedFormat(out int compressed);
        [PreserveSig] int IsEqual(IMFMediaType other, out uint flags);
        [PreserveSig] int GetRepresentation(Guid representation, out IntPtr ppv);
        [PreserveSig] int FreeRepresentation(Guid representation, IntPtr pv);
    }

    [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample : IMFAttributes
    {
        [PreserveSig] int GetSampleFlags(out uint flags);
        [PreserveSig] int SetSampleFlags(uint flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out uint count);
        [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(uint index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out uint length);
        [PreserveSig] int CopyToBuffer(out IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42B8-BC89-6DD7B9CB5C0B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint length);
        [PreserveSig] int SetCurrentLength(uint length);
        [PreserveSig] int GetMaxLength(out uint length);
    }

    [ComImport, Guid("7FEE9E9A-4A89-47A6-897A-F752CBA95BBB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFActivate : IMFAttributes
    {
        [PreserveSig] int ActivateObject(ref Guid riid, out IMFTransform ppv);
        [PreserveSig] int DetachObject();
        [PreserveSig] int ShutdownObject();
    }

    [ComImport, Guid("BFBECA5A-5A35-4507-A1F4-6B5B3184A3E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFTransform
    {
        [PreserveSig] int GetStreamLimits(out uint inputMin, out uint inputMax, out uint outputMin, out uint outputMax);
        [PreserveSig] int GetStreamCount(out uint inputCount, out uint outputCount);
        [PreserveSig] int GetStreamIDs(uint inputArraySize, [Out] uint[] inputIds, uint outputArraySize, [Out] uint[] outputIds);
        [PreserveSig] int GetInputStreamInfo(uint inputStreamId, ref MftInputStreamInfo info);
        [PreserveSig] int GetOutputStreamInfo(uint outputStreamId, ref MftOutputStreamInfo info);
        [PreserveSig] int GetAttributes(out IMFAttributes attributes);
        [PreserveSig] int GetInputAvailableType(uint inputStreamId, uint typeIndex, out IMFMediaType type);
        [PreserveSig] int GetOutputAvailableType(uint outputStreamId, uint typeIndex, out IMFMediaType type);
        [PreserveSig] int SetInputType(uint inputStreamId, IMFMediaType type, uint flags);
        [PreserveSig] int SetOutputType(uint outputStreamId, IMFMediaType type, uint flags);
        [PreserveSig] int GetInputCurrentType(uint inputStreamId, out IMFMediaType type);
        [PreserveSig] int GetOutputCurrentType(uint outputStreamId, out IMFMediaType type);
        [PreserveSig] int GetInputStatus(uint inputStreamId, out uint flags);
        [PreserveSig] int GetOutputStatus(out uint flags);
        [PreserveSig] int SetOutputBounds(long lowerBound, long upperBound);
        [PreserveSig] int ProcessEvent(uint inputStreamId, IntPtr pEvent);
        [PreserveSig] int ProcessMessage(uint message, IntPtr param);
        [PreserveSig] int ProcessInput(uint inputStreamId, IMFSample sample, uint flags);
        [PreserveSig] int ProcessOutput(uint flags, uint outputBufferCount, ref MftOutputDataBuffer outputSamples, out uint status);
    }

}

internal static class HResultExtensions
{
    public static void ThrowIfFailed(this int hr)
    {
        if (hr < 0) throw new COMException($"HRESULT 0x{hr:X8}", hr);
    }
}
