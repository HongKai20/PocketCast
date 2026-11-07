using Makaretu.Dns;

namespace PocketCast.Core.Pairing;

/// <summary>
/// 用 mDNS 广播 _pocketcast._tcp，同一 WiFi 下的安卓 App 打开就能看到电脑。
/// </summary>
public sealed class MdnsPublisher : IDisposable
{
    private readonly MulticastService _mdns;
    private readonly ServiceDiscovery _discovery;
    private readonly ServiceProfile _profile;
    private bool _disposed;

    public string InstanceName { get; }
    public bool Running { get; private set; }

    public MdnsPublisher(string instanceName, int port, IEnumerable<System.Net.IPAddress> addresses)
    {
        InstanceName = instanceName;
        _mdns = new MulticastService();
        _discovery = new ServiceDiscovery(_mdns);
        _profile = new ServiceProfile(instanceName, "_pocketcast._tcp", (ushort)port, addresses);
        _profile.AddProperty("name", instanceName);
        _profile.AddProperty("ver", "1");
        _profile.AddProperty("ctrl", "tcp-ndjson");
    }

    public void Start()
    {
        if (Running) return;
        _mdns.Start();
        _discovery.Advertise(_profile);
        Running = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _discovery.Unadvertise(_profile); } catch { }
        try { _mdns.Stop(); } catch { }
        Running = false;
    }
}
