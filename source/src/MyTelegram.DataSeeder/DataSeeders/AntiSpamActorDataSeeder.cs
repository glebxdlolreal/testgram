using EventFlow.Aggregates;
using EventFlow.Queries;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.UserName;
using MyTelegram.Queries;

namespace MyTelegram.DataSeeder.DataSeeders;

/// <summary>Creates the local moderation actor through events, with durable creation provenance.</summary>
public sealed class AntiSpamActorDataSeeder(
    ICommandBus commandBus, IEventStore eventStore, ISnapshotStore snapshotStore,
    IQueryProcessor queryProcessor, IMongoDatabase database) : IDataSeeder, ITransientDependency
{
    public async Task SeedAsync()
    {
        var id = MyTelegramConsts.AntiSpamBotUserId;
        var markers = database.GetCollection<BsonDocument>(MyTelegramConsts.AntiSpamActorCollection);
        var marker = await markers.Find(new BsonDocument("_id", "actor")).SingleOrDefaultAsync();
        var aggregate = new UserAggregate(UserId.Create(id));
        await aggregate.LoadAsync(eventStore, snapshotStore, CancellationToken.None);
        var rows = await LoadRowsAsync();
        var username = await queryProcessor.ProcessAsync(new GetUserNameByNameQuery(MyTelegramConsts.AntiSpamBotUserName));
        if (username != null && (username.PeerId != id || username.PeerType != PeerType.User))
            throw new InvalidOperationException("Antispam actor username is occupied; no account was adopted.");

        if (marker == null)
        {
            if (!aggregate.IsNew || rows.Count != 0 || username != null)
                throw new InvalidOperationException("Antispam actor id is occupied without creation provenance.");
            marker = new BsonDocument
            {
                ["_id"] = "actor", ["user_id"] = id,
                ["access_hash"] = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue) * 2147483647L,
                ["request_id"] = Guid.NewGuid().ToString("D"), ["ready"] = false
            };
            // Insert, not replacement: concurrent seeders cannot adopt each other's proposed identity.
            await markers.InsertOneAsync(marker);
        }
        if (!MatchesInteger(marker, "user_id", id) ||
            !marker.TryGetValue("access_hash", out var hashValue) || !(hashValue.IsInt64 || hashValue.IsInt32) || hashValue.ToInt64() == 0 ||
            !marker.TryGetValue("request_id", out var requestValue) || !requestValue.IsString ||
            !Guid.TryParse(requestValue.AsString, out var requestId) || requestId == Guid.Empty || rows.Count > 1)
            throw new InvalidOperationException("Antispam actor provenance or unique identity is invalid.");
        var hash = hashValue.ToInt64();

        if (aggregate.IsNew)
        {
            if (rows.Count != 0 || marker.GetValue("ready", false) != BsonBoolean.False)
                throw new InvalidOperationException("Antispam actor exists only in a read model or lost its events; refusing adoption.");
            // Created without a username: request commands dedupe on ReqMsgId alone, and the create-user
            // saga would forward this RequestInfo to UpdateUserNameCommand2 on the same aggregate.
            await commandBus.PublishAsync(new CreateUserCommand(UserId.Create(id), Request(requestId, 1),
                id, hash, string.Empty, "Testgram Anti-Spam", null, null, true));
        }

        // A read model is not ownership proof. Check the original persisted creation before
        // modifying an existing aggregate, including a retry after CreateUser was committed.
        var events = await eventStore.LoadEventsAsync<UserAggregate, UserId>(UserId.Create(id), CancellationToken.None);
        var creations = events.OfType<IDomainEvent<UserAggregate, UserId, UserCreatedEvent>>().ToList();
        if (creations.Count != 1 || creations[0].AggregateEvent.UserId != id ||
            creations[0].AggregateEvent.AccessHash != hash || !creations[0].AggregateEvent.Bot ||
            creations[0].AggregateEvent.FirstName != "Testgram Anti-Spam" ||
            creations[0].AggregateEvent.UserName is not (null or "" or MyTelegramConsts.AntiSpamBotUserName) ||
            !string.IsNullOrEmpty(creations[0].AggregateEvent.PhoneNumber))
            throw new InvalidOperationException("Antispam actor event identity differs; no account was adopted.");

        var state = new UserState();
        foreach (var domainEvent in events.OrderBy(e => e.AggregateSequenceNumber))
        {
            switch (domainEvent.GetAggregateEvent())
            {
                case UserCreatedEvent created: state.Apply(created); break;
                case UserNameUpdatedEvent renamed: state.Apply(renamed); break;
                case UserProfileUpdatedEvent profile: state.Apply(profile); break;
                case UserFirstNameUpdatedEvent firstName: state.Apply(firstName); break;
                case UserDeletedEvent deleted: state.Apply(deleted); break;
                case UserVerifiedHasSetEvent verification: state.Apply(verification); break;
            }
        }
        if (state.UserId != id || state.AccessHash != hash || !state.IsBot || state.IsDeleted ||
            state.FirstName != "Testgram Anti-Spam" || state.UserName is not (null or "" or MyTelegramConsts.AntiSpamBotUserName))
            throw new InvalidOperationException("Antispam actor current event identity differs; no account was adopted.");
        if (state.UserName != MyTelegramConsts.AntiSpamBotUserName)
        {
            // A distinct ReqMsgId keeps the saga's UpdateUserNameCommand2 apart from the creation command.
            await commandBus.PublishAsync(new UpdateUserNameCommand(
                UserNameId.Create(MyTelegramConsts.AntiSpamBotUserName.ToLower()), Request(requestId, 2),
                new Peer(PeerType.User, id), MyTelegramConsts.AntiSpamBotUserName, null));
            var renamed = (await eventStore.LoadEventsAsync<UserAggregate, UserId>(UserId.Create(id), CancellationToken.None))
                .Select(e => e.GetAggregateEvent()).OfType<UserNameUpdatedEvent>().LastOrDefault();
            if (renamed?.UserItem.UserName != MyTelegramConsts.AntiSpamBotUserName)
                throw new InvalidOperationException("Antispam actor username was not applied; retry seeding.");
            state.Apply(renamed);
        }
        await ValidateProjectionAsync(hash, false);
        if (!state.Verified)
            await commandBus.PublishAsync(new SetVerifiedCommand(UserId.Create(id), true));
        // Projection lag is retryable, but never advertise ready before both reads agree.
        await ValidateProjectionAsync(hash, true);
        await markers.UpdateOneAsync(new BsonDocument
            { ["_id"] = "actor", ["user_id"] = id, ["access_hash"] = hash, ["request_id"] = requestValue },
            Builders<BsonDocument>.Update.Set("ready", true));
    }

    private static RequestInfo Request(Guid requestId, byte step) => RequestInfo.Empty with
    {
        RequestId = requestId,
        ReqMsgId = BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData([.. requestId.ToByteArray(), step])) | 1,
        Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };

    private Task<List<BsonDocument>> LoadRowsAsync() => database.GetCollection<BsonDocument>("eventflow-userreadmodel")
        .Find(Builders<BsonDocument>.Filter.Or(
            new BsonDocument("UserId", MyTelegramConsts.AntiSpamBotUserId),
            new BsonDocument("UserName", new BsonRegularExpression("^" + MyTelegramConsts.AntiSpamBotUserName + "$", "i"))))
        .Limit(2).ToListAsync();

    private async Task ValidateProjectionAsync(long hash, bool requireVerified)
    {
        var rows = await LoadRowsAsync();
        var user = await queryProcessor.ProcessAsync(new GetUserByIdQuery(MyTelegramConsts.AntiSpamBotUserId));
        if (rows.Count == 0 || user == null)
            throw new InvalidOperationException("Antispam actor projection is incomplete; retry seeding after projection catches up.");
        if (rows.Count != 1 || !MatchesInteger(rows[0], "UserId", MyTelegramConsts.AntiSpamBotUserId) ||
            !MatchesInteger(rows[0], "AccessHash", hash) ||
            rows[0].GetValue("Bot", false) != BsonBoolean.True ||
            rows[0].GetValue("FirstName", BsonNull.Value) != "Testgram Anti-Spam" ||
            rows[0].GetValue("UserName", BsonNull.Value) != MyTelegramConsts.AntiSpamBotUserName ||
            rows[0].GetValue("IsDeleted", BsonNull.Value) != BsonNull.Value && rows[0].GetValue("IsDeleted") != BsonBoolean.False ||
            user.UserId != MyTelegramConsts.AntiSpamBotUserId || !user.Bot || user.IsDeleted == true || user.AccessHash != hash ||
            user.UserName != MyTelegramConsts.AntiSpamBotUserName || user.FirstName != "Testgram Anti-Spam")
            throw new InvalidOperationException("Antispam actor projected identity differs; no account was adopted.");
        if (requireVerified && (!user.Verified || rows[0].GetValue("Verified", false) != BsonBoolean.True))
            throw new InvalidOperationException("Antispam actor verification projection is incomplete; retry seeding after projection catches up.");
    }

    private static bool MatchesInteger(BsonDocument document, string field, long expected) =>
        document.TryGetValue(field, out var value) && (value.IsInt64 || value.IsInt32) && value.ToInt64() == expected;
}
