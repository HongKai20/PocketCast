# 卸载 / 反注册 PocketCast 虚拟摄像头
# 用法： powershell -ExecutionPolicy Bypass -File unregister.ps1
#        powershell -ExecutionPolicy Bypass -File unregister.ps1 -Ax path\to\PocketCam.ax

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

$clsidKey = "HKCU:\Software\Classes\CLSID\{A7B0C1D2-3E4F-4A5B-9C8D-1E2F3A4B5C6D}"
$deviceKey = "HKCU:\Software\Classes\CLSID\{860BB310-5D01-11D0-BD3B-00A0C911CE86}\Instance\{A7B0C1D2-3E4F-4A5B-9C8D-1E2F3A4B5C6D}"

if ($Ax -and (Test-Path $Ax)) {
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
    if ($h -ne [IntPtr]::Zero) {
        $p = [NativeAx]::GetProcAddress($h, "DllUnregisterServer")
        if ($p -ne [IntPtr]::Zero) {
            $fn = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer($p, [HResultFn])
            $fn.Invoke() | Out-Null
        }
        [NativeAx]::FreeLibrary($h) | Out-Null
    }
}

foreach ($k in @($deviceKey, $clsidKey)) {
    if (Test-Path $k) {
        Remove-Item -Recurse -Force $k
        Write-Host "已删除 $k"
    }
}

# 32 位视图（WOW6432Node）：交给 32 位助手反注册，再兜底删键
$dir = if ($Ax) { Split-Path -Parent $Ax } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$ax32 = Join-Path $dir "PocketCam32.ax"
$reg32 = Join-Path $dir "reg32.exe"
if ((Test-Path $ax32) -and (Test-Path $reg32)) {
    & $reg32 $ax32 "unregister" | Out-Null
}

foreach ($k in @("HKCU:\Software\Classes\WOW6432Node\CLSID\{860BB310-5D01-11D0-BD3B-00A0C911CE86}\Instance\{A7B0C1D2-3E4F-4A5B-9C8D-1E2F3A4B5C6D}",
                 "HKCU:\Software\Classes\WOW6432Node\CLSID\{A7B0C1D2-3E4F-4A5B-9C8D-1E2F3A4B5C6D}")) {
    if (Test-Path $k) {
        Remove-Item -Recurse -Force $k
        Write-Host "已删除 $k"
    }
}

Write-Host "已反注册 PocketCast Virtual Camera。"
