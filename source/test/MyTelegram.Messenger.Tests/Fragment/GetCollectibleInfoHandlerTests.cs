using EventFlow.Queries;
using Moq;
using MongoDB.Bson;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Handlers.LatestLayer.Fragment;
using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Messenger.Services.Fragment;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Tests.Peers;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Fragment;

namespace MyTelegram.Messenger.Tests.Fragment;

/// <summary>
/// Feature: <c>fragment.getCollectibleInfo</c>.
/// <para>
/// It answered for any catalogue entry, so anybody could learn about a collectible its owner had not
/// activated, or about a phone number hidden by privacy settings. The API page limits it to the
/// caller's own collectibles, usernames their holder activated and phone numbers the caller may see.
/// </para>
/// </summary>
public class GetCollectibleInfoHandlerTests
{
    private const long OwnerUserId = 2_000_001;
    private const long CallerUserId = 2_000_002;

    [Fact]
    public async Task An_activated_username_is_described_to_anyone()
    {
        var handler = CreateHandler(active: true);

        var result = (TCollectibleInfo)(await ChannelUsernameHandlerTests.InvokeAsync(handler, CallerUserId,
            new RequestGetCollectibleInfo { Collectible = new TInputCollectibleUsername { Username = "NFT" } }))!;

        result.PurchaseDate.ShouldBe(1712160000);
        result.Amount.ShouldBe(14500);
        result.CryptoAmount.ShouldBe(50_000_000_000);
        result.Url.ShouldBe("https://fragment.com/username/nft");
    }

    [Fact]
    public async Task A_disabled_username_is_hidden_from_others()
    {
        var handler = CreateHandler(active: false);

        var exception = await Should.ThrowAsync<RpcException>(() => ChannelUsernameHandlerTests.InvokeAsync(handler,
            CallerUserId, new RequestGetCollectibleInfo { Collectible = new TInputCollectibleUsername { Username = "nft" } }));

        exception.RpcError.Message.ShouldBe("COLLECTIBLE_NOT_FOUND");
    }

    [Fact]
    public async Task A_disabled_username_is_described_to_its_owner()
    {
        var handler = CreateHandler(active: false);

        var result = await ChannelUsernameHandlerTests.InvokeAsync(handler, OwnerUserId,
            new RequestGetCollectibleInfo { Collectible = new TInputCollectibleUsername { Username = "nft" } });

        result.ShouldBeOfType<TCollectibleInfo>();
    }

    [Fact]
    public async Task An_empty_input_is_COLLECTIBLE_INVALID()
    {
        var handler = CreateHandler(active: true);

        var exception = await Should.ThrowAsync<RpcException>(() => ChannelUsernameHandlerTests.InvokeAsync(handler,
            CallerUserId, new RequestGetCollectibleInfo { Collectible = new TInputCollectiblePhone { Phone = "+ " } }));

        exception.RpcError.Message.ShouldBe("COLLECTIBLE_INVALID");
    }

    [Fact]
    public void Catalogue_numbers_are_read_whatever_their_BSON_type()
    {
        var collectible = FragmentCollectibleCatalog.Parse(new BsonDocument
        {
            ["_id"] = "fragment-phone-888123456",
            ["type"] = "phone",
            ["phone"] = "888123456",
            ["purchase_date"] = 1712160000.0,
            ["currency"] = "USD",
            ["amount"] = new BsonInt64(29900),
            ["crypto_currency"] = "TON",
            ["crypto_amount"] = 1e11,
            ["url"] = "https://fragment.com/number/888123456"
        });

        collectible.PurchaseDate.ShouldBe(1712160000);
        collectible.Amount.ShouldBe(29900);
        collectible.CryptoAmount.ShouldBe(100_000_000_000);
    }

    [Fact]
    public void Phone_numbers_are_compared_as_bare_digits()
    {
        FragmentCollectibleCatalog.NormalizePhone("+888 1234 5678").ShouldBe("88812345678");
    }

    private static object CreateHandler(bool active)
    {
        var catalog = new Mock<IFragmentCollectibleCatalog>(MockBehavior.Loose);
        catalog.Setup(p => p.FindUsernameAsync(It.IsAny<string>())).ReturnsAsync(new FragmentCollectible(
            "fragment-username-nft", "username", "nft", null, 1712160000, "USD", 14500, "TON", 50_000_000_000,
            "https://fragment.com/username/nft", PeerType.User, OwnerUserId, "applied"));

        var holder = new Mock<IUserNameReadModel>(MockBehavior.Loose);
        holder.SetupGet(p => p.PeerType).Returns(PeerType.User);
        holder.SetupGet(p => p.PeerId).Returns(OwnerUserId);

        var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
        queryProcessor.Setup(p => p.ProcessAsync(It.IsAny<GetUserNameByNameQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(holder.Object);

        var owner = new Mock<IUserReadModel>(MockBehavior.Loose);
        owner.SetupGet(p => p.UserId).Returns(OwnerUserId);
        owner.SetupGet(p => p.Usernames).Returns(
        [
            new UsernameInfo { Username = "basic", Editable = true, Active = true },
            new UsernameInfo { Username = "nft", Editable = false, Active = active }
        ]);

        var userAppService = new Mock<IUserAppService>(MockBehavior.Loose);
        userAppService.Setup(p => p.GetAsync((long?)OwnerUserId)).ReturnsAsync(owner.Object);

        return new GetCollectibleInfoHandler(catalog.Object, queryProcessor.Object, userAppService.Object,
            new Mock<IChannelAppService>(MockBehavior.Loose).Object,
            new Mock<IUserConverterService>(MockBehavior.Loose).Object,
            new Mock<IBotOwnershipChecker>(MockBehavior.Loose).Object);
    }
}
