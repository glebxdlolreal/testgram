namespace MyTelegram.Messenger;

/// <summary>
/// A UDP reflector for 1:1 calls (source/reflector), listed to clients as <c>phoneConnection</c>:
/// <c>App__CallReflectors__0__Ip</c>, <c>__Ipv6</c>, <c>__Port</c>.
/// </summary>
public class CallReflectorConfig
{
    public string? Ip { get; set; }

    public string? Ipv6 { get; set; }

    public int Port { get; set; } = 599;

    /// <summary>
    /// The connection id. Clients number their reflectors by sorting these ids, and both sides of a call
    /// must agree on the numbering, so it has to stay the same for every call.
    /// </summary>
    public long Id { get; set; }
}
