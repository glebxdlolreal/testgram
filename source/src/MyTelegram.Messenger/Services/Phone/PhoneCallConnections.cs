using System.Security.Cryptography;
using System.Text;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Phone;

/// <summary>
/// The relay/STUN/TURN servers (<c>phoneCall.connections</c>) a call participant is told about.
/// </summary>
/// <remarks>
/// Shaped like Telegram's: one <c>phoneConnectionWebrtc</c> per server carrying both its <c>turn</c> and
/// <c>stun</c> flags, with credentials issued to this participant for this call. With a shared secret
/// configured they follow the TURN REST scheme coturn verifies with <c>--use-auth-secret</c>:
/// <c>username = "{expiry}:{tag}"</c>, <c>password = base64(HMAC-SHA1(secret, username))</c>. Telegram's
/// usernames look exactly like that (<c>1793544505:40449f32e6898f216b</c>), and differ per participant.
/// </remarks>
public static class PhoneCallConnections
{
    public static TVector<IPhoneConnection> Build(
        IReadOnlyList<WebRtcConnection>? servers,
        long userId,
        long callId,
        DateTimeOffset now)
    {
        var connections = new TVector<IPhoneConnection>();
        if (servers == null)
        {
            return connections;
        }

        for (var i = 0; i < servers.Count; i++)
        {
            var server = servers[i];
            if (!server.Turn && !server.Stun)
            {
                continue;
            }

            var (username, password) = server.Turn
                ? IssueCredentials(server, userId, callId, now)
                : (string.Empty, string.Empty);
            connections.Add(new TPhoneConnectionWebrtc
            {
                Id = i + 1,
                Ip = server.Ip,
                Ipv6 = server.Ipv6 ?? string.Empty,
                Port = server.Port,
                Turn = server.Turn,
                Stun = server.Stun,
                Username = username,
                Password = password
            });
        }

        return connections;
    }

    public static (string Username, string Password) IssueCredentials(
        WebRtcConnection server,
        long userId,
        long callId,
        DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(server.AuthSecret))
        {
            return (server.UserName ?? string.Empty, server.Password ?? string.Empty);
        }

        var expiry = now.ToUnixTimeSeconds() + Math.Max(60, server.CredentialTtlSeconds);
        var tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{userId}:{callId}")))[..18]
            .ToLowerInvariant();
        var username = $"{expiry}:{tag}";
        var password = Convert.ToBase64String(
            HMACSHA1.HashData(Encoding.UTF8.GetBytes(server.AuthSecret), Encoding.UTF8.GetBytes(username)));
        return (username, password);
    }
}
