using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PocketCast.App;

/// <summary>
/// 首次启动时自动注册 DirectShow 虚拟摄像头（PocketCam.ax / PocketCam32.ax）。
/// 注册只写 HKCU\Software\Classes，不需要管理员权限，也不会弹 UAC。
/// 32 位软件读的是 WOW6432Node 视图，所以还要注册一份 32 位滤镜；64 位进程载入不了
/// 32 位 DLL，这一半交给随附的 reg32.exe。
/// </summary>
internal static class VirtualCameraInstaller
{
    private const string Clsid = "{A7B0C1D2-3E4F-4A5B-9C8D-1E2F3A4B5C6D}";
    // DirectShow 视频输入设备类别
    private const string VideoInputCategory = "{860BB310-5D01-11D0-BD3B-00A0C911CE86}";

    private const string ClsidKey = @"Software\Classes\CLSID\" + Clsid;
    private const string InprocKey = ClsidKey + @"\InprocServer32";
    private const string CategoryKey = @"Software\Classes\CLSID\" + VideoInputCategory + @"\Instance\" + Clsid;

    internal sealed record Result(bool Ready, string StatusText, string LogText);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int HResultFn();

    public static Result Ensure()
    {
        var ax = FindAx();
        if (ax == null)
        {
            return new Result(false, "未安装",
                "虚拟摄像头驱动未随程序提供：找不到 PocketCam.ax（应位于程序目录下的 vcam\\PocketCam.ax）。");
        }

        var axDir = Path.GetDirectoryName(ax)!;
        var ax32 = Path.Combine(axDir, "PocketCam32.ax");
        var reg32 = Path.Combine(axDir, "reg32.exe");
        var has32 = File.Exists(ax32);

        var log = new System.Text.StringBuilder();

        // ---------- 64 位滤镜 ----------
        if (!Ready(RegistryView.Registry64, ax))
        {
            var failed = Register64(ax);
            if (failed != null) return failed;
            if (!IsRegistered(RegistryView.Registry64))
            {
                return new Result(false, "安装失败",
                    "注册调用返回成功，但注册表里没有写入设备项。可改用 vcam\\register.ps1 手动注册。");
            }
        }
        log.Append($"64 位滤镜：{ax}");

        // ---------- 32 位滤镜 ----------
        string? note32 = null;
        var ok32 = false;
        if (!has32)
        {
            note32 = "未附带 PocketCam32.ax，32 位软件（ecap 这类老工具、32 位播放器）看不到摄像头。";
        }
        else if (!File.Exists(reg32))
        {
            note32 = "缺少 reg32.exe，无法注册 32 位滤镜（32 位软件看不到摄像头）。";
        }
        else
        {
            if (!Ready(RegistryView.Registry32, ax32))
            {
                var (exitCode, output) = RunReg32(reg32, ax32);
                if (exitCode != 0)
                    note32 = $"32 位滤镜注册失败（退出码 {exitCode}）：{output.Trim()}";
            }
            ok32 = IsRegistered(RegistryView.Registry32);
            if (ok32) log.Append($"\n32 位滤镜：{ax32}");
            else if (note32 == null) note32 = "32 位滤镜注册后注册表里没有设备项。";
        }

        if (note32 != null) log.Append("\n注意：").Append(note32);
        log.Append("\n已打开的微信 / 腾讯会议 / OBS / ecap 需要重启才能看到 PocketCast Virtual Camera。");

        return new Result(true, ok32 || !has32 ? "已就绪" : "已就绪（仅 64 位）", log.ToString());
    }

    /// <summary>在程序目录及其上层目录里找 PocketCam.ax。</summary>
    private static string? FindAx()
    {
        string[] relative =
        {
            @"vcam\PocketCam.ax",
            "PocketCam.ax",
            @"vcam\build\PocketCam.ax",
            @"..\vcam\PocketCam.ax",
            @"..\vcam\build\PocketCam.ax"
        };

        string? dir = AppContext.BaseDirectory;
        for (int depth = 0; depth < 6 && dir != null; depth++)
        {
            foreach (var rel in relative)
            {
                var candidate = Path.GetFullPath(Path.Combine(dir, rel));
                if (File.Exists(candidate)) return candidate;
            }
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    /// <summary>注册 64 位滤镜（直接载入 DLL 调 DllRegisterServer）。失败时返回失败结果。</summary>
    private static Result? Register64(string ax)
    {
        try
        {
            var h = NativeLibrary.Load(ax);
            var p = NativeLibrary.GetExport(h, "DllRegisterServer");
            var fn = Marshal.GetDelegateForFunctionPointer<HResultFn>(p);
            var hr = fn();
            if (hr != 0)
            {
                return new Result(false, "安装失败",
                    $"虚拟摄像头注册失败，HRESULT = 0x{hr:X8}（{ax}）。可改用 vcam\\register.ps1 手动注册。");
            }
        }
        catch (MissingMethodException)
        {
            return new Result(false, "安装失败",
                $"{ax} 不是有效的 DirectShow 滤镜（缺少 DllRegisterServer 导出）。");
        }
        catch (Exception ex)
        {
            return new Result(false, "安装失败",
                $"虚拟摄像头安装失败：{ex.Message}（{ax}）。可改用 vcam\\register.ps1 手动注册。");
        }
        return null;
    }

    private static (int ExitCode, string Output) RunReg32(string reg32, string ax32)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(reg32, $"\"{ax32}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static RegistryKey BaseKey(RegistryView view) =>
        RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);

    private static string? ReadRegisteredPath(RegistryView view)
    {
        using var root = BaseKey(view);
        using var key = root.OpenSubKey(InprocKey);
        return key?.GetValue(null) as string;
    }

    private static bool Ready(RegistryView view, string ax) =>
        SamePath(ReadRegisteredPath(view) ?? string.Empty, ax) && RegistrationIntact(view);

    private static bool IsRegistered(RegistryView view)
    {
        using var root = BaseKey(view);
        using var inproc = root.OpenSubKey(InprocKey);
        using var category = root.OpenSubKey(CategoryKey);
        return inproc?.GetValue(null) is string s && s.Length > 0 && category != null;
    }

    /// <summary>
    /// 设备项是否完整可用。老版本把 FilterData 写成了 REG_SZ，设备枚举器会读不到名称，
    /// 软件里就“找不到相机”；这种历史注册必须重写，不能因为路径没变就跳过。
    /// </summary>
    private static bool RegistrationIntact(RegistryView view)
    {
        using var root = BaseKey(view);
        using var category = root.OpenSubKey(CategoryKey);
        if (category == null) return false;
        if (category.GetValue("FriendlyName") is not string name || name.Length == 0) return false;
        if (category.GetValue("CLSID") is not string clsid || clsid != Clsid) return false;
        if (category.GetValue("FilterData") is not byte[] data || data.Length == 0 || data[0] != 0x02) return false;
        return true;
    }

    private static bool SamePath(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
