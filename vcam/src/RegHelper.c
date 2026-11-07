// 32 位注册助手：64 位程序没法把 32 位 PocketCam32.ax 载入自己的进程，
// 所以由这个小工具（本身就是 32 位）去调用它的 DllRegisterServer / DllUnregisterServer。
// 用法： reg32.exe <PocketCam32.ax 路径> [register|unregister]
// 退出码：0 = 成功；否则非 0。
#include <windows.h>
#include <stdio.h>
#include <wchar.h>

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2)
    {
        printf("usage: reg32.exe <PocketCam32.ax> [register|unregister]\n");
        return 2;
    }

    const char* fn = "DllRegisterServer";
    if (argc >= 3 && _wcsicmp(argv[2], L"unregister") == 0)
        fn = "DllUnregisterServer";

    HMODULE h = LoadLibraryW(argv[1]);
    if (!h)
    {
        printf("LoadLibrary failed: %lu\n", GetLastError());
        return 3;
    }

    FARPROC p = GetProcAddress(h, fn);
    if (!p)
    {
        printf("GetProcAddress(%s) failed: %lu\n", fn, GetLastError());
        return 4;
    }

    typedef HRESULT(WINAPI * PFN)(void);
    HRESULT hr = ((PFN)p)();
    printf("hr=0x%08X\n", (unsigned)hr);
    return hr == S_OK ? 0 : 1;
}
