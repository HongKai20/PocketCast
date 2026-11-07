#pragma once
// 与电脑端 FrameBufferWriter.cs 逐字节一致。改一处必须同步另一处。
//
// header 128 字节
//   0  u32 magic 'PCAM'
//   4  u32 version 1
//   8  u32 width
//   12 u32 height
//   16 u32 stride
//   20 u32 format 'YUY2'
//   24 u32 fpsNum
//   28 u32 fpsDen
//   32 u32 activeSlot
//   36 u32 state (0=黑帧 1=实时)
//   40 u32 seq[4]
//   56 u64 frameCount
//   64 u64 timestampMs
//   72 保留到 128
// 128 起 4 个 slot，每个 1280x720 YUY2 = 1843200 字节

#include <windows.h>

#define POCKETCAM_MAGIC   0x5043414Du   // 'PCAM'
#define POCKETCAM_VERSION 1u
#define POCKETCAM_MAX_W   1280u
#define POCKETCAM_MAX_H   720u
#define POCKETCAM_SLOTS   4u
#define POCKETCAM_HEADER  128u
#define POCKETCAM_SLOT_SZ (POCKETCAM_MAX_W * POCKETCAM_MAX_H * 2u)
#define POCKETCAM_TOTAL   (POCKETCAM_HEADER + POCKETCAM_SLOTS * POCKETCAM_SLOT_SZ)
#define POCKETCAM_FOURCC_YUY2 0x59555932u

struct PocketCamHeader
{
    UINT32 magic;
    UINT32 version;
    UINT32 width;
    UINT32 height;
    UINT32 stride;
    UINT32 format;
    UINT32 fpsNum;
    UINT32 fpsDen;
    volatile UINT32 activeSlot;
    volatile UINT32 state;
    volatile UINT32 seq[POCKETCAM_SLOTS];
    UINT64 frameCount;
    UINT64 timestampMs;
    UINT8  reserved[POCKETCAM_HEADER - 72];
};

// 共享内存名与 C# FrameBufferWriter.MapName 一致（Local\ 前缀 = 当前登录会话）
#define POCKETCAM_MAP_NAME L"Local\\PocketCast.FrameBuffer"
