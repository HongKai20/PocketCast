# PocketCast · 手机当电脑的麦克风 + 摄像头

局域网内把 Android 手机变成电脑的**虚拟麦克风**和**虚拟摄像头**：手机扫码连上电脑后，
腾讯会议、微信、钉钉、OBS、QQ 等任何支持选择音视频设备的软件，都能直接选到
`CABLE Input`（麦克风）和 `PocketCast Virtual Camera`（摄像头）。

- 纯局域网 P2P，音视频**不出公网**、不经过任何云中转。
- 手机端**按需采集**：只有打开麦克风/摄像头开关时才真正采集，关闭立即释放硬件。
- 电脑端虚拟设备**常驻**：手机没开时输出静音和纯黑帧，软件里不会报“设备无信号”。

---

## 1. 架构

```
┌────────────── 手机 (Android 10+) ──────────────┐        ┌──────────── 电脑 (Windows 10/11) ────────────┐
│  Camera2 → MediaCodec H.264 硬编 (720p30)      │        │  WebRTC (SIPSorcery) 收流                    │
│  AudioRecord → Opus 48kHz 单声道 10ms 帧       │ ──────▶ │   ├─ H.264 RTP 拆包 → Media Foundation 解码    │
│  WebRTC 发送（DTLS-SRTP 加密）                  │  UDP    │   │      └→ 共享内存 (Local\PocketCast.FrameBuffer)│
│  TCP 信令 8765（一次性 token 校验）              │ ◀────▶ │   │             └→ DirectShow .ax → 会议软件      │
│  扫码 / mDNS 发现                               │  TCP    │   └─ Opus 解码 → PCM → VB-CABLE CABLE Input   │
└────────────────────────────────────────────────┘        │  信令服务 + 二维码 + mDNS + 托盘 UI             │
                                                          └───────────────────────────────────────────────┘
```

关键设计：

| 环节 | 实现 |
| --- | --- |
| 传输 | WebRTC（SIPSorcery 10），DTLS-SRTP 加密，局域网 ICE 直连 |
| 音频编码 | Opus 48kHz 单声道，10ms 帧，码率上限 64kbps（带 FEC） |
| 视频编码 | H.264 硬编（MediaCodec），720p30 / 640x480 / 960x540 三档 |
| 视频解码 | 系统 Media Foundation H.264 解码器（硬件优先），NV12→YUY2 |
| 虚拟麦克风 | 复用 VB-CABLE（或 VoiceMeeter），写 PCM 到 `CABLE Input` |
| 虚拟摄像头 | 自研 DirectShow 源滤镜（用户态 .ax，注册在 HKCU，免管理员） |
| 共享内存 | 128B 头 + 4 个 720p YUY2 槽位，seq 双写校验防撕裂 |
| 配对 | 二维码含 IP/端口/一次性 token；token 用一次即失效并轮换 |

---

## 2. 目录结构

```
PocketCast/
├─ pc/src/
│  ├─ PocketCast.Core/       # 核心：信令、WebRTC、解码、共享内存、VB-CABLE
│  ├─ PocketCast.App/        # WPF 托盘界面（二维码、状态、一键断开）
│  └─ PocketCast.SelfTest/   # 本机管线自测（延迟/吞吐/解码器探测）
├─ android/app/src/main/     # Kotlin 源码（扫码、双开关、WebRTC 推流、延迟自检）
├─ android/build_apk.ps1     # 手工构建 APK（kotlinc + aapt2 + d8 + apksigner，不依赖 Gradle）
├─ vcam/                     # DirectShow 虚拟摄像头（C++ 源码 + 构建/注册脚本）
└─ docs/延迟测试报告.md
```

产物在 `dist/`：

```
dist/
├─ PocketCast-android.apk          # 手机安装包（已签名，arm64 / armeabi-v7a / x86_64）
├─ PocketCast-pc/                  # 电脑端（绿色免安装，双击 PocketCast.exe；内含 vcam\PocketCam.ax）
├─ PocketCast-SelfTest/            # 自测工具（可选）
├─ vcam/                           # 虚拟摄像头：C++ 源码 + build/register/unregister 脚本
├─ docs/延迟测试报告.md
└─ README.md
```

---

## 3. 安装步骤

### 第 1 步：装 VB-CABLE（虚拟声卡，麦克风必需）

电脑端**第一次启动**时如果检测不到 VB-CABLE，会弹窗询问是否自动安装：

- 点「是」：程序自动从 vb-audio.com 下载官方安装包（约 1.1 MB）解压到
  `%LOCALAPPDATA%\PocketCast\vcable`，并以**管理员身份**打开官方安装程序。
- 点「否」：之后随时可以点主界面的「安装 VB-CABLE」按钮重新发起。

在弹出的官方安装程序里完成 3 步：

1. Windows 的 UAC 窗口点「是」（VB-CABLE 是内核驱动，必须授权）。
2. 点 `Install Driver`，等它提示安装完成。
3. **重启电脑**（VB-CABLE 必须重启才生效）。

重启后声音设置里会多出 `CABLE Input`（播放）和 `CABLE Output`（录音）两个设备。

> 想手动装：打开 <https://vb-audio.com/Cable/> 下载 `VBCABLE_Driver_Pack43.zip`，
> 解压后右键 `VBCABLE_Setup_x64.exe` → 以管理员身份运行 → `Install Driver` → 重启。
>
> 已有 VoiceMeeter 也可以，程序会自动寻找名字里包含 `VoiceMeeter Input` 的设备。
>
> 程序每 5 秒检查一次设备：驱动装好后会自动接上音频输出，**不需要重开 PocketCast**
> （但重启电脑仍然是必须的）。

### 第 2 步：虚拟摄像头（自动安装，无需手动操作）

`PocketCam.ax`（64 位）已经随电脑端一起打包在 `dist/PocketCast-pc/vcam/PocketCam.ax`。
**电脑端每次启动都会自动检查并注册**：注册只写 `HKCU\Software\Classes`，
不需要管理员权限、不弹 UAC；日志区和状态区会显示 `虚拟摄像头：已自动安装 / 已就绪`。

- 程序每次启动会比对注册表里的路径与当前 `PocketCam.ax`，路径变了（换版本、换目录）就自动重新注册。
- 若自动注册失败（日志会写明原因，例如杀毒软件拦截），可手动补一次：
  `powershell -ExecutionPolicy Bypass -File register.ps1`（在 `dist\vcam` 目录下）。
- 反注册：`powershell -ExecutionPolicy Bypass -File unregister.ps1`。
- 注册后**已打开过的软件需要重启**才能看到新摄像头。
- 想自己改代码重新编译：`powershell -ExecutionPolicy Bypass -File build.ps1`
  （脚本会自动查找 w64devkit 的 gcc、zig 或 VS2022 的 MSVC）。

### 第 3 步：启动电脑端

双击 `dist/PocketCast-pc/PocketCast.exe`（绿色免安装，自带 .NET 运行时）。

- **首次启动会自动注册虚拟摄像头**，日志区会打印结果；无需再手动跑脚本。
- 首次启动 Windows 会弹「是否允许通过防火墙」→ 勾选**专用网络**并允许（信令端口 8765）。
- 窗口里显示二维码、`IP:8765`、配对码；右下角状态区显示音视频设备是否就绪。
- 关闭窗口会**最小化到托盘**，虚拟设备继续工作；托盘右键可显示窗口 / 断开手机 / 退出。

### 第 4 步：手机装 APK 并连接

1. 把 `PocketCast-android.apk` 传到手机安装（允许“安装未知来源应用”）。
2. 手机和电脑连**同一个 WiFi**（推荐 5GHz）。
3. 打开 App → 点「扫码连接」扫电脑上的二维码；或在局域网里点「刷新发现」（mDNS），
   或长按连接区域手动输入 `IP` 和配对码。

---

## 4. 使用

手机界面：

- **麦克风开关**（蓝色）和**摄像头开关**（绿色）两个大按钮。打开才采集，关闭立刻释放。
- 分辨率：`720p` / `480p` / `自适应（960x540，带宽折中档）`。
- 音量条控制手机媒体音量（提示音、自检蜂鸣用）。
- 状态区显示连接状态和实时延迟（RTT）。

在软件里选设备：

| 软件 | 摄像头 | 麦克风 |
| --- | --- | --- |
| 腾讯会议 | 设置 → 视频 → 摄像头 → `PocketCast Virtual Camera` | 设置 → 音频 → 麦克风 → `CABLE Output` |
| 微信 / QQ | 设置 → 视频通话 → 摄像头 | 设置 → 语音 → 麦克风 |
| 钉钉 | 设置 → 音视频 → 摄像头/麦克风 | 同上 |
| OBS | 来源 → 视频采集设备 → `PocketCast Virtual Camera` | 来源 → 音频输入采集 → `CABLE Output` |

> 麦克风之所以选 **`CABLE Output`**：程序把手机音频写进 `CABLE Input`，系统会把它当作一条
> “虚拟线缆”，`CABLE Output` 就是这条线缆的另一端，会议软件从那里读取即可。

**不需要**在 Windows 声音设置里改默认设备，按需在软件内选择即可。

---

## 5. 延迟测试

### 5.1 一键自检（推荐，App 内置）

1. 先打开手机的**摄像头**和**麦克风**开关，并让电脑端处于运行状态。
2. 手机 App 点「延迟自检」。
3. 手机同时**闪光 + 响 1kHz 蜂鸣**；电脑端检测到亮度跳变和音频能量跳变，
   用 NTP 式四时间戳时钟同步换算出端到端延迟，结果显示在手机和电脑状态栏。

### 5.2 手工验证（拍手对音画）

1. 手机对着电脑屏幕，屏幕上是秒表/计时器。
2. 拍一下手，同时录屏（Windows 自带 Xbox Game Bar：`Win+G`）。
3. 逐帧比对：屏幕数字走到某值和听到拍手声的帧差 → 音频延迟；画面变化帧差 → 视频延迟。

### 5.3 本机管线基准

`dist/PocketCast-SelfTest/PocketCast.SelfTest.exe` 会打印本机真实测量值（共享内存回环、
Opus 编解码耗时与码率、VB-CABLE 与 H.264 解码器可用性）。当前机器的实测数据见
[docs/延迟测试报告.md](docs/延迟测试报告.md)。

---

## 6. 常见问题

**Q1. 软件里找不到 `PocketCast Virtual Camera`**
- 先看电脑端状态区的「虚拟摄像头」是否显示 `已自动安装 / 已就绪`；显示 `安装失败` 时看日志区的具体原因。
- 自动注册没成功：在 `vcam` 目录跑 `register.ps1` 手动补一次。
- 注册后没重启软件：把微信/QQ/浏览器完全退出再打开（软件只在启动时枚举一次设备）。
- 32 位和 64 位软件都支持：程序会同时注册 `PocketCam.ax`（64 位）和 `PocketCam32.ax`（32 位），
  32 位那一半由随附的 `reg32.exe` 代注册。状态区显示 `已就绪（仅 64 位）` 说明 32 位没注册上，
  日志区会给出原因（缺 `PocketCam32.ax` / `reg32.exe`，或注册失败）。
- **能用它的是走 DirectShow 的软件**：微信 / QQ / 腾讯会议 / 钉钉 / OBS / Zoom / Chrome / Edge /
  PotPlayer，以及 ECap / AMCap 这类老采集工具。这些都会列出 `PocketCast Virtual Camera`。
  虚拟摄像头对外提供 `RGB24 / RGB32 / YUY2` 三种像素格式、5 档分辨率（1280x720 / 960x540 /
  640x480 / 352x288 / 320x240），并像真摄像头一样同时给出「采集」和「预览」两个引脚，
  老软件按预览引脚打开时不会报“请检查装置连接状况”。
- **Windows「相机」App 看不到**：它只走 Media Foundation 的设备接口（需要 KS 驱动才能注册），
  本项目是用户态 DirectShow 滤镜，做不到；本机也没有 `mfvirtualcamera.dll`（虚拟摄像头 API）。
  需要在「相机」App 里用的话，可用 OBS 添加本摄像头再把 OBS 虚拟摄像头输出出去（需另装 OBS）。

**Q2. 有摄像头但一直是黑屏**
- 手机端“摄像头”开关没打开（电脑端空闲时输出纯黑帧，属正常）。
- 手机端状态没显示“已连接”：检查 WiFi 是否同一网段、防火墙是否放行。
- 电脑端状态栏提示「找不到 H.264 解码器」：见 Q6。

**Q3. 麦克风没声音**
- 软件里麦克风选成了 `CABLE Input`，应选 **`CABLE Output`**。
- 手机端“麦克风”开关没打开。
- VB-CABLE 没装或没重启；电脑端状态栏会显示「未找到 VB-CABLE」或「已安装，重启电脑后生效」。
  点「安装 VB-CABLE」按钮，按第 1 步装完并重启即可。

**Q4. 连不上 / 提示配对码无效**
- 二维码里的 token 用过一次就失效（防重放）。**配对成功后电脑端会自动换一张新二维码**，
  界面上显示的配对码也会同步更新；如果扫的是旧码就会失败，直接用新的那张即可。
- 电脑有多张网卡（虚拟机/VPN）：二维码里是程序自动挑的局域网 IP，选错了就用手动输入 IP。
- 防火墙拦截：允许 `PocketCast.exe` 访问专用网络（TCP 8765 + UDP）。

**Q5. 画面卡顿 / 延迟高**
- 用 5GHz WiFi；**锁屏已经支持**（连接后自动启动前台服务 + WakeLock/WifiLock，CPU 与 WiFi 不会休眠）。
  状态栏会常驻「PocketCast 正在推流」通知，说明前台服务在运行；没有这条通知就代表服务没起来，
  锁屏后系统会把相机/麦克风按「后台应用」处理并掐断——重装最新 APK 即可（旧包清单里漏了该服务）。
- 小米 / 红米（MIUI、HyperOS）：第一次连接时会请求「不受电池优化限制」，请点允许；再到
  `设置 → 应用管理 → PocketCast → 省电策略` 选 **无限制**、`自启动` 选 **允许**，
  否则部分 ROM 锁屏后仍会清理后台。
- 降到 480p；关闭其他占用 WiFi 的设备。
- 2.4GHz 下 720p 容易拥塞，建议 480p。
- 画面某次卡住不动时，电脑端会自动请求关键帧（RTCP PLI，失败再走信令让手机重启视频编码），
  日志区能看到「已向手机请求关键帧」。若持续卡住，检查 WiFi 丢包。

**Q6. 提示「系统里找不到 H.264 解码器」**
- 说明这台 Windows 缺少 Media Foundation 的 H.264 解码器，常见于 **Windows N / KN 版**。
- 解决：安装「Media Feature Pack」（微软官网搜索 `Media Feature Pack Windows 11 N`），
  装完重启。普通家庭版/专业版默认自带。

**Q7. 手机端 App 打不开摄像头**
- 检查系统权限：相机、麦克风都要允许。
- 有别的 App 占用相机（如微信视频通话中）时，需要先退出那个 App。
- 点「扫码连接」时 App 会自动先停掉正在推流的摄像头、把相机让给扫码界面，扫完再恢复；
  所以不用手动关摄像头开关。若仍提示「相机被占用」，退出占用相机的软件再试。
- 重新扫码时不会再闪退：连接复用同一个 WebRTC 工厂，断开时先停录音线程再释放音频设备。

**Q8. 想在多台电脑/多部手机上用**
- 每台电脑都是独立的一次性 token；手机同一时间只连一台电脑（单连接限制）。

---

## 7. 从源码构建

### 7.1 电脑端

```powershell
dotnet build .\pc\src\PocketCast.App\PocketCast.App.csproj -c Release -p:Platform=x64
dotnet publish .\pc\src\PocketCast.App\PocketCast.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o .\dist\PocketCast-pc
```

### 7.2 虚拟摄像头

日常使用不需要手动注册：电脑端启动时会自动完成（见第 2 步）。开发时重建/手动注册：

```powershell
cd vcam
powershell -ExecutionPolicy Bypass -File build.ps1     # w64devkit gcc / zig / MSVC 自动查找
powershell -ExecutionPolicy Bypass -File register.ps1
```

### 7.3 手机端 APK

`android/build_apk.ps1` 是不依赖 Gradle 的手工构建脚本（kotlinc + aapt2 + d8 + zipalign + apksigner）。
需要：JDK 17、Android SDK（platform 34 / build-tools 34.0.0）、Kotlin 编译器、
`webrtc-sdk:android:125.6422.07` 的 `classes.jar` 与 `jni/*.so`、`zxing` jar。

```powershell
cd android
powershell -ExecutionPolicy Bypass -File build_apk.ps1
```

---

## 8. 已知限制

- 只做 Android 10+，**没有 iOS 端**（iOS 无法后台长时采集，且需要开发者签名）。
- 虚拟摄像头是 **DirectShow** 滤镜；走 Media Foundation 的软件看不到它。
- 虚拟麦克风依赖 **VB-CABLE / VoiceMeeter**（不自研内核驱动，与需求一致）。
  VB-CABLE 是内核驱动：程序能自动下载并拉起官方安装程序，但 UAC 授权和重启电脑必须由用户完成。
- `dist/vcam/PocketCam.ax` 已随包提供；如需重新编译，见第 7 节（支持 w64devkit gcc / zig / MSVC）。
- “自适应”分辨率是 960x540 折中档位，不是按带宽动态切换。
- 本机（构建环境）没有 VB-CABLE、也没有 H.264 解码器（Windows N 版），
  因此自测报告中这两项为“未就绪/不可用”，属环境限制，不影响代码逻辑。
