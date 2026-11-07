# PocketCast 虚拟摄像头（DirectShow 滤镜）构建脚本
#
# 需要一个 64 位 C++ 编译器，任选其一（脚本会按顺序自动查找）：
#   1) w64devkit 的 gcc（推荐，解压即用）    https://github.com/skeeto/w64devkit/releases
#      把 w64devkit 解压到本仓库的 _toolchain\w64devkit，或把 gcc.exe 加入 PATH
#   2) zig（单文件免安装）                  https://ziglang.org/download/
#      如果装过 `pip install ziglang`，本脚本也会自动识别
#   3) Visual Studio 2019/2022 的 MSVC（含 “使用 C++ 的桌面开发” 工作负载）
#
# 用法：  powershell -ExecutionPolicy Bypass -File build.ps1
# 产物：  build\PocketCam.ax    （64 位滤镜）
#        build\PocketCam32.ax  （32 位滤镜，需 i686-w64-mingw32-gcc，w64devkit 自带）
#        build\reg32.exe       （32 位注册助手）

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src"
$out = Join-Path $root "build"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$ax = Join-Path $out "PocketCam.ax"
$ax32 = Join-Path $out "PocketCam32.ax"
$reg32 = Join-Path $out "reg32.exe"

$cpp = Join-Path $src "PocketCam.cpp"
$def = Join-Path $src "PocketCam.def"
$regSrc = Join-Path $src "RegHelper.c"

function Find-Gcc {
    $paths = @()
    $g = Get-Command gcc -ErrorAction SilentlyContinue
    if ($g) { $paths += $g.Source }
    $paths += @(
        (Join-Path $root "..\..\_toolchain\w64devkit\bin\gcc.exe"),
        (Join-Path $root "..\_toolchain\w64devkit\bin\gcc.exe")
    )
    foreach ($p in $paths) { if ($p -and (Test-Path $p)) { return (Resolve-Path $p).Path } }
    return $null
}

function Find-Gcc32($gccExe) {
    $candidates = @()
    $g = Get-Command i686-w64-mingw32-gcc -ErrorAction SilentlyContinue
    if ($g) { $candidates += $g.Source }
    $dir = Split-Path -Parent $gccExe
    $candidates += @(
        (Join-Path $dir "i686-w64-mingw32-gcc.exe"),
        (Join-Path $root "..\..\_toolchain\w64devkit\bin\i686-w64-mingw32-gcc.exe"),
        (Join-Path $root "..\_toolchain\w64devkit\bin\i686-w64-mingw32-gcc.exe")
    )
    foreach ($p in $candidates) { if ($p -and (Test-Path $p)) { return (Resolve-Path $p).Path } }
    return $null
}

function Find-Zig {
    $z = Get-Command zig -ErrorAction SilentlyContinue
    if ($z) { return @{ exe = $z.Source; prefix = @() } }

    # pip install ziglang 会把 zig 放在 python 的 site-packages\ziglang\zig.exe
    $py = Get-Command python -ErrorAction SilentlyContinue
    if ($py) {
        $path = & python -c "import ziglang, os; print(os.path.join(os.path.dirname(ziglang.__file__), 'zig.exe'))" 2>$null
        if ($path -and (Test-Path $path.Trim())) {
            return @{ exe = $path.Trim(); prefix = @() }
        }
        return @{ exe = $py.Source; prefix = @("-m", "ziglang") }
    }
    return $null
}

function Find-Msvc {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { return $null }
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { return $null }
    $vcvars = Join-Path $vs.Trim() "VC\Auxiliary\Build\vcvars64.bat"
    if (Test-Path $vcvars) { return $vcvars }
    return $null
}

$gcc = Find-Gcc
if ($gcc) {
    Write-Host "使用 gcc 编译：$gcc"
    & $gcc -shared -O2 -DUNICODE -D_UNICODE -static-libgcc -static-libstdc++ `
        -o $ax $cpp $def `
        -lstdc++ -lole32 -loleaut32 -ladvapi32 -luser32 -lkernel32 -luuid
    if ($LASTEXITCODE -ne 0) { throw "gcc 编译失败（退出码 $LASTEXITCODE）" }
    Write-Host "已生成：$ax"

    $gcc32 = Find-Gcc32 $gcc
    if ($gcc32) {
        & $gcc32 -shared -O2 -DUNICODE -D_UNICODE -static-libgcc -static-libstdc++ `
            -o $ax32 $cpp $def `
            -lstdc++ -lole32 -loleaut32 -ladvapi32 -luser32 -lkernel32 -luuid
        if ($LASTEXITCODE -ne 0) { throw "32 位 gcc 编译失败（退出码 $LASTEXITCODE）" }
        & $gcc32 -O2 -municode -o $reg32 $regSrc -lole32 -ladvapi32
        if ($LASTEXITCODE -ne 0) { throw "reg32 编译失败（退出码 $LASTEXITCODE）" }
        Write-Host "已生成：$ax32 / $reg32"
    }
    else {
        Write-Host "未找到 i686-w64-mingw32-gcc，跳过 32 位版本（32 位软件会看不到摄像头）"
    }
    exit 0
}

$zig = Find-Zig
if ($zig) {
    Write-Host "使用 zig 编译：$($zig.exe) $($zig.prefix -join ' ')"
    $args = @()
    $args += $zig.prefix
    $args += @(
        "cc",
        "-target", "x86_64-windows-gnu",
        "-shared",
        "-O2",
        "-DUNICODE", "-D_UNICODE",
        "-o", $ax,
        $cpp, $def,
        "-lole32", "-loleaut32", "-ladvapi32", "-luser32", "-lkernel32"
    )
    & $zig.exe @args
    if ($LASTEXITCODE -ne 0) { throw "zig 编译失败（退出码 $LASTEXITCODE）" }
    Write-Host "已生成：$ax"
    exit 0
}

$vcvars = Find-Msvc
if ($vcvars) {
    Write-Host "使用 MSVC 编译：$vcvars"
    $cmd = "`"$vcvars`" && cl /nologo /LD /O2 /EHsc /DUNICODE /D_UNICODE /D_CRT_SECURE_NO_WARNINGS /Fe`"$ax`" `"$cpp`" /link /DEF:`"$def`" ole32.lib oleaut32.lib advapi32.lib user32.lib"
    & cmd /c $cmd
    if ($LASTEXITCODE -ne 0) { throw "MSVC 编译失败（退出码 $LASTEXITCODE）" }
    Write-Host "已生成：$ax"
    exit 0
}

Write-Error @"
没有找到 C++ 编译器。
请任选一种方式后再运行本脚本：
  1. 下载 w64devkit 解压到 _toolchain\w64devkit： https://github.com/skeeto/w64devkit/releases
  2. 安装 zig： https://ziglang.org/download/   （或 pip install ziglang）
  3. 安装 Visual Studio 2022，勾选「使用 C++ 的桌面开发」
"@
exit 1
