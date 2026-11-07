# 注册 PocketCast 虚拟摄像头（DirectShow 滤镜）
# 不需要管理员权限：注册信息写入 HKCU\Software\Classes
#
# 用法： powershell -ExecutionPolicy Bypass -File register.ps1
#        powershell -ExecutionPolicy Bypass -File register.ps1 -Ax path\to\PocketCam.ax

param(
    [string]$Ax = ""
)

$ErrorActionPreference = "Stop"

if (-not $Ax) {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    $candidates = @(
        (Join-Path $here "build\PocketCam.ax"),
        (Join-Path $here "PocketCam.ax"),
        (Join-Path (Split-Path -Parent $here) "PocketCam.ax")
    )
    $Ax = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $Ax -or -not (Test-Path $Ax)) {
    Write-Error "找不到 PocketCam.ax。请先编译（build.ps1），或用 -Ax 指定文件路径。"
    exit 1
}

$Ax = (Resolve-Path $Ax).Path

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class NativeAx {
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr h, string name);
    [DllImport("kernel32", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr h);
}
public delegate int HResultFn();
"@

$h = [NativeAx]::LoadLibraryW($Ax)
if ($h -eq [IntPtr]::Zero) {
    Write-Error "加载 $Ax 失败（错误码 $([Runtime.InteropServices.Marshal]::GetLastWin32Error())）。可能是位数不匹配或缺少运行库。"
    exit 1
}

$p = [NativeAx]::GetProcAddress($h, "DllRegisterServer")
if ($p -eq [IntPtr]::Zero) {
    [NativeAx]::FreeLibrary($h) | Out-Null
    Write-Error "该文件不是有效的 DirectShow 滤镜（缺少 DllRegisterServer 导出）。"
    exit 1
}

$fn = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer($p, [HResultFn])
$hr = $fn.Invoke()
[NativeAx]::FreeLibrary($h) | Out-Null

if ($hr -ne 0) {
    Write-Error ("注册失败，HRESULT = 0x{0:X8}" -f $hr)
    exit 1
}

$key = "HKCU:\Software\Classes\CLSID\{860BB310-5D01-11D0-BD3B-00A0C911CE86}\Instance\{A7B0C1D2-3E4F-4A5B-9C8D-1E2F3A4B5C6D}"
if (-not (Test-Path $key)) {
    Write-Warning "注册表写入后未找到设备项，请检查上方输出。"
}

# 32 位滤镜：老 32 位软件（ecap 这类工具、32 位播放器）读的是 WOW6432Node 视图。
# PowerShell 本身是 64 位，载入不了 32 位 DLL，交给随附的 reg32.exe 代劳。
$dir = Split-Path -Parent $Ax
$ax32 = Join-Path $dir "PocketCam32.ax"
$reg32 = Join-Path $dir "reg32.exe"
if ((Test-Path $ax32) -and (Test-Path $reg32)) {
    & $reg32 $ax32 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning "32 位滤镜注册失败（退出码 $LASTEXITCODE）" }
    else { Write-Host "已注册 32 位滤镜：$ax32" }
}
else {
    Write-Warning "未找到 PocketCam32.ax / reg32.exe，32 位软件将看不到摄像头。"
}

Write-Host "已注册：$Ax"
Write-Host "设备名：PocketCast Virtual Camera"
Write-Host "提示：已打开过的软件（微信 / 腾讯会议 / OBS / ecap）需要重启才能看到新摄像头。"
