using System.Reflection;
using EventFlow.Queries;
using Moq;
using MyTelegram.Messenger.Options;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Premium;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Premium;

/// <summary>
/// Telegram Premium terms, prices and the limits Premium doubles.
/// See https://corefork.telegram.org/api/premium
/// </summary>
public class PremiumPricingAndLimitTests
{
    private const long UserId = 2010001;

    /// <summary>
    /// tdlib turns messageActionGiftPremium.days back into months with exactly this formula
    /// (get_premium_duration_day_count), so every term must round-trip.
    /// </summary>
    [Theory]
    [InlineData(1, 30)]
    [InlineData(3, 91)]
    [InlineData(6, 182)]
    [InlineData(12, 365)]
    [InlineData(24, 730)]
    public void Days_match_tdlibs_month_conversion(int months, int days)
    {
        PremiumDuration.DaysFromMonths(months).ShouldBe(days);
    }

    [Fact]
    public void Default_terms_mirror_real_telegram()
    {
        var options = new PremiumOptions();

        options.GetPeriods().Where(p => p.Subscription).Select(p => p.Months).ShouldBe([1, 12, 24]);
        options.GetPeriods().Where(p => p.Gift).Select(p => p.Months).ShouldBe([3, 6, 12]);
        options.FindGift(3)!.AmountStars.ShouldBe(1000);
        options.FindGift(6)!.AmountStars.ShouldBe(1500);
        options.FindGift(12)!.AmountStars.ShouldBe(2500);
        options.FindSubscription(3).ShouldBeNull();
        options.FindGift(1).ShouldBeNull();
    }

    [Fact]
    public void Configured_terms_replace_the_defaults()
    {
        var options = new PremiumOptions
        {
            Periods = [new PremiumPeriodOption { Months = 2, Amount = 100, Subscription = true }]
        };

        options.GetPeriods().Select(p => p.Months).ShouldBe([2]);
        options.FindSubscription(1).ShouldBeNull();
    }

    [Theory]
    [InlineData("org.telegram.telegramPremium.monthly", 1)]
    [InlineData("org.telegram.telegramPremium.annual", 12)]
    [InlineData("org.telegram.telegramPremium.biannual", 24)]
    [InlineData("org.telegram.telegramPremium.sixMonths", 6)]
    [InlineData(null, 1)]
    public void Store_product_ids_name_their_term(string? product, int months)
    {
        PremiumStorePurchaseService.MonthsFromStoreProduct(product).ShouldBe(months);
    }

    [Theory]
    [InlineData(false, 4000)]
    [InlineData(true, 8000)]
    public async Task Limits_follow_the_advertised_app_config(bool premium, int expectedParts)
    {
        var resolver = new PremiumLimitResolver(Users(premium).Object, new AppConfigHelper());

        (await resolver.GetLimitAsync(UserId, PremiumLimit.UploadMaxFileParts)).ShouldBe(expectedParts);
        (await resolver.GetLimitAsync(UserId, PremiumLimit.DialogsPinned)).ShouldBe(premium ? 10 : 5);
        (await resolver.GetLimitAsync(UserId, PremiumLimit.BotsCreate)).ShouldBe(premium ? 40 : 20);
    }

    [Theory]
    [InlineData(false, 4000, false)]
    [InlineData(false, 4001, true)]
    [InlineData(true, 8000, false)]
    [InlineData(true, 8001, true)]
    public async Task Uploads_past_the_part_limit_are_refused(bool premium, int parts, bool refused)
    {
        var guard = new PremiumLimitGuard(new PremiumLimitResolver(Users(premium).Object, new AppConfigHelper()),
            new Mock<IQueryProcessor>().Object);
        var media = new TInputMediaUploadedDocument
        {
            File = new TInputFileBig { Id = 1, Parts = parts, Name = "video.mp4" },
            MimeType = "video/mp4",
            Attributes = []
        };

        if (refused)
        {
            (await Should.ThrowAsync<RpcException>(() => guard.EnsureUploadPartsAsync(UserId, media)))
                .RpcError.Message.ShouldBe("FILE_PARTS_INVALID");
        }
        else
        {
            await guard.EnsureUploadPartsAsync(UserId, media);
        }
    }

    [Theory]
    [InlineData(false, 499, false)]
    [InlineData(false, 500, true)]
    [InlineData(true, 999, false)]
    [InlineData(true, 1000, true)]
    public async Task Joining_past_channels_limit_is_refused(bool premium, int joined, bool refused)
    {
        var queries = new Mock<IQueryProcessor>();
        queries.Setup(q => q.ProcessAsync(It.IsAny<GetJoinedChannelCountQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(joined);
        var guard = new PremiumLimitGuard(new PremiumLimitResolver(Users(premium).Object, new AppConfigHelper()),
            queries.Object);

        if (refused)
        {
            (await Should.ThrowAsync<RpcException>(() => guard.EnsureCanJoinChannelAsync(UserId)))
                .RpcError.Message.ShouldBe("CHANNELS_TOO_MUCH");
        }
        else
        {
            await guard.EnsureCanJoinChannelAsync(UserId);
        }
    }

    [Theory]
    [InlineData(false, 10, true)]
    [InlineData(true, 10, false)]
    public async Task Owning_past_channels_public_limit_is_refused(bool premium, int owned, bool refused)
    {
        var queries = new Mock<IQueryProcessor>();
        queries.Setup(q => q.ProcessAsync(It.IsAny<GetAdminedPublicChannelIdsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, owned).Select(i => (long)i).ToList());
        var guard = new PremiumLimitGuard(new PremiumLimitResolver(Users(premium).Object, new AppConfigHelper()),
            queries.Object);

        if (refused)
        {
            (await Should.ThrowAsync<RpcException>(() => guard.EnsureCanOwnPublicChannelAsync(UserId)))
                .RpcError.Message.ShouldBe("CHANNELS_ADMIN_PUBLIC_TOO_MUCH");
        }
        else
        {
            await guard.EnsureCanOwnPublicChannelAsync(UserId);
        }
    }

    [Fact]
    public void Shared_app_config_matches_real_telegrams_premium_keys()
    {
        var config = (TJsonObject)new AppConfigHelper().GetAppConfig();
        var values = config.Value.OfType<TJsonObjectValue>().ToDictionary(v => v.Key, v => v.Value);

        // tdlib and tdesktop treat a missing key as "blocked", so it must be present and false.
        values["premium_purchase_blocked"].ShouldBeOfType<TJsonBool>().Value.ShouldBeFalse();
        values["premium_bot_username"].ShouldBeOfType<TJsonString>().Value.ShouldBe("PremiumBot");
        values.ShouldNotContainKey("premium_invoice_slug");
        values.ShouldNotContainKey("premium_manage_subscription_url");
        ((TJsonNumber)values["bots_create_limit_default"]).Value.ShouldBe(20);
        ((TJsonNumber)values["bots_create_limit_premium"]).Value.ShouldBe(40);
        ((TJsonArray)values["premium_promo_order"]).Value.OfType<TJsonString>().Select(s => s.Value)
            .ShouldContain("ai_compose");
    }

    [Fact]
    public async Task Gift_options_follow_real_telegrams_shape()
    {
        var handler = CreateHandler("Payments.GetPremiumGiftCodeOptionsHandler",
            Microsoft.Extensions.Options.Options.Create(new PremiumOptions()), Gate(blocked: false).Object, PremiumSubscriptionTests.Pricing());

        var options = ((TVector<IPremiumGiftCodeOption>)(await InvokeAsync(handler, new MyTelegram.Schema.Payments.RequestGetPremiumGiftCodeOptions()))!)
            .Cast<TPremiumGiftCodeOption>().ToList();

        options.Count.ShouldBe(3 * 10 + 3);
        options[0].Months.ShouldBe(12);
        options[0].Users.ShouldBe(1);
        options.Where(o => o.Currency == "XTR").Select(o => (o.Months, o.Amount))
            .ShouldBe([(12, 2500L), (6, 1500L), (3, 1000L)]);
        options.Where(o => o.Currency == "XTR").ShouldAllBe(o => o.Users == 1);
    }

    [Fact]
    public async Task A_blocked_account_is_offered_nothing_to_buy()
    {
        var handler = CreateHandler("Payments.GetPremiumGiftCodeOptionsHandler",
            Microsoft.Extensions.Options.Options.Create(new PremiumOptions()), Gate(blocked: true).Object, PremiumSubscriptionTests.Pricing());

        var options = (TVector<IPremiumGiftCodeOption>)(await InvokeAsync(handler, new MyTelegram.Schema.Payments.RequestGetPremiumGiftCodeOptions()))!;

        options.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task App_config_reports_the_per_account_purchase_block(bool blocked)
    {
        var appConfigHelper = new AppConfigHelper();
        var sounds = new Mock<MyTelegram.Messenger.Services.Emoji.IEmojiSoundAppConfigBuilder>();
        var transcription = new Mock<MyTelegram.Messenger.Services.Transcription.ITranscriptionAppConfigBuilder>();
        var handler = CreateHandler("Help.GetAppConfigHandler", appConfigHelper, sounds.Object, transcription.Object,
            Gate(blocked).Object, PremiumSubscriptionTests.StarsGate(blocked).Object);

        var result = (MyTelegram.Schema.Help.TAppConfig)(await InvokeAsync(handler, new MyTelegram.Schema.Help.RequestGetAppConfig { Hash = 0 }))!;
        var values = ((TJsonObject)result.Config).Value.OfType<TJsonObjectValue>().ToList();

        foreach (var key in new[] { "premium_purchase_blocked", "stars_purchase_blocked" })
        {
            values.Count(v => v.Key == key).ShouldBe(1);
            values.Single(v => v.Key == key).Value.ShouldBeOfType<TJsonBool>().Value.ShouldBe(blocked);
        }

        (result.Hash == appConfigHelper.GetAppConfigHash()).ShouldBe(!blocked);
    }

    internal static Mock<IUserAppService> Users(bool premium)
    {
        var user = new Mock<IUserReadModel>();
        user.SetupGet(u => u.Premium).Returns(premium);
        user.SetupGet(u => u.UserId).Returns(UserId);
        var users = new Mock<IUserAppService>();
        users.Setup(u => u.GetAsync(It.IsAny<long>())).ReturnsAsync(user.Object);
        users.Setup(u => u.GetAsync(It.IsAny<long?>())).ReturnsAsync(user.Object);
        return users;
    }

    internal static Mock<IPremiumPurchaseGate> Gate(bool blocked)
    {
        var gate = new Mock<IPremiumPurchaseGate>();
        gate.Setup(g => g.IsBlockedAsync(It.IsAny<long>())).ReturnsAsync(blocked);
        gate.Setup(g => g.EnsureAllowedAsync(It.IsAny<long[]>())).Returns(() =>
        {
            if (blocked)
            {
                RpcErrors.RpcErrors406.PremiumCurrentlyUnavailable.ThrowRpcError();
            }

            return Task.CompletedTask;
        });
        return gate;
    }

    private static object CreateHandler(string name, params object[] args) => Activator.CreateInstance(
        typeof(PremiumOptions).Assembly.GetType("MyTelegram.Messenger.Handlers.LatestLayer." + name)!, args)!;

    private static async Task<object?> InvokeAsync(object handler, object request)
    {
        var input = new Mock<IRequestInput>();
        input.SetupGet(i => i.UserId).Returns(UserId);
        var method = handler.GetType().GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(handler, [input.Object, request])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }
}
