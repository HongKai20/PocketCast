using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Microsoft.Win32;

namespace PocketCast.App;

/// <summary>
/// 引导安装 VB-CABLE（虚拟声卡）。
/// 它是内核驱动，必须管理员权限 + 重启才能生效，不能静默安装；
/// 这里只做「下载官方安装包（原样不动）→ 解压 → 以管理员身份打开官方安装程序」。
/// </summary>
internal static class VirtualCableInstaller
{
    private const string DownloadUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip";
    private const string ZipName = "VBCABLE_Driver_Pack43.zip";
    private const string SetupName = "VBCABLE_Setup_x64.exe";

    internal enum Outcome { Started, Cancelled, Failed }

    internal sealed record Result(Outcome Outcome, string Message);

    public static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketCast", "vcable");

    /// <summary>驱动是否已安装（服务已注册）。安装后通常要重启才出现 CABLE Input / Output。</summary>
    public static bool IsDriverInstalled()
    {
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services == null) return false;
            return services.GetSubKeyNames()
                .Any(n => n.StartsWith("VBAudio", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>下载并打开官方安装程序。耗时较长，请在后台线程调用。</summary>
    public static Result Install(Action<string> log)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var zip = Path.Combine(CacheDir, ZipName);
            if (!File.Exists(zip) || new FileInfo(zip).Length < 900_000)
            {
                log("正在从 vb-audio.com 下载官方安装包（约 1.1 MB）…");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                using var resp = http.GetAsync(DownloadUrl).GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
                using var fs = File.Create(zip);
                resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
            }

            var exeDir = Path.Combine(CacheDir, "setup");
            if (Directory.Exists(exeDir)) Directory.Delete(exeDir, true);
            ZipFile.ExtractToDirectory(zip, exeDir);

            var exe = Path.Combine(exeDir, SetupName);
            if (!File.Exists(exe))
                return new Result(Outcome.Failed, $"安装包不完整：缺少 {SetupName}，可从 https://vb-audio.com/Cable/ 手动下载。");

            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",              // 触发 UAC，由用户点「是」
                WorkingDirectory = exeDir
            });

            return new Result(Outcome.Started,
                "已打开 VB-CABLE 官方安装程序：请点「Install Driver」，装完后重启电脑。");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new Result(Outcome.Cancelled, "已取消：安装虚拟声卡需要管理员权限（UAC 未授权）。");
        }
        catch (Exception ex)
        {
            return new Result(Outcome.Failed, $"准备 VB-CABLE 安装程序失败：{ex.Message}");
        }
    }
}
