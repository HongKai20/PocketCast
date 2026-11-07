// PocketCast 虚拟摄像头：最小化 DirectShow 源滤镜，直接从共享内存取手机画面。
// 不依赖 DirectShow Base Classes（strmbase），mingw-w64 / zig cc 都能编译。
//
// 共享内存布局见 SharedFrame.h（与电脑端 FrameBufferWriter.cs 逐字节一致）。
// 输出 YUY2，支持 1280x720 / 960x540 / 640x480 @30fps；手机分辨率不同时按最近邻缩放。
// 没有画面时输出纯黑帧（Y=16，U=V=128），软件里永远有画面、不会黑屏报错。

#include <windows.h>
#include <olectl.h>
#include <oleauto.h>
#include "PocketCamGuids.h"   // 内含 initguid.h + strmif.h，REFERENCE_TIME 等类型必须先于 amvideo.h
#include <amvideo.h>
#include <string.h>
#include <stdlib.h>
#include "SharedFrame.h"

HINSTANCE g_hInst = NULL;

// 释放 AM_MEDIA_TYPE（strmbase 的 mtype.h 里才有，这里自己实现）
static void FreeMediaType(AM_MEDIA_TYPE* pmt)
{
    if (!pmt) return;
    if (pmt->pbFormat) { CoTaskMemFree(pmt->pbFormat); pmt->pbFormat = NULL; }
    ZeroMemory(pmt, sizeof(*pmt));
}

#ifndef VFW_E_NOT_CONNECTED
#define VFW_E_NOT_CONNECTED ((HRESULT)0x80040209L)
#endif
#ifndef VFW_E_ALREADY_CONNECTED
#define VFW_E_ALREADY_CONNECTED ((HRESULT)0x80040204L)
#endif
#ifndef VFW_E_TYPE_NOT_ACCEPTED
#define VFW_E_TYPE_NOT_ACCEPTED ((HRESULT)0x80040201L)
#endif
#ifndef VFW_E_NOT_STOPPED
#define VFW_E_NOT_STOPPED ((HRESULT)0x80040208L)
#endif
#ifndef VFW_S_STATE_INTERMEDIATE
#define VFW_S_STATE_INTERMEDIATE ((HRESULT)0x00040237L)
#endif
#ifndef VFW_E_NOT_FOUND
#define VFW_E_NOT_FOUND ((HRESULT)0x80040216L)
#endif
#ifndef VFW_E_CANNOT_CONNECT
#define VFW_E_CANNOT_CONNECT ((HRESULT)0x8004022AL)
#endif
#ifndef KEY_WOW64_64KEY
#define KEY_WOW64_64KEY 0x0100
#endif

// ---------------------------------------------------------------- 基础工具

static LONGLONG QpcNow()
{
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return t.QuadPart;
}

static LONGLONG QpcFreq()
{
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    return f.QuadPart;
}

static void WaitUntilQpc(LONGLONG target, HANDLE hStop)
{
    for (;;)
    {
        LONGLONG now = QpcNow();
        if (now >= target) return;
        LONGLONG ms = (target - now) * 1000 / QpcFreq();
        if (ms > 15) ms = 15;          // 分片等待，便于及时响应停止
        if (ms < 1) ms = 1;
        if (WaitForSingleObject(hStop, (DWORD)ms) == WAIT_OBJECT_0) return;
    }
}

// ---------------------------------------------------------------- 输出格式表
//
// 同一分辨率提供三种像素格式：
//   RGB24 / RGB32 —— 老软件（ECap、AMCap）与旧版 Video Renderer 只认这两位，
//                   只给 YUY2 的话连接阶段就会失败，软件报“请检查装置连接状况”。
//   YUY2          —— 原生格式，省一次转换，OBS / 微信这类现代软件常用。
static const GUID* const g_Subtypes[] = {
    &MEDIASUBTYPE_RGB24,
    &MEDIASUBTYPE_RGB32,
    &MEDIASUBTYPE_YUY2,
};
static const int g_SubtypeCount = 3;

static int TypeCount() { return g_FormatCount * g_SubtypeCount; }

static void GetType(int i, int* pw, int* ph, const GUID** psub)
{
    if (i < 0 || i >= TypeCount()) i = 0;
    *pw = g_Formats[i / g_SubtypeCount].w;
    *ph = g_Formats[i / g_SubtypeCount].h;
    *psub = g_Subtypes[i % g_SubtypeCount];
}

static bool IsYuy2(const GUID* sub) { return IsEqualGUID(*sub, MEDIASUBTYPE_YUY2); }

static int SubtypeBits(const GUID* sub)
{
    if (IsYuy2(sub)) return 16;
    if (IsEqualGUID(*sub, MEDIASUBTYPE_RGB32)) return 32;
    return 24;
}

static int SubtypeBytes(const GUID* sub)
{
    if (IsYuy2(sub)) return 2;
    if (IsEqualGUID(*sub, MEDIASUBTYPE_RGB32)) return 4;
    return 3;
}

static DWORD SubtypeFourcc(const GUID* sub)
{
    return IsYuy2(sub) ? MAKEFOURCC('Y', 'U', 'Y', '2') : BI_RGB;
}

static void FillVideoInfo(VIDEOINFOHEADER* pvi, int w, int h, const GUID* sub)
{
    int bytes = SubtypeBytes(sub);
    ZeroMemory(pvi, sizeof(*pvi));
    pvi->AvgTimePerFrame = 10000000LL / g_Fps;
    // dwBitRate 必须保持 0：旧版 Video Renderer 会拿它和自带的类型逐个字段比对，
    // 非 0 就会被判为“类型不接受”，连接直接失败（ECap 那句“请检查装置连接状况”就是这么来的）。
    pvi->dwBitRate = 0;
    pvi->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    pvi->bmiHeader.biWidth = w;
    pvi->bmiHeader.biHeight = h;
    pvi->bmiHeader.biPlanes = 1;
    pvi->bmiHeader.biBitCount = (WORD)SubtypeBits(sub);
    pvi->bmiHeader.biCompression = SubtypeFourcc(sub);
    pvi->bmiHeader.biSizeImage = w * h * bytes;
    pvi->rcSource.right = w;
    pvi->rcSource.bottom = h;
    pvi->rcTarget.right = w;
    pvi->rcTarget.bottom = h;
}

static HRESULT MakeMediaType(int w, int h, const GUID* sub, AM_MEDIA_TYPE** ppmt)
{
    AM_MEDIA_TYPE* pmt = (AM_MEDIA_TYPE*)CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE));
    if (!pmt) return E_OUTOFMEMORY;
    ZeroMemory(pmt, sizeof(*pmt));
    pmt->majortype = MEDIATYPE_Video;
    pmt->subtype = *sub;
    pmt->bFixedSizeSamples = TRUE;
    pmt->bTemporalCompression = FALSE;
    pmt->lSampleSize = w * h * SubtypeBytes(sub);
    pmt->formattype = FORMAT_VideoInfo;
    pmt->cbFormat = sizeof(VIDEOINFOHEADER);
    pmt->pbFormat = (BYTE*)CoTaskMemAlloc(sizeof(VIDEOINFOHEADER));
    if (!pmt->pbFormat) { CoTaskMemFree(pmt); return E_OUTOFMEMORY; }
    FillVideoInfo((VIDEOINFOHEADER*)pmt->pbFormat, w, h, sub);
    *ppmt = pmt;
    return S_OK;
}

// 返回该媒体类型在格式表中的下标；不匹配返回 -1
static int MatchType(const AM_MEDIA_TYPE* pmt)
{
    if (!pmt || !pmt->pbFormat) return -1;
    if (pmt->cbFormat < sizeof(VIDEOINFOHEADER)) return -1;
    if (!IsEqualGUID(pmt->majortype, MEDIATYPE_Video)) return -1;
    if (!IsEqualGUID(pmt->formattype, FORMAT_VideoInfo)) return -1;
    const VIDEOINFOHEADER* pvi = (const VIDEOINFOHEADER*)pmt->pbFormat;
    for (int i = 0; i < TypeCount(); i++)
    {
        int w, h;
        const GUID* sub;
        GetType(i, &w, &h, &sub);
        if (!IsEqualGUID(pmt->subtype, *sub)) continue;
        if ((int)pvi->bmiHeader.biWidth == w && abs((int)pvi->bmiHeader.biHeight) == h)
            return i;
    }
    return -1;
}

class CPocketCam;
class CPocketCamPin;

// ---------------------------------------------------------------- IEnumPins

class CEnumPins : public IEnumPins
{
public:
    CEnumPins(IPin** pPins, int nCount) : m_cRef(1), m_pos(0), m_count(nCount)
    {
        if (m_count > 4) m_count = 4;
        for (int i = 0; i < m_count; i++)
        {
            m_pins[i] = pPins[i];
            if (m_pins[i]) m_pins[i]->AddRef();
        }
        for (int i = m_count; i < 4; i++) m_pins[i] = NULL;
    }
    virtual ~CEnumPins()
    {
        for (int i = 0; i < m_count; i++)
            if (m_pins[i]) m_pins[i]->Release();
    }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv)
    {
        if (!ppv) return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IEnumPins))
        {
            *ppv = static_cast<IEnumPins*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = NULL;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() { return InterlockedIncrement(&m_cRef); }
    STDMETHODIMP_(ULONG) Release()
    {
        ULONG c = InterlockedDecrement(&m_cRef);
        if (c == 0) delete this;
        return c;
    }
    STDMETHODIMP Next(ULONG cPins, IPin** ppPins, ULONG* pcFetched)
    {
        if (!ppPins) return E_POINTER;
        ULONG done = 0;
        while (done < cPins && m_pos < m_count)
        {
            if (!m_pins[m_pos]) break;
            m_pins[m_pos]->AddRef();
            ppPins[done++] = m_pins[m_pos];
            m_pos++;
        }
        if (pcFetched) *pcFetched = done;
        return done == cPins ? S_OK : S_FALSE;
    }
    STDMETHODIMP Skip(ULONG cPins) { m_pos += (int)cPins; return m_pos > m_count ? S_FALSE : S_OK; }
    STDMETHODIMP Reset() { m_pos = 0; return S_OK; }
    STDMETHODIMP Clone(IEnumPins** ppEnum)
    {
        if (!ppEnum) return E_POINTER;
        CEnumPins* e = new CEnumPins(m_pins, m_count);
        if (!e) return E_OUTOFMEMORY;
        e->m_pos = m_pos;
        *ppEnum = e;
        return S_OK;
    }

private:
    LONG m_cRef;    // 与 InterlockedIncrement 的参数类型一致（32 位下 ULONG 是 unsigned long）
    int m_pos;
    int m_count;
    IPin* m_pins[4];
};

// ---------------------------------------------------------------- IEnumMediaTypes

class CEnumMediaTypes : public IEnumMediaTypes
{
public:
    CEnumMediaTypes() : m_cRef(1), m_pos(0) {}
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv)
    {
        if (!ppv) return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IEnumMediaTypes))
        {
            *ppv = static_cast<IEnumMediaTypes*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = NULL;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() { return InterlockedIncrement(&m_cRef); }
    STDMETHODIMP_(ULONG) Release()
    {
        ULONG c = InterlockedDecrement(&m_cRef);
        if (c == 0) delete this;
        return c;
    }
    STDMETHODIMP Next(ULONG cTypes, AM_MEDIA_TYPE** ppTypes, ULONG* pcFetched)
    {
        if (!ppTypes) return E_POINTER;
        ULONG done = 0;
        while (done < cTypes && m_pos < TypeCount())
        {
            int w, h;
            const GUID* sub;
            GetType(m_pos, &w, &h, &sub);
            if (FAILED(MakeMediaType(w, h, sub, &ppTypes[done])))
                break;
            done++;
            m_pos++;
        }
        if (pcFetched) *pcFetched = done;
        return done == cTypes ? S_OK : S_FALSE;
    }
    STDMETHODIMP Skip(ULONG cTypes) { m_pos += (int)cTypes; return m_pos > TypeCount() ? S_FALSE : S_OK; }
    STDMETHODIMP Reset() { m_pos = 0; return S_OK; }
    STDMETHODIMP Clone(IEnumMediaTypes** ppEnum)
    {
        if (!ppEnum) return E_POINTER;
        CEnumMediaTypes* e = new CEnumMediaTypes();
        if (!e) return E_OUTOFMEMORY;
        e->m_pos = m_pos;
        *ppEnum = e;
        return S_OK;
    }

private:
    LONG m_cRef;    // 与 InterlockedIncrement 的参数类型一致（32 位下 ULONG 是 unsigned long）
    int m_pos;
};

// ---------------------------------------------------------------- 输出 Pin

class CPocketCamPin : public IPin, public IAMStreamConfig, public IKsPropertySet
{
public:
    CPocketCamPin(CPocketCam* pFilter, int nType, const GUID& category, LPCWSTR name);
    virtual ~CPocketCamPin();

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv)
    {
        if (!ppv) return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IPin))
            *ppv = static_cast<IPin*>(this);
        else if (IsEqualIID(riid, IID_IAMStreamConfig))
            *ppv = static_cast<IAMStreamConfig*>(this);
        else if (IsEqualIID(riid, IID_IKsPropertySet))
            *ppv = static_cast<IKsPropertySet*>(this);
        else { *ppv = NULL; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() { return InterlockedIncrement(&m_cRef); }
    STDMETHODIMP_(ULONG) Release()
    {
        ULONG c = InterlockedDecrement(&m_cRef);
        if (c == 0) delete this;
        return c;
    }

    // IPin
    STDMETHODIMP Connect(IPin* pReceivePin, const AM_MEDIA_TYPE* pmt);
    STDMETHODIMP ReceiveConnection(IPin* pConnector, const AM_MEDIA_TYPE* pmt);
    STDMETHODIMP Disconnect();
    STDMETHODIMP ConnectedTo(IPin** ppPin);
    STDMETHODIMP ConnectionMediaType(AM_MEDIA_TYPE* pmt);
    STDMETHODIMP QueryPinInfo(PIN_INFO* pInfo);
    STDMETHODIMP QueryDirection(PIN_DIRECTION* pPinDir);
    STDMETHODIMP QueryId(LPWSTR* Id);
    STDMETHODIMP QueryAccept(const AM_MEDIA_TYPE* pmt);
    STDMETHODIMP EnumMediaTypes(IEnumMediaTypes** ppEnum);
    STDMETHODIMP QueryInternalConnections(IPin** apPin, ULONG* nPin) { return E_NOTIMPL; }
    STDMETHODIMP EndOfStream() { return S_OK; }
    STDMETHODIMP BeginFlush() { return S_OK; }
    STDMETHODIMP EndFlush() { return S_OK; }
    STDMETHODIMP NewSegment(REFERENCE_TIME, REFERENCE_TIME, double) { return S_OK; }

    // IAMStreamConfig
    STDMETHODIMP SetFormat(AM_MEDIA_TYPE* pmt);
    STDMETHODIMP GetFormat(AM_MEDIA_TYPE** ppmt);
    STDMETHODIMP GetNumberOfCapabilities(int* piCount, int* piSize);
    STDMETHODIMP GetStreamCaps(int iIndex, AM_MEDIA_TYPE** ppmt, BYTE* pSCC);

    // IKsPropertySet：只实现“引脚类别 = 采集”，这是 CaptureGraphBuilder2 找采集引脚的依据
    STDMETHODIMP Set(REFGUID, DWORD, void*, DWORD, void*, DWORD) { return E_NOTIMPL; }
    STDMETHODIMP Get(REFGUID guidPropSet, DWORD dwPropId, void*, DWORD,
                     void* pPropData, DWORD cbPropData, DWORD* pcbReturned);
    STDMETHODIMP QuerySupported(REFGUID guidPropSet, DWORD dwPropId, DWORD* pTypeSupport);

    // 供滤镜调用
    HRESULT Active();
    HRESULT Inactive();
    void SetPinName(LPCWSTR name) { lstrcpynW(m_szName, name, 64); }
    void SetMediaType(AM_MEDIA_TYPE* pmt);

private:
    HRESULT DoConnect(IPin* pConnector, const AM_MEDIA_TYPE* pmt, int nType);
    static DWORD WINAPI ThreadProc(LPVOID param);
    DWORD Loop();
    HRESULT DeliverOne(LONGLONG qpcFrameStart);
    void FillFromShared(BYTE* pDst, int dw, int dh, long cbBuffer);
    static void ConvertYuy2ToRgb(const BYTE* src, BYTE* dst, int w, int h, bool b32);
    bool OpenShared();
    void CloseShared();
    void CopyFrame(const BYTE* src, int sw, int sh, int sstride,
                   BYTE* dst, int dw, int dh, long cbBuffer);

    LONG m_cRef;    // 与 InterlockedIncrement 的参数类型一致（32 位下 ULONG 是 unsigned long）
    CPocketCam* m_pFilter;
    int m_nType;    // 格式表下标（分辨率 × 像素格式）
    GUID m_category;   // PIN_CATEGORY_CAPTURE / PIN_CATEGORY_PREVIEW
    CRITICAL_SECTION m_cs;
    IPin* m_pConnected;
    IMemInputPin* m_pInput;
    IMemAllocator* m_pAllocator;
    AM_MEDIA_TYPE m_mt;
    bool m_bHaveMt;
    HANDLE m_hThread;
    HANDLE m_hStop;
    volatile bool m_bStop;
    LONGLONG m_qpcRun;
    LONGLONG m_qpcInterval;
    HANDLE m_hMap;
    BYTE* m_pView;
    BYTE* m_pScratch;   // RGB 输出时先把画面缩放到这块 YUY2 缓冲，再转色
    DWORD m_dwLastOpen;
    WCHAR m_szName[64];
};

CPocketCamPin::CPocketCamPin(CPocketCam* pFilter, int nType, const GUID& category, LPCWSTR name)
    : m_cRef(1), m_pFilter(pFilter), m_nType(nType), m_category(category), m_pConnected(NULL),
      m_pInput(NULL), m_pAllocator(NULL), m_bHaveMt(false), m_hThread(NULL), m_hStop(NULL),
      m_bStop(false), m_qpcRun(0), m_qpcInterval(0), m_hMap(NULL), m_pView(NULL),
      m_pScratch(NULL), m_dwLastOpen(0)
{
    InitializeCriticalSection(&m_cs);
    ZeroMemory(&m_mt, sizeof(m_mt));
    lstrcpynW(m_szName, name ? name : L"Capture", 64);
}

CPocketCamPin::~CPocketCamPin()
{
    Inactive();
    if (m_pConnected) { m_pConnected->Release(); m_pConnected = NULL; }
    if (m_pInput) { m_pInput->Release(); m_pInput = NULL; }
    if (m_pAllocator) { m_pAllocator->Release(); m_pAllocator = NULL; }
    if (m_bHaveMt) { FreeMediaType(&m_mt); m_bHaveMt = false; }
    if (m_pScratch) { free(m_pScratch); m_pScratch = NULL; }
    DeleteCriticalSection(&m_cs);
}

void CPocketCamPin::SetMediaType(AM_MEDIA_TYPE* pmt)
{
    if (m_bHaveMt) { FreeMediaType(&m_mt); m_bHaveMt = false; }
    if (!pmt) return;
    ZeroMemory(&m_mt, sizeof(m_mt));
    m_mt.majortype = pmt->majortype;
    m_mt.subtype = pmt->subtype;
    m_mt.bFixedSizeSamples = pmt->bFixedSizeSamples;
    m_mt.bTemporalCompression = pmt->bTemporalCompression;
    m_mt.lSampleSize = pmt->lSampleSize;
    m_mt.formattype = pmt->formattype;
    m_mt.cbFormat = pmt->cbFormat;
    if (pmt->cbFormat && pmt->pbFormat)
    {
        m_mt.pbFormat = (BYTE*)CoTaskMemAlloc(pmt->cbFormat);
        if (m_mt.pbFormat) memcpy(m_mt.pbFormat, pmt->pbFormat, pmt->cbFormat);
        else { m_mt.cbFormat = 0; }
    }
    m_bHaveMt = true;
}

STDMETHODIMP CPocketCamPin::Connect(IPin* pReceivePin, const AM_MEDIA_TYPE* pmt)
{
    if (!pReceivePin) return E_POINTER;
    if (m_pConnected) return VFW_E_ALREADY_CONNECTED;

    if (pmt)
    {
        int nType = MatchType(pmt);
        if (nType < 0) return VFW_E_TYPE_NOT_ACCEPTED;
        return DoConnect(pReceivePin, pmt, nType);
    }

    // 没有指定媒体类型：按顺序把我们支持的格式拿去问对端，谁接受就用谁。
    // 老软件的视频渲染器只认 RGB，OBS 这类更愿意直接吃 YUY2。
    int total = TypeCount();
    int start = (m_nType >= 0 && m_nType < total) ? m_nType : 0;
    for (int k = 0; k < total; k++)
    {
        int nType = (start + k) % total;
        int w, h;
        const GUID* sub;
        GetType(nType, &w, &h, &sub);

        AM_MEDIA_TYPE* pdef = NULL;
        if (FAILED(MakeMediaType(w, h, sub, &pdef))) continue;

        HRESULT hrAccept = pReceivePin->QueryAccept(pdef);
        HRESULT hr = S_FALSE;
        // QueryAccept 返回 S_OK 才连；个别滤镜没实现（E_NOTIMPL）时也试一次
        if (hrAccept == S_OK || FAILED(hrAccept))
            hr = DoConnect(pReceivePin, pdef, nType);
        FreeMediaType(pdef);

        if (SUCCEEDED(hr)) return S_OK;
    }
    return VFW_E_CANNOT_CONNECT;
}

STDMETHODIMP CPocketCamPin::ReceiveConnection(IPin* pConnector, const AM_MEDIA_TYPE* pmt)
{
    if (!pConnector || !pmt) return E_POINTER;
    int nType = MatchType(pmt);
    if (nType < 0) return VFW_E_TYPE_NOT_ACCEPTED;
    return DoConnect(pConnector, pmt, nType);
}

HRESULT CPocketCamPin::DoConnect(IPin* pConnector, const AM_MEDIA_TYPE* pmt, int nType)
{
    EnterCriticalSection(&m_cs);
    if (m_pConnected)
    {
        LeaveCriticalSection(&m_cs);
        return VFW_E_ALREADY_CONNECTED;
    }

    IMemInputPin* pInput = NULL;
    HRESULT hr = pConnector->QueryInterface(IID_IMemInputPin, (void**)&pInput);
    if (FAILED(hr) || !pInput)
    {
        LeaveCriticalSection(&m_cs);
        return FAILED(hr) ? hr : E_FAIL;
    }

    hr = pConnector->ReceiveConnection(this, pmt);
    if (FAILED(hr)) { pInput->Release(); LeaveCriticalSection(&m_cs); return hr; }

    IMemAllocator* pAlloc = NULL;
    hr = pInput->GetAllocator(&pAlloc);
    if (FAILED(hr) || !pAlloc)
    {
        pInput->Release();
        LeaveCriticalSection(&m_cs);
        return VFW_E_NOT_CONNECTED;
    }

    m_pConnected = pConnector;
    m_pConnected->AddRef();
    m_pAllocator = pAlloc;
    m_pInput = pInput;
    m_nType = nType;
    SetMediaType((AM_MEDIA_TYPE*)pmt);

    LeaveCriticalSection(&m_cs);
    return S_OK;
}

STDMETHODIMP CPocketCamPin::Disconnect()
{
    Inactive();
    EnterCriticalSection(&m_cs);
    if (m_pConnected) { m_pConnected->Release(); m_pConnected = NULL; }
    if (m_pInput) { m_pInput->Release(); m_pInput = NULL; }
    if (m_pAllocator) { m_pAllocator->Release(); m_pAllocator = NULL; }
    LeaveCriticalSection(&m_cs);
    return S_OK;
}

STDMETHODIMP CPocketCamPin::ConnectedTo(IPin** ppPin)
{
    if (!ppPin) return E_POINTER;
    EnterCriticalSection(&m_cs);
    *ppPin = m_pConnected;
    if (m_pConnected) m_pConnected->AddRef();
    LeaveCriticalSection(&m_cs);
    return m_pConnected ? S_OK : VFW_E_NOT_CONNECTED;
}

STDMETHODIMP CPocketCamPin::ConnectionMediaType(AM_MEDIA_TYPE* pmt)
{
    if (!pmt) return E_POINTER;
    EnterCriticalSection(&m_cs);
    if (!m_bHaveMt) { LeaveCriticalSection(&m_cs); return VFW_E_NOT_CONNECTED; }
    ZeroMemory(pmt, sizeof(*pmt));
    pmt->majortype = m_mt.majortype;
    pmt->subtype = m_mt.subtype;
    pmt->bFixedSizeSamples = m_mt.bFixedSizeSamples;
    pmt->bTemporalCompression = m_mt.bTemporalCompression;
    pmt->lSampleSize = m_mt.lSampleSize;
    pmt->formattype = m_mt.formattype;
    pmt->cbFormat = m_mt.cbFormat;
    if (m_mt.cbFormat && m_mt.pbFormat)
    {
        pmt->pbFormat = (BYTE*)CoTaskMemAlloc(m_mt.cbFormat);
        if (pmt->pbFormat) memcpy(pmt->pbFormat, m_mt.pbFormat, m_mt.cbFormat);
        else pmt->cbFormat = 0;
    }
    LeaveCriticalSection(&m_cs);
    return S_OK;
}

STDMETHODIMP CPocketCamPin::QueryPinInfo(PIN_INFO* pInfo)
{
    if (!pInfo) return E_POINTER;
    pInfo->pFilter = (IBaseFilter*)m_pFilter;
    if (pInfo->pFilter) pInfo->pFilter->AddRef();
    pInfo->dir = PINDIR_OUTPUT;
    lstrcpynW(pInfo->achName, m_szName, 128);
    return S_OK;
}

STDMETHODIMP CPocketCamPin::QueryDirection(PIN_DIRECTION* pPinDir)
{
    if (!pPinDir) return E_POINTER;
    *pPinDir = PINDIR_OUTPUT;
    return S_OK;
}

STDMETHODIMP CPocketCamPin::QueryId(LPWSTR* Id)
{
    if (!Id) return E_POINTER;
    WCHAR* p = (WCHAR*)CoTaskMemAlloc(64 * sizeof(WCHAR));
    if (!p) return E_OUTOFMEMORY;
    lstrcpynW(p, m_szName, 64);
    *Id = p;
    return S_OK;
}

STDMETHODIMP CPocketCamPin::QueryAccept(const AM_MEDIA_TYPE* pmt)
{
    return MatchType(pmt) >= 0 ? S_OK : S_FALSE;
}

STDMETHODIMP CPocketCamPin::EnumMediaTypes(IEnumMediaTypes** ppEnum)
{
    if (!ppEnum) return E_POINTER;
    CEnumMediaTypes* e = new CEnumMediaTypes();
    if (!e) return E_OUTOFMEMORY;
    *ppEnum = e;
    return S_OK;
}

STDMETHODIMP CPocketCamPin::SetFormat(AM_MEDIA_TYPE* pmt)
{
    if (!pmt) return E_POINTER;
    int nType = MatchType(pmt);
    if (nType < 0) return VFW_E_TYPE_NOT_ACCEPTED;
    EnterCriticalSection(&m_cs);
    m_nType = nType;
    SetMediaType(pmt);
    LeaveCriticalSection(&m_cs);
    return S_OK;
}

STDMETHODIMP CPocketCamPin::GetFormat(AM_MEDIA_TYPE** ppmt)
{
    if (!ppmt) return E_POINTER;
    int w, h;
    const GUID* sub;
    GetType(m_nType, &w, &h, &sub);
    return MakeMediaType(w, h, sub, ppmt);
}

STDMETHODIMP CPocketCamPin::GetNumberOfCapabilities(int* piCount, int* piSize)
{
    if (piCount) *piCount = TypeCount();
    if (piSize) *piSize = sizeof(VIDEO_STREAM_CONFIG_CAPS);
    return S_OK;
}

STDMETHODIMP CPocketCamPin::GetStreamCaps(int iIndex, AM_MEDIA_TYPE** ppmt, BYTE* pSCC)
{
    if (iIndex < 0 || iIndex >= TypeCount()) return S_FALSE;
    int w, h;
    const GUID* sub;
    GetType(iIndex, &w, &h, &sub);
    if (FAILED(MakeMediaType(w, h, sub, ppmt))) return E_OUTOFMEMORY;
    if (pSCC)
    {
        VIDEO_STREAM_CONFIG_CAPS* caps = (VIDEO_STREAM_CONFIG_CAPS*)pSCC;
        ZeroMemory(caps, sizeof(*caps));
        caps->guid = FORMAT_VideoInfo;
        caps->MinCroppingSize.cx = w;
        caps->MinCroppingSize.cy = h;
        caps->MaxCroppingSize.cx = w;
        caps->MaxCroppingSize.cy = h;
        caps->CropGranularityX = 2;
        caps->CropGranularityY = 1;
        caps->CropAlignX = 2;
        caps->CropAlignY = 1;
        caps->MinOutputSize.cx = w;
        caps->MinOutputSize.cy = h;
        caps->MaxOutputSize.cx = w;
        caps->MaxOutputSize.cy = h;
        caps->OutputGranularityX = 1;
        caps->OutputGranularityY = 1;
        caps->MinFrameInterval = 10000000LL / g_Fps;
        caps->MaxFrameInterval = 10000000LL / g_Fps;
        caps->MinBitsPerSecond = w * h * SubtypeBits(sub) * g_Fps / 8;
        caps->MaxBitsPerSecond = caps->MinBitsPerSecond;
    }
    return S_OK;
}

// AMPROPSETID_Pin / PIN_CATEGORY：告诉图构建器“我是采集引脚”
STDMETHODIMP CPocketCamPin::Get(REFGUID guidPropSet, DWORD dwPropId, void*, DWORD,
                                void* pPropData, DWORD cbPropData, DWORD* pcbReturned)
{
    if (pcbReturned) *pcbReturned = 0;
    if (!IsEqualGUID(guidPropSet, AMPROPSETID_Pin)) return E_PROP_SET_UNSUPPORTED;
    if (dwPropId != AMPROPERTY_PIN_CATEGORY) return E_PROP_ID_UNSUPPORTED;
    if (pcbReturned) *pcbReturned = sizeof(GUID);
    if (!pPropData || cbPropData < sizeof(GUID)) return E_UNEXPECTED;
    *(GUID*)pPropData = m_category;
    return S_OK;
}

STDMETHODIMP CPocketCamPin::QuerySupported(REFGUID guidPropSet, DWORD dwPropId, DWORD* pTypeSupport)
{
    if (!IsEqualGUID(guidPropSet, AMPROPSETID_Pin) || dwPropId != AMPROPERTY_PIN_CATEGORY)
        return E_PROP_SET_UNSUPPORTED;
    if (pTypeSupport) *pTypeSupport = KSPROPERTY_SUPPORT_GET;
    return S_OK;
}

// ---------------------------------------------------------------- 线程与取帧

HRESULT CPocketCamPin::Active()
{
    EnterCriticalSection(&m_cs);
    if (m_hThread)
    {
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }
    if (!m_pAllocator || !m_pConnected)
    {
        LeaveCriticalSection(&m_cs);
        return VFW_E_NOT_CONNECTED;
    }
    m_pAllocator->Commit();
    m_hStop = CreateEventW(NULL, TRUE, FALSE, NULL);
    m_bStop = false;
    m_qpcRun = QpcNow();
    m_qpcInterval = QpcFreq() / g_Fps;
    m_hThread = CreateThread(NULL, 0, ThreadProc, this, 0, NULL);
    if (!m_hThread)
    {
        CloseHandle(m_hStop);
        m_hStop = NULL;
        m_pAllocator->Decommit();
        LeaveCriticalSection(&m_cs);
        return E_OUTOFMEMORY;
    }
    LeaveCriticalSection(&m_cs);
    return S_OK;
}

HRESULT CPocketCamPin::Inactive()
{
    EnterCriticalSection(&m_cs);
    if (m_hThread)
    {
        m_bStop = true;
        SetEvent(m_hStop);
        LeaveCriticalSection(&m_cs);
        WaitForSingleObject(m_hThread, 3000);
        EnterCriticalSection(&m_cs);
        CloseHandle(m_hThread);
        m_hThread = NULL;
        if (m_hStop) { CloseHandle(m_hStop); m_hStop = NULL; }
        if (m_pAllocator) m_pAllocator->Decommit();
    }
    CloseShared();
    LeaveCriticalSection(&m_cs);
    return S_OK;
}

DWORD WINAPI CPocketCamPin::ThreadProc(LPVOID param)
{
    return ((CPocketCamPin*)param)->Loop();
}

DWORD CPocketCamPin::Loop()
{
    LONGLONG next = m_qpcRun + m_qpcInterval;
    while (!m_bStop)
    {
        WaitUntilQpc(next, m_hStop);
        if (m_bStop) break;
        DeliverOne(next - m_qpcRun);
        next += m_qpcInterval;
        LONGLONG now = QpcNow();
        if (next < now - m_qpcInterval * 3) next = now + m_qpcInterval;
    }
    return 0;
}

HRESULT CPocketCamPin::DeliverOne(LONGLONG qpcFrameStart)
{
    if (!m_pAllocator || !m_pInput || !m_pConnected) return S_OK;

    IMediaSample* pSample = NULL;
    HRESULT hr = m_pAllocator->GetBuffer(&pSample, NULL, NULL, 0);
    if (FAILED(hr) || !pSample) return hr;

    BYTE* pData = NULL;
    hr = pSample->GetPointer(&pData);
    if (SUCCEEDED(hr) && pData)
    {
        long cb = pSample->GetSize();
        int w, h;
        const GUID* sub;
        GetType(m_nType, &w, &h, &sub);
        int bytes = SubtypeBytes(sub);
        FillFromShared(pData, w, h, cb);
        pSample->SetActualDataLength(w * h * bytes);
    }

    REFERENCE_TIME rtStart = (REFERENCE_TIME)(qpcFrameStart * 10000000LL / QpcFreq());
    REFERENCE_TIME rtEnd = rtStart + 10000000LL / g_Fps;
    pSample->SetTime(&rtStart, &rtEnd);
    pSample->SetSyncPoint(TRUE);
    pSample->SetPreroll(FALSE);

    hr = m_pInput->Receive(pSample);
    pSample->Release();
    return hr;
}

bool CPocketCamPin::OpenShared()
{
    if (m_pView) return true;
    DWORD now = GetTickCount();
    if (m_dwLastOpen != 0 && (now - m_dwLastOpen) < 500) return false;
    m_dwLastOpen = now;

    HANDLE h = OpenFileMappingW(FILE_MAP_READ, FALSE, POCKETCAM_MAP_NAME);
    if (!h) return false;
    void* p = MapViewOfFile(h, FILE_MAP_READ, 0, 0, POCKETCAM_TOTAL);
    if (!p) { CloseHandle(h); return false; }
    m_hMap = h;
    m_pView = (BYTE*)p;
    return true;
}

void CPocketCamPin::CloseShared()
{
    if (m_pView) { UnmapViewOfFile(m_pView); m_pView = NULL; }
    if (m_hMap) { CloseHandle(m_hMap); m_hMap = NULL; }
}

void CPocketCamPin::CopyFrame(const BYTE* src, int sw, int sh, int sstride,
                              BYTE* dst, int dw, int dh, long cbBuffer)
{
    int dstride = dw * 2;
    if (sw == dw && sh == dh)
    {
        for (int y = 0; y < dh; y++)
        {
            if ((long)(y + 1) * dstride > cbBuffer) break;
            memcpy(dst + (long)y * dstride, src + (long)y * sstride, dstride);
        }
        return;
    }

    // 最近邻缩放（YUY2 每 4 字节 = 2 个像素）
    int spairs = sw / 2, dpairs = dw / 2;
    for (int y = 0; y < dh; y++)
    {
        if ((long)(y + 1) * dstride > cbBuffer) break;
        int sy = (int)((LONGLONG)y * sh / dh);
        const BYTE* srow = src + (long)sy * sstride;
        BYTE* drow = dst + (long)y * dstride;
        for (int px = 0; px < dpairs; px++)
        {
            int spx = (int)((LONGLONG)px * spairs / dpairs);
            memcpy(drow + px * 4, srow + spx * 4, 4);
        }
    }
}

static inline BYTE ClampByte(int v)
{
    return (BYTE)(v < 0 ? 0 : (v > 255 ? 255 : v));
}

void CPocketCamPin::ConvertYuy2ToRgb(const BYTE* src, BYTE* dst, int w, int h, bool b32)
{
    int step = b32 ? 4 : 3;
    for (int y = 0; y < h; y++)
    {
        const BYTE* s = src + (long)y * w * 2;
        BYTE* d = dst + (long)y * w * step;
        for (int x = 0; x < w; x += 2)
        {
            int Y0 = s[0], U = s[1] - 128, Y1 = s[2], V = s[3] - 128;
            int r0 = Y0 + (int)(1.402f * V);
            int g0 = Y0 - (int)(0.344f * U) - (int)(0.714f * V);
            int b0 = Y0 + (int)(1.772f * U);
            int r1 = Y1 + (int)(1.402f * V);
            int g1 = Y1 - (int)(0.344f * U) - (int)(0.714f * V);
            int b1 = Y1 + (int)(1.772f * U);
            // DirectShow 的 RGB24/RGB32 都是 BGR 顺序
            d[0] = ClampByte(b0); d[1] = ClampByte(g0); d[2] = ClampByte(r0);
            if (b32) d[3] = 0xFF;
            d += step;
            d[0] = ClampByte(b1); d[1] = ClampByte(g1); d[2] = ClampByte(r1);
            if (b32) d[3] = 0xFF;
            d += step;
            s += 4;
        }
    }
}

void CPocketCamPin::FillFromShared(BYTE* pDst, int dw, int dh, long cbBuffer)
{
    int w, h;
    const GUID* sub;
    GetType(m_nType, &w, &h, &sub);
    bool yuy2 = IsYuy2(sub);

    // RGB 输出时先按 YUY2 缩放，再整帧转色
    BYTE* pYuy2 = pDst;
    long cbYuy2 = cbBuffer;
    if (!yuy2)
    {
        if (!m_pScratch) m_pScratch = (BYTE*)malloc(POCKETCAM_MAX_W * POCKETCAM_MAX_H * 2);
        if (!m_pScratch) return;
        pYuy2 = m_pScratch;
        cbYuy2 = (long)w * h * 2;
    }

    bool got = false;
    if (!m_pView) OpenShared();
    if (m_pView)
    {
        const PocketCamHeader* hdr = (const PocketCamHeader*)m_pView;
        if (hdr->magic == POCKETCAM_MAGIC && hdr->version == POCKETCAM_VERSION)
        {
            UINT32 slot = hdr->activeSlot;
            if (slot < POCKETCAM_SLOTS && hdr->seq[slot] != 0)
            {
                UINT32 sw = hdr->width, sh = hdr->height, ss = hdr->stride;
                if (sw >= 2 && sh >= 2 && sw <= POCKETCAM_MAX_W && sh <= POCKETCAM_MAX_H && ss >= sw * 2)
                {
                    const BYTE* src = m_pView + POCKETCAM_HEADER + (size_t)slot * POCKETCAM_SLOT_SZ;
                    CopyFrame(src, (int)sw, (int)sh, (int)ss, pYuy2, dw, dh, cbYuy2);
                    got = true;
                }
            }
        }
    }

    if (!got)
    {
        int dstride = dw * 2;
        for (int y = 0; y < dh; y++)
        {
            if ((long)(y + 1) * dstride > cbYuy2) break;
            BYTE* row = pYuy2 + (long)y * dstride;
            for (int x = 0; x < dstride; x += 2) { row[x] = 0x10; row[x + 1] = 0x80; }
        }
    }

    if (!yuy2)
        ConvertYuy2ToRgb(pYuy2, pDst, w, h, IsEqualGUID(*sub, MEDIASUBTYPE_RGB32));
}

// ---------------------------------------------------------------- 滤镜

class CPocketCam : public IBaseFilter
{
public:
    CPocketCam();
    virtual ~CPocketCam();

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv)
    {
        if (!ppv) return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IBaseFilter) ||
            IsEqualIID(riid, IID_IMediaFilter) || IsEqualIID(riid, IID_IPersist))
        {
            *ppv = static_cast<IBaseFilter*>(this);
            AddRef();
            return S_OK;
        }
        // 部分采集软件（ECap、AMCap 这类老工具）不查询引脚，而是直接对滤镜查询
        // IAMStreamConfig，拿不到就报“无法找到 IAMStreamConfig”。这里转给输出引脚。
        if (m_pPins[0] && IsEqualIID(riid, IID_IAMStreamConfig))
            return m_pPins[0]->QueryInterface(riid, ppv);
        *ppv = NULL;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() { return InterlockedIncrement(&m_cRef); }
    STDMETHODIMP_(ULONG) Release()
    {
        ULONG c = InterlockedDecrement(&m_cRef);
        if (c == 0) delete this;
        return c;
    }

    // IPersist
    STDMETHODIMP GetClassID(CLSID* pClsID)
    {
        if (!pClsID) return E_POINTER;
        *pClsID = CLSID_PocketCamFilter;
        return S_OK;
    }

    // IMediaFilter
    STDMETHODIMP Stop()
    {
        for (int i = 0; i < 2; i++)
            if (m_pPins[i]) m_pPins[i]->Inactive();
        EnterCriticalSection(&m_cs);
        m_State = State_Stopped;
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }
    STDMETHODIMP Pause()
    {
        EnterCriticalSection(&m_cs);
        m_State = State_Paused;
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }
    STDMETHODIMP Run(REFERENCE_TIME)
    {
        EnterCriticalSection(&m_cs);
        m_State = State_Running;
        LeaveCriticalSection(&m_cs);
        for (int i = 0; i < 2; i++)
        {
            if (!m_pPins[i]) continue;
            HRESULT hr = m_pPins[i]->Active();
            // 没接线的引脚（比如只用预览时采集引脚）不算错误
            if (SUCCEEDED(hr) || hr == VFW_E_NOT_CONNECTED) continue;
            return hr;
        }
        return S_OK;
    }
    STDMETHODIMP GetState(DWORD, FILTER_STATE* pState)
    {
        if (!pState) return E_POINTER;
        EnterCriticalSection(&m_cs);
        *pState = m_State;
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }
    STDMETHODIMP SetSyncSource(IReferenceClock* pClock)
    {
        EnterCriticalSection(&m_cs);
        if (m_pClock) { m_pClock->Release(); m_pClock = NULL; }
        if (pClock) { m_pClock = pClock; m_pClock->AddRef(); }
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }
    STDMETHODIMP GetSyncSource(IReferenceClock** ppClock)
    {
        if (!ppClock) return E_POINTER;
        EnterCriticalSection(&m_cs);
        *ppClock = m_pClock;
        if (m_pClock) m_pClock->AddRef();
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }

    // IBaseFilter
    STDMETHODIMP EnumPins(IEnumPins** ppEnum)
    {
        if (!ppEnum) return E_POINTER;
        CEnumPins* e = new CEnumPins((IPin**)m_pPins, 2);
        if (!e) return E_OUTOFMEMORY;
        *ppEnum = e;
        return S_OK;
    }
    STDMETHODIMP FindPin(LPCWSTR Id, IPin** ppPin)
    {
        if (!Id || !ppPin) return E_POINTER;
        int idx = -1;
        if (lstrcmpiW(Id, L"Capture") == 0 || lstrcmpiW(Id, L"Output") == 0) idx = 0;
        else if (lstrcmpiW(Id, L"Preview") == 0) idx = 1;
        if (idx >= 0 && m_pPins[idx])
        {
            *ppPin = m_pPins[idx];
            m_pPins[idx]->AddRef();
            return S_OK;
        }
        *ppPin = NULL;
        return VFW_E_NOT_FOUND;
    }
    STDMETHODIMP QueryFilterInfo(FILTER_INFO* pInfo)
    {
        if (!pInfo) return E_POINTER;
        EnterCriticalSection(&m_cs);
        lstrcpynW(pInfo->achName, m_Name, 128);
        pInfo->pGraph = m_pGraph;
        if (m_pGraph) m_pGraph->AddRef();
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }
    STDMETHODIMP JoinFilterGraph(IFilterGraph* pGraph, LPCWSTR pName)
    {
        EnterCriticalSection(&m_cs);
        m_pGraph = pGraph;
        if (pGraph) pGraph->AddRef();
        if (pName) lstrcpynW(m_Name, pName, 128);
        LeaveCriticalSection(&m_cs);
        return S_OK;
    }
    STDMETHODIMP QueryVendorInfo(LPWSTR* pVendorInfo)
    {
        if (pVendorInfo) *pVendorInfo = NULL;
        return E_NOTIMPL;
    }

private:
    LONG m_cRef;    // 与 InterlockedIncrement 的参数类型一致（32 位下 ULONG 是 unsigned long）
    CPocketCamPin* m_pPins[2];
    IReferenceClock* m_pClock;
    IFilterGraph* m_pGraph;
    FILTER_STATE m_State;
    CRITICAL_SECTION m_cs;
    WCHAR m_Name[128];
};

CPocketCam::CPocketCam()
    : m_cRef(1), m_pClock(NULL), m_pGraph(NULL), m_State(State_Stopped)
{
    InitializeCriticalSection(&m_cs);
    lstrcpyW(m_Name, POCKETCAM_FILTER_NAME);
    // 真摄像头一般有两个输出引脚：采集 + 预览。AMCap / ECap 这类老工具
    // 只请求 PIN_CATEGORY_PREVIEW，没有预览引脚就会在打开设备时报“请检查装置连接状况”。
    m_pPins[0] = new CPocketCamPin(this, 0, PIN_CATEGORY_CAPTURE, L"Capture");
    m_pPins[1] = new CPocketCamPin(this, 0, PIN_CATEGORY_PREVIEW, L"Preview");
}

CPocketCam::~CPocketCam()
{
    for (int i = 0; i < 2; i++)
        if (m_pPins[i]) { m_pPins[i]->Release(); m_pPins[i] = NULL; }
    if (m_pClock) { m_pClock->Release(); m_pClock = NULL; }
    if (m_pGraph) { m_pGraph->Release(); m_pGraph = NULL; }
    DeleteCriticalSection(&m_cs);
}

// ---------------------------------------------------------------- 类工厂

class CClassFactory : public IClassFactory
{
public:
    CClassFactory() : m_cRef(1) {}
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv)
    {
        if (!ppv) return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory))
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = NULL;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() { return InterlockedIncrement(&m_cRef); }
    STDMETHODIMP_(ULONG) Release()
    {
        ULONG c = InterlockedDecrement(&m_cRef);
        if (c == 0) delete this;
        return c;
    }
    STDMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv)
    {
        if (!ppv) return E_POINTER;
        *ppv = NULL;
        if (pUnkOuter) return CLASS_E_NOAGGREGATION;
        CPocketCam* p = new CPocketCam();
        if (!p) return E_OUTOFMEMORY;
        HRESULT hr = p->QueryInterface(riid, ppv);
        p->Release();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL) { return S_OK; }

private:
    LONG m_cRef;    // 与 InterlockedIncrement 的参数类型一致（32 位下 ULONG 是 unsigned long）
};

// ---------------------------------------------------------------- 注册表

// 32 位与 64 位各有独立的注册表视图：64 位滤镜写在 64 位视图，32 位滤镜写在 WOW6432Node，
// 这样 64 位软件和老 32 位软件（很多国产工具）都能各自枚举到同一个设备。
static DWORD WowFlags()
{
#ifdef _WIN64
    return KEY_WOW64_64KEY;
#else
    BOOL wow = FALSE;
    IsWow64Process(GetCurrentProcess(), &wow);
    return wow ? KEY_WOW64_32KEY : 0;
#endif
}

static LONG RegWrite(HKEY hRoot, const WCHAR* sub, const WCHAR* name, const WCHAR* value)
{
    HKEY hk = NULL;
    LONG r = RegCreateKeyExW(hRoot, sub, 0, NULL, REG_OPTION_NON_VOLATILE,
                             KEY_WRITE | WowFlags(), NULL, &hk, NULL);
    if (r != ERROR_SUCCESS) return r;
    if (value)
    {
        // name == NULL 时写入默认值（InprocServer32 的 DLL 路径必须放在默认值里）
        r = RegSetValueExW(hk, name, 0, REG_SZ, (const BYTE*)value,
                           (DWORD)((lstrlenW(value) + 1) * sizeof(WCHAR)));
    }
    RegCloseKey(hk);
    return r;
}

static LONG RegWriteBinary(HKEY hRoot, const WCHAR* sub, const WCHAR* name, const BYTE* data, DWORD cb)
{
    HKEY hk = NULL;
    LONG r = RegCreateKeyExW(hRoot, sub, 0, NULL, REG_OPTION_NON_VOLATILE,
                             KEY_WRITE | WowFlags(), NULL, &hk, NULL);
    if (r != ERROR_SUCCESS) return r;
    r = RegSetValueExW(hk, name, 0, REG_BINARY, data, cb);
    RegCloseKey(hk);
    return r;
}

static void DeleteTreeRecursive(HKEY hRoot, const WCHAR* sub)
{
    HKEY hk = NULL;
    if (RegOpenKeyExW(hRoot, sub, 0, KEY_ALL_ACCESS | WowFlags(), &hk) != ERROR_SUCCESS)
        return;

    WCHAR child[128];
    for (;;)
    {
        DWORD cb = sizeof(child) - sizeof(WCHAR);
        FILETIME ft;
        ZeroMemory(child, sizeof(child));
        if (RegEnumKeyExW(hk, 0, child, &cb, NULL, NULL, NULL, &ft) != ERROR_SUCCESS) break;

        WCHAR full[512];
        lstrcpynW(full, sub, 400);
        lstrcatW(full, L"\\");
        lstrcatW(full, child);
        DeleteTreeRecursive(hRoot, full);
    }
    RegCloseKey(hk);
    RegDeleteKeyExW(hRoot, sub, WowFlags(), 0);
}

static const WCHAR* kClsidKey =
    L"Software\\Classes\\CLSID\\{A7B0C1D2-3E4F-4A5B-9C8D-1E2F3A4B5C6D}";
static const WCHAR* kInstanceKey =
    L"Software\\Classes\\CLSID\\{860BB310-5D01-11D0-BD3B-00A0C911CE86}\\Instance\\{A7B0C1D2-3E4F-4A5B-9C8D-1E2F3A4B5C6D}";

HRESULT RegisterServer(BOOL bRegister)
{
    HKEY root = HKEY_CURRENT_USER;   // 写入 HKCU，无需管理员权限

    if (bRegister)
    {
        WCHAR dllPath[MAX_PATH];
        if (!GetModuleFileNameW(g_hInst, dllPath, MAX_PATH)) return E_FAIL;

        RegWrite(root, kClsidKey, NULL, POCKETCAM_FILTER_DESC);
        RegWrite(root, kClsidKey, L"FriendlyName", POCKETCAM_FILTER_NAME);
        RegWrite(root, L"Software\\Classes\\CLSID\\{A7B0C1D2-3E4F-4A5B-9C8D-1E2F3A4B5C6D}\\InprocServer32",
                 NULL, dllPath);
        RegWrite(root, L"Software\\Classes\\CLSID\\{A7B0C1D2-3E4F-4A5B-9C8D-1E2F3A4B5C6D}\\InprocServer32",
                 L"ThreadingModel", L"Both");

        RegWrite(root, kInstanceKey, L"CLSID", L"{A7B0C1D2-3E4F-4A5B-9C8D-1E2F3A4B5C6D}");
        RegWrite(root, kInstanceKey, L"FriendlyName", POCKETCAM_FILTER_NAME);
        RegWrite(root, kInstanceKey, L"Description", POCKETCAM_FILTER_DESC);
        // FilterData 必须是 REG_BINARY（值 0x02 = 采集设备）。写成 REG_SZ 会让
        // DirectShow 的设备枚举器读名称失败，很多软件因此「找不到相机」。
        BYTE filterData = 0x02;
        RegWriteBinary(root, kInstanceKey, L"FilterData", &filterData, 1);
    }
    else
    {
        DeleteTreeRecursive(root, kInstanceKey);
        DeleteTreeRecursive(root, kClsidKey);
    }
    return S_OK;
}

// ---------------------------------------------------------------- DLL 入口

extern "C" BOOL WINAPI DllMain(HINSTANCE hInst, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) g_hInst = hInst;
    return TRUE;
}

extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = NULL;
    if (!IsEqualCLSID(rclsid, CLSID_PocketCamFilter)) return CLASS_E_CLASSNOTAVAILABLE;

    CClassFactory* f = new CClassFactory();
    if (!f) return E_OUTOFMEMORY;
    HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}

extern "C" HRESULT WINAPI DllCanUnloadNow()
{
    return S_FALSE;   // 常驻内存，避免卸载竞态
}

extern "C" HRESULT WINAPI DllRegisterServer()
{
    return RegisterServer(TRUE);
}

extern "C" HRESULT WINAPI DllUnregisterServer()
{
    return RegisterServer(FALSE);
}

