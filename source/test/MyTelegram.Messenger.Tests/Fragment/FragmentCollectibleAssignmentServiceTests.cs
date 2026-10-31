using EventFlow.Queries;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Domain.Aggregates.User;
using MyTelegram.Domain.Aggregates.UserName;
using MyTelegram.Messenger.Services.Fragment;
using MyTelegram.Messenger.Tests.SecretChat;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;

namespace MyTelegram.Messenger.Tests.Fragment;

/// <summary>
/// Feature: attaching a Fragment collectible username recorded in the catalogue by
/// <c>scripts/assign-nft-username.sh</c>.
/// <para>
/// The script used to write the username straight into <c>eventflow-userreadmodel</c>: no aggregate
/// knew about it, a read model rebuild lost it, it never resolved, and anybody could register the
/// same name as their own basic username. The command server now reserves the name and records it
/// through the owner's aggregate.
/// </para>
/// </summary>
public class FragmentCollectibleAssignmentServiceTests
{
    private const long OwnerUserId = 2_010_001;
    private const long OtherUserId = 2_010_002;

    [RequiresMongoDbFact]
    public async Task A_pending_username_is_reserved_and_attached_disabled()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedCatalogueAsync(mongo.Database);
        var commandBus = new RecordingCommandBus();
        var service = CreateService(mongo.Database, commandBus, holderUserId: null);

        await service.ProcessPendingAsync(CancellationToken.None);

        commandBus.Published.OfType<CreateUserNameCommand>().Single().UserName.ShouldBe("nft");
        commandBus.Published.OfType<SetUsernamesCommand>().Single().Usernames.ShouldBe([
            new UsernameItem("basic", true, true),
            new UsernameItem("nft", false, false)
        ]);
        (await StateAsync(mongo.Database)).ShouldBe("applied");
    }

    [RequiresMongoDbFact]
    public async Task A_username_held_by_someone_else_fails_without_touching_the_owner()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedCatalogueAsync(mongo.Database);
        var commandBus = new RecordingCommandBus();
        var service = CreateService(mongo.Database, commandBus, holderUserId: OtherUserId);

        await service.ProcessPendingAsync(CancellationToken.None);

        commandBus.Published.ShouldBeEmpty();
        (await StateAsync(mongo.Database)).ShouldBe("failed");
    }

    [Fact]
    public void A_hand_written_list_is_brought_into_the_editor_shape()
    {
        var normalized = FragmentCollectibleAssignmentService.NormalizeLegacyList(
        [
            new UsernameItem("hidden", false, false),
            new UsernameItem("stale", true, true),
            new UsernameItem("nft", false, true),
            new UsernameItem("NFT", false, true)
        ], "basic");

        normalized.ShouldBe([
            new UsernameItem("basic", true, true),
            new UsernameItem("nft", false, true),
            new UsernameItem("hidden", false, false)
        ]);
    }

    private static Task SeedCatalogueAsync(IMongoDatabase database)
    {
        return database.GetCollection<BsonDocument>(IFragmentCollectibleCatalog.CollectionName).InsertOneAsync(
            new BsonDocument
            {
                ["_id"] = "fragment-username-nft",
                ["type"] = "username",
                ["username"] = "nft",
                ["purchase_date"] = 1712160000,
                ["currency"] = "USD",
                ["amount"] = 14500,
                ["crypto_currency"] = "TON",
                ["crypto_amount"] = 50_000_000_000L,
                ["url"] = "https://fragment.com/username/nft",
                ["owner_peer_type"] = "user",
                ["owner_peer_id"] = OwnerUserId,
                ["assign_state"] = "pending"
            });
    }

    private static async Task<string> StateAsync(IMongoDatabase database)
    {
        var doc = await database.GetCollection<BsonDocument>(IFragmentCollectibleCatalog.CollectionName)
            .Find(FilterDefinition<BsonDocument>.Empty).FirstAsync();

        return doc["assign_state"].AsString;
    }

    private static FragmentCollectibleAssignmentService CreateService(IMongoDatabase database,
        RecordingCommandBus commandBus, long? holderUserId)
    {
        var owner = new Mock<IUserReadModel>(MockBehavior.Loose);
        owner.SetupGet(p => p.UserId).Returns(OwnerUserId);
        owner.SetupGet(p => p.UserName).Returns("basic");

        var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
        queryProcessor.Setup(p => p.ProcessAsync(It.Is<GetUserByIdQuery>(q => q.UserId == OwnerUserId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(owner.Object);

        if (holderUserId is { } holderId)
        {
            var holder = new Mock<IUserNameReadModel>(MockBehavior.Loose);
            holder.SetupGet(p => p.PeerType).Returns(PeerType.User);
            holder.SetupGet(p => p.PeerId).Returns(holderId);
            queryProcessor.Setup(p => p.ProcessAsync(It.IsAny<GetUserNameByNameQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(holder.Object);
        }

        return new FragmentCollectibleAssignmentService(database, commandBus, queryProcessor.Object,
            NullLogger<FragmentCollectibleAssignmentService>.Instance);
    }
}
