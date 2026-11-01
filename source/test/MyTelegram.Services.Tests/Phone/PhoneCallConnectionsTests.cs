using System.Security.Cryptography;
using System.Text;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// phoneCall.connections, shaped like Telegram's: one entry per server with both its turn and stun flags, and
/// TURN credentials issued per participant and call in the REST form coturn verifies with --use-auth-secret.
/// </summary>
public class PhoneCallConnectionsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_793_523_000);

    private static WebRtcConnection Server(string? secret) => new()
    {
        Ip = "203.0.113.5",
        Ipv6 = "2001:db8::5",
        Port = 3478,
        Turn = true,
        Stun = true,
        UserName = "static-user",
        Password = "static-pass",
        AuthSecret = secret
    };

    [Fact]
    public void EachServer_IsOneEntryWithBothFlags()
    {
        var connection = PhoneCallConnections.Build([Server("s3cret")], 1, 77, Now)
            .ShouldHaveSingleItem().ShouldBeOfType<TPhoneConnectionWebrtc>();

        connection.Turn.ShouldBeTrue();
        connection.Stun.ShouldBeTrue();
        connection.Ip.ShouldBe("203.0.113.5");
        connection.Ipv6.ShouldBe("2001:db8::5");
        connection.Port.ShouldBe(3478);
    }

    [Fact]
    public void WithASharedSecret_CredentialsAreRestStyle_AndDifferPerParticipant()
    {
        var caller = PhoneCallConnections.Build([Server("s3cret")], 1, 77, Now).Single().ShouldBeOfType<TPhoneConnectionWebrtc>();
        var callee = PhoneCallConnections.Build([Server("s3cret")], 2, 77, Now).Single().ShouldBeOfType<TPhoneConnectionWebrtc>();

        caller.Username.ShouldNotBe(callee.Username);
        var parts = caller.Username.Split(':');
        parts.Length.ShouldBe(2);
        long.Parse(parts[0]).ShouldBe(Now.ToUnixTimeSeconds() + 6 * 60 * 60);
        parts[1].Length.ShouldBe(18);

        // This is exactly what coturn recomputes from its --static-auth-secret.
        var expected = Convert.ToBase64String(
            HMACSHA1.HashData(Encoding.UTF8.GetBytes("s3cret"), Encoding.UTF8.GetBytes(caller.Username)));
        caller.Password.ShouldBe(expected);
        caller.Password.ShouldNotContain("static");
    }

    [Fact]
    public void WithoutASecret_TheConfiguredStaticCredentialsAreUsed()
    {
        var connection = PhoneCallConnections.Build([Server(null)], 1, 77, Now).Single().ShouldBeOfType<TPhoneConnectionWebrtc>();

        connection.Username.ShouldBe("static-user");
        connection.Password.ShouldBe("static-pass");
    }
}
