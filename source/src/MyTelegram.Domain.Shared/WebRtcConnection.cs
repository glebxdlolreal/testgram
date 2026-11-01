namespace MyTelegram;

public record WebRtcConnection
{
    public string Ip { get; set; } = default!;
    public string Ipv6 { get; set; } = default!;
    public int Port { get; set; }
    public bool Turn { get; set; }
    public bool Stun { get; set; }
    public string UserName { get; set; } = default!;
    public string Password { get; set; } = default!;

    /// <summary>
    /// The TURN server's shared secret (coturn <c>--static-auth-secret</c> with <c>--use-auth-secret</c>). When
    /// set, every call participant gets their own short-lived credentials instead of
    /// <see cref="UserName"/>/<see cref="Password"/>, as Telegram's servers hand out.
    /// </summary>
    public string? AuthSecret { get; set; }

    /// <summary>How long issued TURN credentials stay valid; Telegram's are about six hours.</summary>
    public int CredentialTtlSeconds { get; set; } = 6 * 60 * 60;
}