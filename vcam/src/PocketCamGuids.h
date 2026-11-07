#pragma once
// PocketCast 虚拟摄像头 —— 用到的 GUID 与公共常量。
//
// 注意：mingw-w64 的 <uuids.h> 没有 include guard，且与 <strmif.h> 里的同名 GUID
// 重复定义，所以这里不包含 uuids.h，只用 INITGUID + strmif.h，缺的媒体类型 GUID 自行定义。
#include <windows.h>
#include <initguid.h>
#include <strmif.h>

// {A7B0C1D2-3E4F-4A5B-9C8D-1E2F3A4B5C6D}
DEFINE_GUID(CLSID_PocketCamFilter,
    0xA7B0C1D2, 0x3E4F, 0x4A5B, 0x9C, 0x8D, 0x1E, 0x2F, 0x3A, 0x4B, 0x5C, 0x6D);

// 以下三个在 <uuids.h> 里才有，这里补定义（值取自 DirectShow SDK 的 uuids.h）
DEFINE_GUID(MEDIATYPE_Video,   0x73646976, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
DEFINE_GUID(MEDIASUBTYPE_YUY2, 0x32595559, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
DEFINE_GUID(MEDIASUBTYPE_RGB24, 0xE436EB7D, 0x524F, 0x11CE, 0x9F, 0x53, 0x00, 0x20, 0xAF, 0x0B, 0xA7, 0x70);
DEFINE_GUID(MEDIASUBTYPE_RGB32, 0xE436EB7E, 0x524F, 0x11CE, 0x9F, 0x53, 0x00, 0x20, 0xAF, 0x0B, 0xA7, 0x70);
DEFINE_GUID(FORMAT_VideoInfo,  0x05589F80, 0xC356, 0x11CE, 0xBF, 0x01, 0x00, 0xAA, 0x00, 0x55, 0x59, 0x5A);

// 采集类别：ICaptureGraphBuilder2 靠 IKsPropertySet(AMPROPSETID_Pin) 判断哪个引脚是“采集引脚”。
DEFINE_GUID(PIN_CATEGORY_CAPTURE, 0xFB6C4281, 0x0353, 0x11D1, 0x90, 0x5F, 0x00, 0x00, 0xC0, 0xCC, 0x16, 0xBA);
DEFINE_GUID(PIN_CATEGORY_PREVIEW, 0xFB6C4282, 0x0353, 0x11D1, 0x90, 0x5F, 0x00, 0x00, 0xC0, 0xCC, 0x16, 0xBA);
DEFINE_GUID(AMPROPSETID_Pin, 0x9B00F101, 0x1567, 0x11D1, 0xB3, 0xF1, 0x00, 0xAA, 0x00, 0x37, 0x61, 0xC5);

#ifndef AMPROPERTY_PIN_CATEGORY
#define AMPROPERTY_PIN_CATEGORY 0u
#endif
#ifndef E_PROP_SET_UNSUPPORTED
#define E_PROP_SET_UNSUPPORTED ((HRESULT)0x80070492L)
#endif
#ifndef E_PROP_ID_UNSUPPORTED
#define E_PROP_ID_UNSUPPORTED ((HRESULT)0x80070490L)
#endif

#define POCKETCAM_FILTER_NAME   L"PocketCast Virtual Camera"
#define POCKETCAM_FILTER_DESC   L"PocketCast 虚拟摄像头（手机摄像头 → 电脑）"

// 支持的输出格式（宽 x 高）。后两档是给老采集软件留的：不少老工具默认就要求 320x240 / 352x288。
static const struct { int w, h; } g_Formats[] = {
    { 1280, 720 },
    {  960, 540 },
    {  640, 480 },
    {  352, 288 },
    {  320, 240 },
};
static const int g_FormatCount = 5;
static const int g_Fps = 30;
