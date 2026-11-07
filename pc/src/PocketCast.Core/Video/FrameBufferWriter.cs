using System.IO.MemoryMappedFiles;

namespace PocketCast.Core.Video;

/// <summary>
/// 与 DirectShow 虚拟摄像头（pocketcam_vcam.ax）共享的帧缓冲。
/// 布局必须与 vcam/src/SharedFrame.h 保持逐字节一致。
///
/// header(128B)
///   0  u32 magic = 'PCAM'
///   4  u32 version = 1
///   8  u32 width
///   12 u32 height
///   16 u32 stride (bytes)
///   20 u32 formatFourCC ('YUY2')
///   24 u32 fpsNum
///   28 u32 fpsDen
///   32 u32 activeSlot
///   36 u32 state (0=空闲黑帧, 1=实时)
///   40 u32 seq[4]
///   56 u64 frameCount
///   64 u64 timestampMs
///   72 保留至 128
/// 128 起 4 个 slot，每个 1280x720 YUY2 = 1,843,200 字节
/// </summary>
public sealed class FrameBufferWriter : IDisposable
{
    public const uint Magic = 0x5043414D;
    public const uint Version = 1;
    public const int MaxWidth = 1280;
    public const int MaxHeight = 720;
    public const int SlotCount = 4;
    public const int HeaderSize = 128;
    public const int SlotSize = MaxWidth * MaxHeight * 2;
    public const int TotalSize = HeaderSize + SlotCount * SlotSize;
    public static readonly uint FormatYuy2 = 0x59555932; // 'YUY2'

    public static string MapName { get; } = @"Local\PocketCast.FrameBuffer";

    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly MemoryMappedViewStream _stream;
    private readonly byte[] _blackFrame = new byte[SlotSize];
    private int _slot;
    private long _frameCount;
    private volatile bool _disposed;

    public FrameBufferWriter()
    {
        _map = MemoryMappedFile.CreateOrOpen(MapName, TotalSize, MemoryMappedFileAccess.ReadWrite);
        _view = _map.CreateViewAccessor(0, TotalSize, MemoryMappedFileAccess.ReadWrite);
        _stream = _map.CreateViewStream(0, TotalSize, MemoryMappedFileAccess.ReadWrite);

        // 黑帧：Y=16, U=V=128
        for (int i = 0; i < _blackFrame.Length; i += 2)
        {
            _blackFrame[i] = 0x10;
            _blackFrame[i + 1] = 0x80;
        }
        WriteHeader(MaxWidth, MaxHeight, MaxWidth * 2, 0);
        for (int i = 0; i < SlotCount; i++)
            Publish(_blackFrame, MaxWidth, MaxHeight, MaxWidth * 2, live: false);
    }

    private void WriteHeader(int width, int height, int stride, int state)
    {
        _view.Write(0L, Magic);
        _view.Write(4L, Version);
        _view.Write(8L, (uint)width);
        _view.Write(12L, (uint)height);
        _view.Write(16L, (uint)stride);
        _view.Write(20L, FormatYuy2);
        _view.Write(24L, 30u);
        _view.Write(28L, 1u);
        _view.Write(32L, (uint)0);
        _view.Write(36L, (uint)state);
        for (int i = 0; i < SlotCount; i++) _view.Write(40L + i * 4, (uint)0);
        _view.Write(56L, 0UL);
        _view.Write(64L, 0UL);
    }

    public void PublishIdle() => Publish(_blackFrame, MaxWidth, MaxHeight, MaxWidth * 2, live: false);

    /// <summary>写入一帧 YUY2（stride 允许大于 width*2）。</summary>
    public void Publish(byte[] yuy2, int width, int height, int stride, bool live = true)
    {
        if (_disposed) return;
        width = Math.Min(width, MaxWidth);
        height = Math.Min(height, MaxHeight);
        if (stride <= 0 || stride > MaxWidth * 2) stride = MaxWidth * 2;

        int slot = _slot;
        _slot = (_slot + 1) % SlotCount;
        long slotOffset = HeaderSize + (long)slot * SlotSize;

        // seq=0 表示正在写，读者跳过该 slot
        _view.Write(40L + slot * 4, (uint)0);

        int rows = Math.Min(height, SlotSize / stride);
        int copyBytes = Math.Min(stride, width * 2);
        for (int y = 0; y < rows; y++)
        {
            int src = y * stride;
            if (src + copyBytes > yuy2.Length) break;
            _stream.Position = slotOffset + (long)y * stride;
            _stream.Write(yuy2, src, copyBytes);
        }

        _frameCount++;
        _view.Write(56L, (ulong)_frameCount);
        _view.Write(64L, (ulong)Environment.TickCount64);
        _view.Write(8L, (uint)width);
        _view.Write(12L, (uint)height);
        _view.Write(16L, (uint)stride);
        _view.Write(36L, (uint)(live ? 1 : 0));

        uint seq = (uint)(_frameCount & 0x7FFFFFFF);
        Thread.MemoryBarrier();
        _view.Write(40L + slot * 4, seq);
        Thread.MemoryBarrier();
        _view.Write(32L, (uint)slot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
        _view.Dispose();
        _map.Dispose();
    }
}
