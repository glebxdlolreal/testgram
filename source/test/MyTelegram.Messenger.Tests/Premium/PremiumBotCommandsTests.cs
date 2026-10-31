using EventFlow;
using EventFlow.Queries;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Options;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Payments;
using MyTelegram.Messenger.Services.Premium;
using MyTelegram.Schema;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Domain.Aggregates.Messaging;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;

namespace MyTelegram.Messenger.Tests.Premium;

/// <summary>
/// @PremiumBot's commands, against the answers real Telegram's bot gave on 2026-10-31: the status
/// and recurring-payment flows, gifts, Stars and giveaways, and the invoice links they end in.
/// </summary>
public class PremiumBotCommandsTests
{
    private const long UserId = 2010001;
    private const long FriendId = 2010002;
    private const long ChannelId = 3997606516;

    [RequiresMongoDbFact]
    public async Task Without_premium_stop_and_resume_say_there_is_nothing_to_change()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var sent = new List<SendMessageInput>();
        var bot = PremiumSubscriptionTests.Bot(mongo.Database, sent, blocked: false, Queries(premium: false).Object);

        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "/stop");
        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "/resume");

        sent.Count.ShouldBe(2);
        sent[0].Message.ShouldBe("Recurring payments are already disabled for this account.");
        sent[1].Message.ShouldStartWith("The current subscription can't be resumed. Send the /start command");
        sent.ShouldAllBe(m => m.ReplyMarkup is TReplyKeyboardHide);
    }

    [RequiresMongoDbFact]
    public async Task Start_with_premium_shows_the_next_payment_and_how_to_stop_it()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var sent = new List<SendMessageInput>();
        var expiresAt = new DateTimeOffset(2027, 1, 10, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var bot = PremiumSubscriptionTests.Bot(mongo.Database, sent, blocked: false, Queries(premium: true).Object,
            AutoRenew(enabled: true).Object, Expiring(expiresAt).Object);

        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "/start");

        var status = sent.ShouldHaveSingleItem();
        status.Media.ShouldBeNull();
        status.Message.ShouldContain("Next payment: $19.90, 10.01.2027.");
        status.Message.ShouldContain("/stop");
        CommandsIn(status).ShouldBe(["/stop", "/upgrade", "/gift"]);
    }

    [RequiresMongoDbFact]
    public async Task Stop_asks_for_confirmation_and_a_reason_before_turning_renewal_off()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var sent = new List<SendMessageInput>();
        var autoRenew = AutoRenew(enabled: true);
        var bot = PremiumSubscriptionTests.Bot(mongo.Database, sent, blocked: false, Queries(premium: true).Object, autoRenew.Object,
            Expiring(DateTimeOffset.UtcNow.AddDays(10).ToUnixTimeSeconds()).Object);

        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "/stop");
        sent[^1].ReplyMarkup.ShouldBeOfType<TReplyKeyboardMarkup>();
        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "Yes, cancel renewal");
        ButtonTexts(sent[^1]).ShouldContain("Too expensive");
        autoRenew.Verify(a => a.SetEnabledAsync(It.IsAny<long>(), false, It.IsAny<string?>()), Times.Never);

        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "Too expensive");

        autoRenew.Verify(a => a.SetEnabledAsync(UserId, false, "Too expensive"), Times.Once);
        sent[^1].Message.ShouldStartWith("Recurring payments have been stopped.");
        sent[^1].ReplyMarkup.ShouldBeOfType<TReplyKeyboardHide>();

        // The reason keyboard is gone with the question: the same text again is not understood.
        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "Too expensive");
        sent[^1].Message.ShouldStartWith("Sorry, I didn't understand");
    }

    [RequiresMongoDbFact]
    public async Task Gift_offers_terms_as_links_only_the_gifter_can_pay()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var sent = new List<SendMessageInput>();
        var bot = PremiumSubscriptionTests.Bot(mongo.Database, sent, blocked: false, Queries(premium: false).Object);

        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "/gift");
        var ask = sent.ShouldHaveSingleItem();
        var button = ask.ReplyMarkup.ShouldBeOfType<TReplyKeyboardMarkup>().Rows[0].Buttons[0].ShouldBeOfType<TKeyboardButtonRequestPeer>();
        button.ButtonId.ShouldBe(1);
        button.MaxQuantity.ShouldBe(10);
        button.PeerType.ShouldBeOfType<TRequestPeerTypeUser>().Bot.ShouldBe(false);

        await bot.HandleRequestedPeerAsync(PremiumSubscriptionTests.Input(), 1, [new Peer(PeerType.User, FriendId)]);

        sent[1].Message.ShouldBe("🎁");
        var offer = sent[2];
        offer.Message.ShouldStartWith("Thank you, the contact has been received. Now you can choose a gift subscription for Friend:");
        var links = offer.ReplyMarkup.ShouldBeOfType<TReplyInlineMarkup>().Rows.Select(r => r.Buttons[0].ShouldBeOfType<TKeyboardButtonUrl>().Url).ToList();
        links.Count.ShouldBe(3);
        links.ShouldAllBe(url => url.StartsWith("https://t.me/$premcodes"));

        var slug = links[0]["https://t.me/$".Length..];
        var invoice = (await BotInvoiceHelper.FindBySlugAsync(mongo.Database, slug)).ShouldNotBeNull();
        invoice.Title.ShouldBe("Premium Gifts");
        invoice.ToPeerId.ShouldBe(UserId);
        System.Text.Encoding.UTF8.GetString(invoice.Payload).ShouldBe($"premium-gift:12:{FriendId}");

        var stranger = new Mock<IRequestInput>();
        stranger.SetupGet(i => i.UserId).Returns(FriendId);
        (await Should.ThrowAsync<RpcException>(() => bot.BuildPaymentFormAsync(stranger.Object, invoice)))
            .RpcError.Message.ShouldBe("INVOICE_INVALID");
    }

    [RequiresMongoDbFact]
    public async Task Stars_lists_the_balance_and_buying_answers_with_top_up_links()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var sent = new List<SendMessageInput>();
        var bot = PremiumSubscriptionTests.Bot(mongo.Database, sent, blocked: false, Queries(premium: false).Object);

        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "/stars");
        sent[0].Message.ShouldStartWith("Your current balance: 0 ⭐️");
        ButtonTexts(sent[0]).ShouldBe(["Buy Telegram Stars", "Gift Telegram Stars", "Cancel"]);

        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "Buy Telegram Stars");

        sent[1].Message.ShouldBe("⭐️");
        var links = sent[2].ReplyMarkup.ShouldBeOfType<TReplyInlineMarkup>().Rows.SelectMany(r => r.Buttons)
            .Select(b => b.ShouldBeOfType<TKeyboardButtonUrl>()).ToList();
        links.Count.ShouldBe(StarsCatalog.TopupOffers.Length);
        links[0].Url.ShouldStartWith("https://t.me/$stars100_");

        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "/stars");
        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "Cancel");
        sent[^1].Message.ShouldBe("The stars command has been cancelled.");
    }

    [RequiresMongoDbFact]
    public async Task Giveaway_needs_a_channel_the_user_administers_and_then_edits_the_choice_in_place()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var sent = new List<SendMessageInput>();
        var bus = new Mock<ICommandBus>();
        var bot = PremiumSubscriptionTests.Bot(mongo.Database, sent, blocked: false, Queries(premium: false, channelAdmin: false).Object,
            commandBus: bus.Object);

        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "/giveaway");
        var buttons = sent[0].ReplyMarkup.ShouldBeOfType<TReplyKeyboardMarkup>().Rows.Select(r => r.Buttons[0].ShouldBeOfType<TKeyboardButtonRequestPeer>()).ToList();
        buttons.Select(b => b.ButtonId).ShouldBe([2, 3]);

        await bot.HandleRequestedPeerAsync(PremiumSubscriptionTests.Input(), 2, [new Peer(PeerType.Channel, ChannelId)]);
        sent[^1].Message.ShouldStartWith("No chat selected.");

        bot = PremiumSubscriptionTests.Bot(mongo.Database, sent, blocked: false, Queries(premium: false, channelAdmin: true).Object,
            commandBus: bus.Object);
        await bot.HandleRequestedPeerAsync(PremiumSubscriptionTests.Input(), 2, [new Peer(PeerType.Channel, ChannelId)]);

        sent[^2].Message.ShouldBe("⚡️");
        var terms = sent[^1];
        terms.Message.ShouldStartWith("OK! Please select the duration of the Telegram Premium subscriptions you want to give away in Test:");
        terms.MessageId.ShouldNotBeNull();
        var data = terms.ReplyMarkup.ShouldBeOfType<TReplyInlineMarkup>().Rows[0].Buttons[0].ShouldBeOfType<TKeyboardButtonCallback>().Data;
        data.ShouldBe($"give/{ChannelId}/12");

        await bot.HandleCallbackAsync(PremiumSubscriptionTests.Input(), data);

        bus.Verify(b => b.PublishAsync(It.Is<EditOutboxMessageCommand>(c => c.NewMessage.StartsWith("Perfect!")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [RequiresMongoDbFact]
    public async Task A_lapsed_term_switches_renewal_off_instead_of_charging()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var options = new MyTelegramMessengerServerOptions();
        options.Stripe.SecretKey = "sk_test_unused";
        var service = new PremiumAutoRenewService(mongo.Database, Microsoft.Extensions.Options.Options.Create(options),
            Expiring(null).Object, PremiumPricingAndLimitTests.Gate(false).Object, NullLogger<PremiumAutoRenewService>.Instance);
        await service.SaveCardAsync(UserId, 1, 499, "USD", "cus_1", "pm_1");

        (await service.RenewDueAsync()).ShouldBeEmpty();

        var renewal = (await service.GetAsync(UserId)).ShouldNotBeNull();
        renewal.Enabled.ShouldBeFalse();
        renewal.StopReason.ShouldBe("expired");
        (await service.SetEnabledAsync(FriendId, true)).ShouldBeFalse();
    }

    [RequiresMongoDbFact]
    public async Task Renewal_outcomes_are_told_the_way_real_telegram_tells_them()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var sent = new List<SendMessageInput>();
        var bot = PremiumSubscriptionTests.Bot(mongo.Database, sent, blocked: false, Queries(premium: true).Object);
        var expiresAt = new DateTimeOffset(2025, 6, 7, 0, 19, 14, TimeSpan.Zero).ToUnixTimeSeconds();

        await bot.NotifyRenewalAsync(new PremiumRenewalOutcome(UserId, true, 1, 1990, "USD", expiresAt));
        await bot.NotifyRenewalAsync(new PremiumRenewalOutcome(UserId, false, 1, 1990, "USD", expiresAt, WillRetry: true));

        sent[0].Message.ShouldBe("We received the payment for $19.90.\nYour premium subscription is now renewed until 7 Jun 2025 00:19:14 UTC.\n\nTo 🎁 gift Telegram Premium to a friend, use the /gift command.");
        sent[1].Message.ShouldStartWith("Automatic payment failed. We will try again in several hours.");
    }

    [RequiresMongoDbFact]
    public async Task Blocked_accounts_get_the_same_answers_with_nothing_to_press()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var sent = new List<SendMessageInput>();
        var bot = PremiumSubscriptionTests.Bot(mongo.Database, sent, blocked: true, Queries(premium: false).Object, starsBlocked: true);

        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "/gift");
        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "/stars");
        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "/giveaway");
        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "/stars_giveaway");
        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "/upgrade");

        sent.Select(m => m.Message[..12]).ShouldBe(["To gift 🎁 a", "Your current", "To 💰 pay fo", "To 💰 pay fo", "Pay the invo"]);
        sent.ShouldAllBe(m => m.Media == null && m.ReplyMarkup is TReplyKeyboardHide);

        // Nothing is waiting for an answer: a reply-keyboard text is not understood.
        await bot.HandleMessageAsync(PremiumSubscriptionTests.Input(), "Buy Telegram Stars");
        sent[^1].Message.ShouldStartWith("Sorry, I didn't understand");
    }

    [Fact]
    public void A_settled_payment_crosses_to_the_command_server_intact()
    {
        var serializer = new PremiumBotPaymentSettledEventDataSerializer();
        var settled = new PremiumBotPaymentSettledEvent(UserId, 42, 222, "bot-invoice-slug-premcodes_ab", "pi_1", "cus_1", string.Empty);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();

        serializer.Serialize(buffer, settled);

        serializer.Deserialize(buffer.WrittenMemory).ShouldBe(settled);
    }

    [Fact]
    public void A_chosen_chat_crosses_to_the_command_server_intact()
    {
        var serializer = new MyTelegram.Messenger.Services.Bots.BotRequestedPeerEventDataSerializer();
        var chosen = new MyTelegram.Messenger.Services.Bots.BotRequestedPeerEvent(UserId, 42, 222, PremiumBotService.BotUserId, 77, 2,
            [new Peer(PeerType.Channel, ChannelId), new Peer(PeerType.User, FriendId)]);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();

        serializer.Serialize(buffer, chosen);
        var read = serializer.Deserialize(buffer.WrittenMemory);

        (read with { Peers = chosen.Peers }).ShouldBe(chosen);
        read.Peers.ShouldBe(chosen.Peers);
    }

    [Fact]
    public void The_welcome_notification_matches_real_telegram()
    {
        PremiumWelcomeNotifier.Text.ShouldBe("Thank you for subscribing to 🌟 Telegram Premium! Learn all about the exclusive benefits you've unlocked: @premium.");
        PremiumWelcomeNotifier.Text.IndexOf("@premium", StringComparison.Ordinal).ShouldBe(106);
    }

    private static Mock<IQueryProcessor> Queries(bool premium, bool channelAdmin = true)
    {
        var queries = new Mock<IQueryProcessor>();
        queries.Setup(q => q.ProcessAsync(It.IsAny<GetUserByIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GetUserByIdQuery query, CancellationToken _) =>
            {
                var user = new Mock<IUserReadModel>();
                user.SetupGet(u => u.UserId).Returns(query.UserId);
                user.SetupGet(u => u.Premium).Returns(query.UserId == UserId && premium);
                user.SetupGet(u => u.FirstName).Returns(query.UserId == UserId ? "Me" : "Friend");
                return user.Object;
            });
        var channel = new Mock<IChannelReadModel>();
        channel.SetupGet(c => c.ChannelId).Returns(ChannelId);
        channel.SetupGet(c => c.Title).Returns("Test");
        channel.SetupGet(c => c.Broadcast).Returns(true);
        channel.SetupGet(c => c.CreatorId).Returns(channelAdmin ? UserId : 1);
        channel.SetupGet(c => c.AdminList).Returns([]);
        queries.Setup(q => q.ProcessAsync(It.IsAny<GetChannelByIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);
        queries.Setup(q => q.ProcessAsync(It.IsAny<GetDeviceByUserIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IDeviceReadModel>());
        return queries;
    }

    private static Mock<IPremiumAutoRenewService> AutoRenew(bool enabled)
    {
        var autoRenew = new Mock<IPremiumAutoRenewService>();
        autoRenew.Setup(a => a.GetAsync(It.IsAny<long>())).ReturnsAsync(new PremiumAutoRenewalDocument
        {
            UserId = UserId, Enabled = enabled, Months = 1, Amount = 1990, Currency = "USD",
            StripeCustomerId = "cus_1", StripePaymentMethodId = "pm_1"
        });
        autoRenew.Setup(a => a.SetEnabledAsync(It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<string?>())).ReturnsAsync(true);
        return autoRenew;
    }

    private static Mock<IPremiumSubscriptionService> Expiring(long? expiresAt)
    {
        var subscriptions = new Mock<IPremiumSubscriptionService>();
        subscriptions.Setup(s => s.GetExpiresAtAsync(It.IsAny<long>())).ReturnsAsync(expiresAt);
        return subscriptions;
    }

    private static List<string> CommandsIn(SendMessageInput message) =>
        message.Entities!.OfType<TMessageEntityBotCommand>().Select(e => message.Message.Substring(e.Offset, e.Length)).ToList();

    private static List<string> ButtonTexts(SendMessageInput message) =>
        message.ReplyMarkup.ShouldBeOfType<TReplyKeyboardMarkup>().Rows.SelectMany(r => r.Buttons).Select(b => b.Text).ToList();
}
