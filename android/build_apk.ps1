# PocketCast Android APK 手工构建脚本（不依赖 Gradle）
#
# 工具链（与本仓库 _toolchain 目录一致）：
#   JDK 17、Android SDK（platforms/android-34 + build-tools/34.0.0）、
#   Kotlin 编译器（kotlin-compiler.jar）、webrtc-sdk 的 classes.jar 与 jni/*.so、zxing.jar
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File build_apk.ps1
#   powershell -ExecutionPolicy Bypass -File build_apk.ps1 -Toolchain "D:\_toolchain" -Out "..\..\dist\PocketCast-android.apk"
#
# 产物：build\PocketCast-android.apk（已签名、已 zipalign），并复制到 -Out 指定位置。

param(
    [string]$Toolchain = "",
    [string]$Out = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $Toolchain) {
    $candidates = @(
        (Join-Path (Split-Path -Parent (Split-Path -Parent $root)) "_toolchain"),
        (Join-Path $root "_toolchain")
    )
    foreach ($c in $candidates) { if (Test-Path $c) { $Toolchain = $c; break } }
}
if (-not (Test-Path $Toolchain)) { throw "找不到工具链目录：$Toolchain（用 -Toolchain 指定）" }

# ---------- 路径 ----------
$sdk        = Join-Path $Toolchain "android-sdk"
$m2         = Join-Path $Toolchain "m2"
$buildTools = Join-Path $sdk "build-tools\34.0.0"
$androidJar = Join-Path $sdk "platforms\android-34\android.jar"
$webrtcJar  = Join-Path $m2 "webrtc\classes.jar"
$webrtcJni  = Join-Path $m2 "webrtc\jni"
$zxingJar   = Join-Path $m2 "zxing.jar"
$annotJar   = Join-Path $m2 "annotations.jar"

$jdk = Get-ChildItem (Join-Path $Toolchain "jdk") -Directory -ErrorAction SilentlyContinue |
       Where-Object { Test-Path (Join-Path $_.FullName "bin\java.exe") } | Select-Object -First 1
if (-not $jdk) { throw "找不到 JDK：$Toolchain\jdk" }
$java = Join-Path $jdk.FullName "bin\java.exe"
$env:JAVA_HOME = $jdk.FullName
$env:Path = "$($jdk.FullName)\bin;$env:Path"

foreach ($p in @($buildTools, $androidJar, $webrtcJar, $zxingJar)) {
    if (-not (Test-Path $p)) { throw "缺少：$p" }
}
if (-not (Test-Path $webrtcJni)) { throw "缺少 WebRTC 原生库目录：$webrtcJni" }

$src        = Join-Path $root "app\src\main"
$manifest   = Join-Path $src "AndroidManifest.xml"
$javaSrcDir = Join-Path $src "java"
$keystore   = (Join-Path $root "pocketcast.keystore")
if (-not (Test-Path $keystore)) { throw "缺少签名文件：$keystore" }

$build     = Join-Path $root "build"
$classes   = Join-Path $build "classes"
$classesJar= Join-Path $build "classes.jar"
$dexDir    = Join-Path $build "dex"
$base      = Join-Path $build "base.apk"
$unsigned  = Join-Path $build "PocketCast-unsigned.apk"
$aligned   = Join-Path $build "PocketCast-aligned.apk"
$final     = Join-Path $build "PocketCast-android.apk"

Remove-Item -Recurse -Force $build -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $classes, $dexDir | Out-Null

function Step($name) { Write-Host "==> $name" }

# ---------- 1. Kotlin 编译 ----------
Step "编译 Kotlin（$java）"
$ktFiles = Get-ChildItem $javaSrcDir -Recurse -Filter *.kt | ForEach-Object { $_.FullName }
if (-not $ktFiles) { throw "没有找到 Kotlin 源文件：$javaSrcDir" }

$compilerCp = @(
    "kotlin-compiler.jar", "kotlin-stdlib.jar", "kotlin-reflect.jar",
    "kotlin-script-runtime.jar", "kotlin-daemon.jar", "trove4j.jar", "coroutines.jar", "annotations.jar"
) | ForEach-Object { Join-Path $m2 $_ } | Where-Object { Test-Path $_ }
$compilerCp = $compilerCp -join ";"

$compileCp = @($androidJar, (Join-Path $m2 "kotlin-stdlib.jar"), $webrtcJar, $zxingJar, $annotJar) | Where-Object { Test-Path $_ }
$compileCp = $compileCp -join ";"

& $java -Xmx1g -cp $compilerCp org.jetbrains.kotlin.cli.jvm.K2JVMCompiler `
    -no-stdlib -no-reflect -jvm-target 11 `
    -classpath $compileCp `
    -d $classes $ktFiles
if ($LASTEXITCODE -ne 0) { throw "Kotlin 编译失败（退出码 $LASTEXITCODE）" }

# ---------- 2. 打 jar 并 dex ----------
Step "d8 生成 classes.dex"
$jarExe = Join-Path $jdk.FullName "bin\jar.exe"
& $jarExe cf $classesJar -C $classes .
if ($LASTEXITCODE -ne 0) { throw "打包 classes.jar 失败" }

$d8 = Join-Path $buildTools "d8.bat"
$dexInputs = @($classesJar, (Join-Path $m2 "kotlin-stdlib.jar"), $webrtcJar, $zxingJar) | Where-Object { Test-Path $_ }
$quoted = ($dexInputs | ForEach-Object { '"' + $_ + '"' }) -join ' '
$d8Cmd = '"' + $d8 + '" --release --min-api 29 --lib "' + $androidJar + '" --output "' + $dexDir + '" ' + $quoted
& cmd /c $d8Cmd
if ($LASTEXITCODE -ne 0) { throw "d8 失败（退出码 $LASTEXITCODE）" }

# ---------- 3. aapt2 link ----------
Step "aapt2 link（生成资源与清单）"
$logo = Join-Path $src "res"
$aapt2Args = @(
    "link", "-o", $base,
    "-I", $androidJar,
    "--manifest", $manifest,
    "--min-sdk-version", "29",
    "--target-sdk-version", "34",
    "--version-code", "1",
    "--version-name", "1.0"
)
if (Test-Path $logo) { $aapt2Args += @("-R", (Get-ChildItem $logo -Recurse -File | ForEach-Object { $_.FullName })) }
& (Join-Path $buildTools "aapt2.exe") @aapt2Args
if ($LASTEXITCODE -ne 0) { throw "aapt2 link 失败（退出码 $LASTEXITCODE）" }

# ---------- 4. 把 dex 和 .so 塞进 APK ----------
Step "合并 classes.dex 与原生库"
Copy-Item $base $unsigned -Force
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($unsigned, [IO.Compression.ZipArchiveMode]::Update)
try {
    # classes.dex
    $dex = Join-Path $dexDir "classes.dex"
    if (-not (Test-Path $dex)) { throw "d8 没有产出 classes.dex" }
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $zip, $dex, "classes.dex", [IO.Compression.CompressionLevel]::Optimal) | Out-Null

    # lib/<abi>/libjingle_peerconnection_so.so —— 不压缩，方便系统直接 mmap 加载
    foreach ($abi in @("arm64-v8a", "armeabi-v7a", "x86_64")) {
        $so = Join-Path $webrtcJni "$abi\libjingle_peerconnection_so.so"
        if (-not (Test-Path $so)) { continue }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $so, "lib/$abi/libjingle_peerconnection_so.so",
            [IO.Compression.CompressionLevel]::NoCompression) | Out-Null
    }
} finally { $zip.Dispose() }

# ---------- 5. zipalign + 签名 ----------
Step "zipalign"
& (Join-Path $buildTools "zipalign.exe") -f -p 4 $unsigned $aligned
if ($LASTEXITCODE -ne 0) { throw "zipalign 失败（退出码 $LASTEXITCODE）" }

Step "apksigner 签名"
$signer = Join-Path $buildTools "apksigner.bat"
& cmd /c "`"$signer`" sign --ks `"$keystore`" --ks-key-alias pocketcast --ks-pass pass:pocketcast --key-pass pass:pocketcast --out `"$final`" `"$aligned`""
if ($LASTEXITCODE -ne 0) { throw "apksigner 失败（退出码 $LASTEXITCODE）" }

& cmd /c "`"$signer`" verify --print-certs `"$final`"" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "签名校验未通过" }

$size = (Get-Item $final).Length / 1MB
Write-Host ("完成：{0}（{1:N1} MB）" -f $final, $size)

# ---------- 6. 复制到 dist ----------
if ($Out) {
    $dir = Split-Path -Parent $Out
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Copy-Item $final $Out -Force
    Write-Host "已复制到：$Out"
}
