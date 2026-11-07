using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using QRCoder;

namespace PocketCast.Core.Pairing;

public static class LanInfo
{
    /// <summary>选一块"像局域网"的 IPv4 网卡：优先 Wi-Fi/以太网、有默认网关、非 169.254。</summary>
    public static IPAddress? PrimaryIPv4()
    {
        var candidates = new List<(int score, IPAddress addr)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = ni.GetIPProperties();
            if (props.GatewayAddresses.Count == 0) continue;
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.Address.ToString().StartsWith("169.254.")) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                int score = 0;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 30;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 20;
                if (ua.Address.ToString().StartsWith("192.168.")) score += 5;
                candidates.Add((score, ua.Address));
            }
        }
        return candidates.OrderByDescending(c => c.score).Select(c => c.addr).FirstOrDefault();
    }

    public static IReadOnlyList<IPAddress> AllIPv4()
    {
        var list = new List<IPAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    list.Add(ua.Address);
            }
        }
        return list;
    }

    /// <summary>二维码内容：紧凑 JSON，手机扫码后直接拿到 IP/端口/一次性 token。</summary>
    public static string QrPayload(string pcName, IPAddress ip, int port, string token)
        => $"{{\"v\":1,\"host\":\"{ip}\",\"port\":{port},\"token\":\"{token}\",\"name\":\"{pcName}\"}}";
}

public static class QrCodeFactory
{
    public static byte[] Png(string payload, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }
}
