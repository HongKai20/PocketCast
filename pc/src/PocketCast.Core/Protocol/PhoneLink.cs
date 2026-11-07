using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PocketCast.Core.Protocol;

/// <summary>
/// 手机与电脑之间的一条 TCP 信令连接（NDJSON）。PC 端为服务端，端口默认 8765。
/// 音视频不经过这里，这里只做配对、SDP/ICE 交换、控制指令与延迟测试。
/// </summary>
public sealed class PhoneLink : IDisposable
{
    private readonly TcpClient _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly BlockingCollection<string> _outbox = new();
    private readonly Thread _writeThread;
    private volatile bool _closed;

    public event Action<PhoneLink>? Closed;

    public string RemoteAddress { get; }
    public string DeviceName { get; set; } = "未知手机";
    public bool Authenticated { get; set; }
    public long ConnectedAtMs { get; } = Video.Clock.MsNow;
    public bool AudioActive { get; set; }
    public bool VideoActive { get; set; }
    public int RequestedHeight { get; set; } = 720;
    public double PhoneFps { get; set; }
    public double PhoneKbps { get; set; }

    public PhoneLink(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _client.ReceiveBufferSize = 64 * 1024;
        RemoteAddress = client.Client.RemoteEndPoint is IPEndPoint ep ? ep.Address.ToString() : "?";
        var stream = client.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
        _writeThread = new Thread(WriteLoop) { IsBackground = true, Name = "phone-write" };
        _writeThread.Start();
    }

    public void Send(string json)
    {
        if (_closed) return;
        _outbox.Add(json);
    }

    public void Send(string type, params (string, object?)[] fields) => Send(Signal.Build(type, fields));

    private void WriteLoop()
    {
        try
        {
            foreach (var line in _outbox.GetConsumingEnumerable())
            {
                _writer.WriteLine(line);
                _writer.Flush();
            }
        }
        catch
        {
            Close();
        }
    }

    /// <summary>阻塞读取行；连接断开返回 null。</summary>
    public string? ReadLine()
    {
        try { return _reader.ReadLine(); }
        catch { return null; }
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try { _outbox.CompleteAdding(); } catch { }
        try { _client.Close(); } catch { }
        Closed?.Invoke(this);
    }

    public void Dispose()
    {
        Close();
        try { _reader.Dispose(); } catch { }
        try { _writer.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
    }
}

/// <summary>监听手机连接并做一次性 token 校验。</summary>
public sealed class SignalingServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Thread _acceptThread;
    private volatile bool _stopped;
    private readonly object _sync = new();
    private PhoneLink? _current;

    public int Port { get; }
    public string Token { get; private set; }
    public PhoneLink? Current => _current;

    public event Action<PhoneLink, Signal>? MessageReceived;
    public event Action<PhoneLink>? PhoneConnected;
    public event Action? PhoneDisconnected;

    public SignalingServer(int port = 8765)
    {
        Port = port;
        Token = NewToken();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "signaling-accept" };
        _acceptThread.Start();
    }

    public static string NewToken()
    {
        // 6 位数字，扫码一次即失效
        return Random.Shared.Next(100000, 999999).ToString();
    }

    /// <summary>刷新一次性 token（已连接时保留旧 token 不影响现有会话）。</summary>
    public string RotateToken()
    {
        lock (_sync) { Token = NewToken(); return Token; }
    }

    private void AcceptLoop()
    {
        while (!_stopped)
        {
            try
            {
                var client = _listener.AcceptTcpClient();
                if (_current != null)
                {
                    // 只允许一台手机
                    using var reject = client;
                    reject.NoDelay = true;
                    var s = reject.GetStream();
                    var w = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true };
                    w.WriteLine(Signal.Build(SignalType.Reject, ("reason", "已有手机连接")));
                    continue;
                }
                var link = new PhoneLink(client);
                _current = link;
                link.Closed += OnLinkClosed;
                var t = new Thread(() => ServeLink(link)) { IsBackground = true, Name = "signaling-session" };
                t.Start();
            }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Console.WriteLine($"[Signaling] {ex.Message}"); }
        }
    }

    private void ServeLink(PhoneLink link)
    {
        try
        {
            string? line = link.ReadLine();
            if (line is null) { link.Close(); return; }
            var hello = Signal.Parse(line);
            if (hello.Type != SignalType.Hello)
            {
                link.Send(SignalType.Reject, ("reason", "首包必须是 hello"));
                link.Close();
                return;
            }
            lock (_sync)
            {
                if (hello.Str("token") != Token)
                {
                    link.Send(SignalType.Reject, ("reason", "配对码错误或已失效"));
                    link.Close();
                    return;
                }
                Token = NewToken();
            }
            link.Authenticated = true;
            link.DeviceName = hello.Str("device") ?? "Android 手机";
            link.Send(SignalType.Welcome, ("name", Environment.MachineName), ("session", Guid.NewGuid().ToString("N")[..8]));
            PhoneConnected?.Invoke(link);

            while (!_stopped)
            {
                line = link.ReadLine();
                if (line is null) break;
                if (line.Length == 0) continue;
                try
                {
                    var msg = Signal.Parse(line);
                    MessageReceived?.Invoke(link, msg);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Signaling] 解析失败 {ex.Message}: {line[..Math.Min(120, line.Length)]}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Signaling] 会话异常 {ex.Message}");
        }
        finally
        {
            link.Close();
        }
    }

    private void OnLinkClosed(PhoneLink link)
    {
        if (_current == link)
        {
            _current = null;
            PhoneDisconnected?.Invoke();
        }
    }

    public void Dispose()
    {
        _stopped = true;
        try { _listener.Stop(); } catch { }
        _current?.Dispose();
        _current = null;
    }
}
