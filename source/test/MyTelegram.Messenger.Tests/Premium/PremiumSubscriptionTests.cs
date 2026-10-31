using System.Text;
using EventFlow;
using EventFlow.Queries;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Domain.Aggregates.User;
using MyTelegram.Messenger.Options;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Payments;
using MyTelegram.Messenger.Services.Premium;
using MyTelegram.Messenger.Services.WebFiles;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;
using MyTelegram.Messenger.Converters.ConverterServices;

namespace MyTelegram.Messenger.Tests.Premium;

/// <summary>
/// The Premium term: purchases extend it, expiry ends it, and the flag only ever changes through the
/// user aggregate. Also the per-account purchase block and @PremiumBot's answers.
/// See https://corefork.telegram.org/api/premium
/// </summary>
public class PremiumSubscriptionTests
{
    private const long UserId = 2010001;

    [RequiresMongoDbFact]
    public async Task A_purchase_extends_the_current_term_instead_of_replacing_it()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var bus = new Mock<ICommandBus>();
        var service = Subscriptions(mongo.Database, bus, premium: false);

        var first = await service.GrantAsync(UserId, 30, "test");
        var second = await service.GrantAsync(UserId, 30, "test");

        (second.ExpiresAt - first.ExpiresAt).ShouldBe(30L * 86400);
        bus.Verify(b => b.PublishAsync(It.Is<UpdateUserPremiumStatusCommand>(c => c.Premium), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        (await mongo.Database.GetCollection<BsonDocument>("eventflow-userreadmodel").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty))
            .ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task A_lapsed_term_restarts_from_now()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await mongo.Database.GetCollection<BsonDocument>(PremiumSubscriptionService.CollectionName).InsertOneAsync(
            new BsonDocument
            {
                ["_id"] = PremiumSubscriptionService.DocumentId(UserId), ["UserId"] = UserId,
                ["ExpiresAt"] = now - 86400, ["Active"] = false
            });

        var result = await Subscriptions(mongo.Database, new Mock<ICommandBus>(), premium: false).GrantAsync(UserId, 30, "test");

        result.ExpiresAt.ShouldBeInRange(now + 30 * 86400 - 5, now + 30 * 86400 + 5);
    }

    [RequiresMongoDbFact]
    public async Task Expiry_turns_premium_off_through_the_aggregate()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var collection = mongo.Database.GetCollection<BsonDocument>(PremiumSubscriptionService.CollectionName);
        await collection.InsertManyAsync([
            new BsonDocument { ["_id"] = "premium-1", ["UserId"] = 1L, ["ExpiresAt"] = now - 10, ["Active"] = true },
            new BsonDocument { ["_id"] = "premium-2", ["UserId"] = 2L, ["ExpiresAt"] = now + 1000, ["Active"] = true }
        ]);
        var bus = new Mock<ICommandBus>();

        var revoked = await Subscriptions(mongo.Database, bus, premium: true).RevokeExpiredAsync();

        revoked.ShouldBe(1);
        bus.Verify(b => b.PublishAsync(It.Is<UpdateUserPremiumStatusCommand>(c => !c.Premium), It.IsAny<CancellationToken>()),
            Times.Once);
        (await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", "premium-1")).FirstAsync())["Active"].AsBoolean.ShouldBeFalse();
        (await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", "premium-2")).FirstAsync())["Active"].AsBoolean.ShouldBeTrue();
    }

    [RequiresMongoDbFact]
    public async Task Legacy_terms_join_expiry()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<BsonDocument>(PremiumSubscriptionService.CollectionName);
        await collection.InsertManyAsync([
            // The Stars gift path keyed terms by a random id, with no Active flag.
            new BsonDocument { ["UserId"] = 5L, ["Months"] = 1, ["ActivatedAt"] = 100, ["ExpiresAt"] = 200, ["Currency"] = "XTR" },
            new BsonDocument { ["_id"] = "premium-6", ["UserId"] = 6L, ["ExpiresAt"] = 300 }
        ]);

        await Subscriptions(mongo.Database, new Mock<ICommandBus>(), premium: true).NormalizeLegacyTermsAsync();

        var all = await collection.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        all.Select(d => d["_id"].AsString).OrderBy(id => id).ShouldBe(["premium-5", "premium-6"]);
        all.ShouldAllBe(d => d["Active"].AsBoolean);
    }

    [RequiresMongoDbFact]
    public async Task A_hand_written_block_document_blocks_the_account()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var blocks = mongo.Database.GetCollection<BsonDocument>(PremiumPurchaseGate.CollectionName);
        await blocks.InsertManyAsync([
            new BsonDocument { ["_id"] = 101 },
            new BsonDocument { ["_id"] = 102L, ["Blocked"] = false },
            new BsonDocument { ["_id"] = 103L, ["Blocked"] = true }
        ]);
        var gate = new PremiumPurchaseGate(mongo.Database);

        (await gate.IsBlockedAsync(101)).ShouldBeTrue();
        (await gate.IsBlockedAsync(102)).ShouldBeFalse();
        (await gate.IsBlockedAsync(103)).ShouldBeTrue();
        (await gate.IsBlockedAsync(104)).ShouldBeFalse();
        (await Should.ThrowAsync<RpcException>(() => gate.EnsureAllowedAsync(104, 103)))
            .RpcError.Message.ShouldBe("PREMIUM_CURRENTLY_UNAVAILABLE");
    }

    /// <summary>The bot_url links real Telegram hands out: PremiumBot?start=1, =12 and =24.</summary>
    [RequiresMongoDbTheory]
    [InlineData("/start 1", 1, 499, "Telegram Premium")]
    [InlineData("/start 12", 12, 3999, "Premium Annual Subscription")]
    [InlineData("/start 24", 24, 6999, "Premium for 2 years")]
    public async Task PremiumBot_sells_the_requested_term(string command, int months, long amount, string title)
    {
        using var mongo = EmbeddedMongoServer.Start();
        var sent = new List<SendMessageInput>();
        var bot = Bot(mongo.Database, sent, blocked: false);

        await bot.HandleMessageAsync(Input(), command);

        sent.Count.ShouldBe(2);
        sent[0].Media.ShouldBeOfType<TMessageMediaInvoice>();
        sent[0].ReplyMarkup.ShouldBeOfType<TReplyInlineMarkup>().Rows[0].Buttons[0].ShouldBeOfType<TKeyboardButtonBuy>();
        sent[1].Message.ShouldStartWith("Pay the invoice above");
        var stored = await mongo.Database.GetCollection<BotInvoiceDocument>(BotInvoiceHelper.CollectionName)
            .Find(d => d.MsgId == 77).FirstAsync();
        Encoding.UTF8.GetString(stored.Payload).ShouldBe($"premium:{months}");
        stored.TotalAmount.ShouldBe(amount);
        stored.Currency.ShouldBe("USD");
        stored.BotId.ShouldBe(MyTelegramConsts.PremiumBotUserId);
        stored.Title.ShouldBe(title);
        stored.StartParam.ShouldBe(months.ToString());
        BotInvoiceHelper.ReadPhoto(stored).ShouldBeOfType<TWebDocument>().AccessHash.ShouldBe(42);
    }

    /// <summary>
    /// Real Telegram's note under each invoice: a switch to two years under the monthly one, to monthly
    /// under the yearly one, nothing under the two-year one.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task PremiumBot_offers_to_switch_terms_like_real_Telegram()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var sent = new List<SendMessageInput>();
        var bot = Bot(mongo.Database, sent, blocked: false);

        await bot.HandleMessageAsync(Input(), "/start");
        await bot.HandleMessageAsync(Input(), "/start 12");
        await bot.HandleMessageAsync(Input(), "/start 24");

        var monthly = sent[1].ReplyMarkup.ShouldBeOfType<TReplyInlineMarkup>().Rows[0].Buttons[0].ShouldBeOfType<TKeyboardButtonCallback>();
        monthly.Text.ShouldBe("Switch to 2-year (-41% per month)");
        monthly.Data.ShouldStartWith("init/24M/");
        var yearly = sent[3].ReplyMarkup.ShouldBeOfType<TReplyInlineMarkup>().Rows[0].Buttons[0].ShouldBeOfType<TKeyboardButtonCallback>();
        yearly.Text.ShouldBe("Switch to monthly for 4.99\u00a0USD per month");
        yearly.Data.ShouldStartWith("init/1M/");
        sent[5].ReplyMarkup.ShouldBeOfType<TReplyKeyboardHide>();
        sent[1].Entities.ShouldHaveSingleItem().ShouldBeOfType<TMessageEntityBold>().Length.ShouldBe("Pay the invoice above".Length);
    }

    [RequiresMongoDbFact]
    public async Task PremiumBot_sends_the_term_a_switch_button_names()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var sent = new List<SendMessageInput>();

        await Bot(mongo.Database, sent, blocked: false).HandleCallbackAsync(Input(), "init/24M/118135547");

        sent[0].Media.ShouldBeOfType<TMessageMediaInvoice>();
        var stored = await mongo.Database.GetCollection<BotInvoiceDocument>(BotInvoiceHelper.CollectionName)
            .Find(d => d.MsgId == 77).FirstAsync();
        Encoding.UTF8.GetString(stored.Payload).ShouldBe("premium:24");
    }

    /// <summary>Real Telegram answers /start 3 and /start status with the monthly invoice.</summary>
    [RequiresMongoDbTheory]
    [InlineData("/start double_limits__channels")]
    [InlineData("/start 3")]
    [InlineData("/start")]
    public async Task PremiumBot_defaults_to_a_month_for_any_other_start_parameter(string command)
    {
        using var mongo = EmbeddedMongoServer.Start();
        var sent = new List<SendMessageInput>();

        await Bot(mongo.Database, sent, blocked: false).HandleMessageAsync(Input(), command);

        sent.Count.ShouldBe(2);
        var stored = await mongo.Database.GetCollection<BotInvoiceDocument>(BotInvoiceHelper.CollectionName)
            .Find(d => d.MsgId == 77).FirstAsync();
        Encoding.UTF8.GetString(stored.Payload).ShouldBe("premium:1");
    }

    [RequiresMongoDbFact]
    public async Task PremiumBot_does_not_sell_to_a_blocked_account()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var sent = new List<SendMessageInput>();
        var bot = Bot(mongo.Database, sent, blocked: true);

        await bot.HandleMessageAsync(Input(), "/start");
        await bot.HandleMessageAsync(Input(), "/start 12");
        await bot.HandleCallbackAsync(Input(), "init/24M/1");

        sent.Count.ShouldBe(3);
        sent.ShouldAllBe(m => m.Media == null && m.Message == "Sorry, I didn't understand your request. Send the /start command");
        (await mongo.Database.GetCollection<BotInvoiceDocument>(BotInvoiceHelper.CollectionName)
            .CountDocumentsAsync(FilterDefinition<BotInvoiceDocument>.Empty)).ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task PremiumBot_does_not_understand_anything_but_start()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var sent = new List<SendMessageInput>();
        var bot = Bot(mongo.Database, sent, blocked: false);

        await bot.HandleMessageAsync(Input(), "hello");
        await bot.HandleMessageAsync(Input(), "/status");

        sent.Count.ShouldBe(2);
        sent.ShouldAllBe(m => m.Media == null && m.Message.StartsWith("Sorry, I didn't understand") && m.ReplyMarkup is TReplyKeyboardHide);
        var command = sent[0].Entities.ShouldHaveSingleItem().ShouldBeOfType<TMessageEntityBotCommand>();
        sent[0].Message.Substring(command.Offset, command.Length).ShouldBe("/start");
    }

    private static PremiumSubscriptionService Subscriptions(IMongoDatabase database, Mock<ICommandBus> bus, bool premium) =>
        new(database, bus.Object, PremiumPricingAndLimitTests.Users(premium).Object, NullLogger<PremiumSubscriptionService>.Instance);

    private static PremiumBotService Bot(IMongoDatabase database, List<SendMessageInput> sent, bool blocked)
    {
        var messages = new Mock<IMessageAppService>();
        messages.Setup(m => m.SendMessageAsync(It.IsAny<List<SendMessageInput>>()))
            .Callback<List<SendMessageInput>>(sent.AddRange)
            .Returns(Task.CompletedTask);
        var media = new Mock<IMediaHelper>();
        media.Setup(m => m.SaveMediaAsync(It.IsAny<IInputMedia?>())).ReturnsAsync(new TMessageMediaInvoice
        {
            Title = "Telegram Premium", Description = string.Empty, Currency = "USD", TotalAmount = 1, StartParam = string.Empty
        });
        // 77 for each invoice, 78 for the note after it.
        var next = 77;
        var ids = new Mock<IIdGenerator>();
        ids.Setup(i => i.NextIdAsync(It.IsAny<IdType>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => next++ % 2 == 1 ? 77 : 78);
        var registrar = new Mock<IWebFileRegistrar>();
        registrar.Setup(r => r.EnsureRegisteredAsync(It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<TVector<IDocumentAttribute>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var proxy = new Mock<IWebDocumentProxy>();
        proxy.Setup(p => p.Sign(It.IsAny<string>())).Returns(42);

        return new PremiumBotService(database, messages.Object, media.Object, ids.Object, new Mock<IQueryProcessor>().Object,
            new Mock<IUserConverterService>().Object, Microsoft.Extensions.Options.Options.Create(new PremiumOptions()),
            PremiumPricingAndLimitTests.Gate(blocked).Object, new Mock<IPremiumSubscriptionService>().Object,
            new Mock<IPremiumCardCheckout>().Object, new Mock<IBotInvoicePaymentService>().Object,
            registrar.Object, proxy.Object, NullLogger<PremiumBotService>.Instance);
    }

    private static IRequestInput Input()
    {
        var input = new Mock<IRequestInput>();
        input.SetupGet(i => i.UserId).Returns(UserId);
        return input.Object;
    }
}
