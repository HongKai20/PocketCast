using System.Text.Json;
using System.Text.Json.Nodes;

namespace PocketCast.Core.Protocol;

/// <summary>
/// 信令协议：手机与电脑之间走一条明文 TCP（NDJSON，一行一条 JSON）。
/// 音视频本身走 WebRTC（DTLS-SRTP），信令只承载 SDP / ICE / 控制与延迟测试。
/// </summary>
public sealed class Signal
{
    public JsonObject Root { get; }

    private Signal(JsonObject root) => Root = root;

    public string Type => Str("t") ?? "";

    public string? Str(string key)
    {
        var n = Root[key];
        if (n is null) return null;
        if (n is JsonValue v) return v.TryGetValue<string>(out var s) ? s : v.ToJsonString();
        return n.ToJsonString();
    }

    public int? Int(string key)
    {
        var n = Root[key];
        if (n is JsonValue v && v.TryGetValue<int>(out var i)) return i;
        if (n is JsonValue v2 && v2.TryGetValue<double>(out var d)) return (int)d;
        return null;
    }

    public long? Long(string key)
    {
        var n = Root[key];
        if (n is JsonValue v && v.TryGetValue<long>(out var i)) return i;
        if (n is JsonValue v2 && v2.TryGetValue<double>(out var d)) return (long)d;
        return null;
    }

    public double? Double(string key)
    {
        var n = Root[key];
        if (n is JsonValue v && v.TryGetValue<double>(out var d)) return d;
        return null;
    }

    public bool? Bool(string key)
    {
        var n = Root[key];
        if (n is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return null;
    }

    public static Signal Parse(string line)
    {
        var node = JsonNode.Parse(line);
        if (node is not JsonObject obj) throw new FormatException("信令必须是 JSON 对象");
        return new Signal(obj);
    }

    public static string Build(string type, params (string Key, object? Value)[] fields)
    {
        var obj = new JsonObject { ["t"] = type };
        foreach (var (k, v) in fields)
        {
            obj[k] = v switch
            {
                null => null,
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                JsonNode n => n,
                _ => JsonValue.Create(v.ToString())
            };
        }
        return obj.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}

public static class SignalType
{
    public const string Hello = "hello";
    public const string Welcome = "welcome";
    public const string Reject = "reject";
    public const string Offer = "offer";
    public const string Answer = "answer";
    public const string Candidate = "candidate";
    public const string Control = "control";
    public const string Ack = "ack";
    public const string Clock = "clock";
    public const string ClockAck = "clock_ack";
    public const string ClockResult = "clock_result";
    public const string Probe = "probe";
    public const string ProbeResult = "probe_result";
    public const string Stats = "stats";
    /// <summary>电脑端发现画面停滞，要求手机重启视频轨（强制产生新的 IDR）。</summary>
    public const string Keyframe = "keyframe";
    public const string Bye = "bye";
}
