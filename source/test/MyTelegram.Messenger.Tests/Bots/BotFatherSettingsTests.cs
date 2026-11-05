using Moq;
using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Bots;

/// <summary>
/// Feature: the @BotFather settings that act outside BotFather — the Login Widget signature behind a
/// linked domain, the domain itself, description GIF sizes and the default admin rights document.
/// </summary>
public class BotFatherSettingsTests
{
    [Fact]
    public void The_login_signature_is_the_Login_Widget_hmac_keyed_with_the_bot_token()
    {
        var fields = new Dictionary<string, string>
        {
            ["id"] = "2010001", ["first_name"] = "Gleb", ["username"] = "glebx", ["auth_date"] = "1700000000"
        };

        // hmac-sha256(sorted "key=value" lines, sha256(token)), computed independently with Python.
        UrlAuthService.Sign(fields, "600000000123:AAAbbbCCC")
            .ShouldBe("bbf8a633e18b46ff21f75e6ebf2629a4282aad2d94617f5482ca22acdf2f7187");
    }

    [Theory]
    [InlineData("https://example.com/login", "example.com", true)]
    [InlineData("https://auth.example.com/login?x=1", "example.com", true)]
    [InlineData("https://example.com.evil.org/login", "example.com", false)]
    [InlineData("https://notexample.com/", "example.com", false)]
    [InlineData("javascript:alert(1)", "example.com", false)]
    public void A_button_logs_in_only_on_the_linked_domain(string url, string domain, bool expected)
    {
        UrlAuthService.DomainMatches(url, domain).ShouldBe(expected);
    }

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("https://Example.COM/path", "example.com")]
    [InlineData("sub.example.co.uk", "sub.example.co.uk")]
    [InlineData("localhost", null)]
    [InlineData("not a domain", null)]
    [InlineData("1.2.3.4", null)]
    public void A_domain_is_taken_from_what_the_owner_sends(string text, string? expected)
    {
        BotFatherBotService.NormalizeDomain(text).ShouldBe(expected);
    }

    [Theory]
    [InlineData(320, 180, true)]
    [InlineData(640, 360, true)]
    [InlineData(960, 540, true)]
    [InlineData(640, 480, false)]
    public void A_description_gif_must_have_one_of_the_three_sizes(int width, int height, bool expected)
    {
        var media = new TMessageMediaDocument
        {
            Document = new TDocument
            {
                Attributes = new TVector<IDocumentAttribute>(
                    new TDocumentAttributeAnimated(), new TDocumentAttributeVideo { W = width, H = height })
            }
        };

        BotAppMediaValidator.IsValidDescriptionAnimation(media).ShouldBe(expected);
    }

    [Fact]
    public void A_plain_video_is_not_a_description_gif()
    {
        var media = new TMessageMediaDocument
        {
            Document = new TDocument
            {
                Attributes = new TVector<IDocumentAttribute>(new TDocumentAttributeVideo { W = 640, H = 360 })
            }
        };

        BotAppMediaValidator.IsValidDescriptionAnimation(media).ShouldBeFalse();
    }

    [Fact]
    public void Default_admin_rights_survive_the_round_trip_including_direct_messages()
    {
        var rights = new TChatAdminRights { ChangeInfo = true, ManageDirectMessages = true, Other = true };

        var restored = BotDefaultAdminRights.FromDocument(BotDefaultAdminRights.ToDocument(rights));

        restored.ChangeInfo.ShouldBeTrue();
        restored.ManageDirectMessages.ShouldBeTrue();
        restored.Other.ShouldBeTrue();
        restored.BanUsers.ShouldBeFalse();
    }

    [Fact]
    public void Every_right_BotFather_offers_is_stored()
    {
        BotDefaultAdminRights.GroupChoices.Concat(BotDefaultAdminRights.BroadcastChoices)
            .ShouldAllBe(p => BotDefaultAdminRights.IsKnownField(p.Field));
    }
}
