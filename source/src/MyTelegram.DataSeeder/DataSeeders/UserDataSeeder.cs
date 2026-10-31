using EventFlow.Queries;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.UserName;
using MyTelegram.Queries;

namespace MyTelegram.DataSeeder.DataSeeders;

public class UserDataSeeder(
    ICommandBus commandBus,
    IEventStore eventStore,
    IQueryProcessor queryProcessor,
    IMongoDatabase database,
    ILogger<UserDataSeeder> logger,
    IOptionsMonitor<MyTelegramDataSeederOptions> options,
    IDataSeederHelper dataSeederHelper,
    ISnapshotStore snapshotStore)
    : IDataSeeder, ITransientDependency
{
    public async Task SeedAsync()
    {
        var config = await dataSeederHelper.LoadDataSeederConfigAsync();

        if (config.IsUserCreated)
        {
            await UpdateServiceNotificationAccountBioAsync();

            // Added after the first seeding run of the installs that already exist, so it cannot live
            // behind the "users were created" flag.
            await CreateChatImporterBotUserAsync();
            await CreateGifSearchBotUserAsync();
            await CreatePremiumBotUserAsync();

            return;
        }

        await CreateOfficialUserAsync();
        await CreateChatImporterBotUserAsync();
        await CreateDefaultSupportUserAsync();
        await CreateAnonymousUserAsync();
        await CreateGroupAnonymousBotUserAsync();
        await CreateBotFatherUserAsync();
        await CreateGifSearchBotUserAsync();
        await CreatePremiumBotUserAsync();

        if (options.CurrentValue.CreateTestUsers)
        {
            var initUserId = MyTelegramConsts.UserIdInitId;
            var testUserCount = 30;
            for (var i = 1; i < testUserCount; i++)
            {
                await CreateUserIfNeededAsync(initUserId + i,
                    $"1{i}",
                    $"{i}",
                    $"{i}",
                    $"user{i}",
                    false);
            }
        }

        config.IsUserCreated = true;
        await dataSeederHelper.SaveDataSeederConfigAsync();
    }

    public async Task<bool> CreateUserIfNeededAsync(long userId,
        string phoneNumber,
        string firstName,
        string? lastName,
        string? userName,
        bool bot)
    {
        var aggregateId = UserId.Create(userId);
        var u = new UserAggregate(aggregateId);
        await u.LoadAsync(eventStore, snapshotStore, CancellationToken.None);
        if (!u.IsNew)
        {
            return false;
        }

        // Some accounts are inserted straight into the read model by an init container rather than
        // through an aggregate (BotFather is), so their aggregate looks new while the user already
        // exists. Creating it again would leave two read-model documents for one user id, and
        // GetUserByIdQuery would then return either of them at random.
        var existingUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(userId));
        if (existingUser != null)
        {
            logger.LogInformation("User {UserId} already exists outside the event store — skipping", userId);

            return false;
        }

        var accessHash = Random.Shared.NextInt64();
        var createUserCommand =
            new CreateUserCommand(aggregateId,
                // The request id seeds the deterministic source id of every command the create-user
                // saga chain publishes. Left at Guid.Empty, the follow-up UpdateUserNameCommand2 of
                // the second seeded user collides with an operation already performed, and the whole
                // seeding run dies with a DuplicateOperationException.
                RequestInfo.Empty with
                {
                    RequestId = Guid.NewGuid(),
                    Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                },
                userId,
                accessHash,
                phoneNumber,
                firstName,
                lastName,
                userName,
                bot
            );
        await commandBus.PublishAsync(createUserCommand);

        if (userId != MyTelegramConsts.NotificationServiceUserId)
        {
            var command = new UpdateUserPremiumStatusCommand(u.Id, true);
            await commandBus.PublishAsync(command);
        }

        logger.LogInformation("User {UserName} created successfully", userName ?? firstName);

        return true;
    }

    private async Task CreateDefaultSupportUserAsync()
    {
        var userId = MyTelegramConsts.DefaultSupportUserId;
        var created = await CreateUserIfNeededAsync(userId,
            MyTelegramConsts.DefaultSupportUserId.ToString(),
            "Testgram Support",
            null,
            null,
            false);

        if (created)
        {
            var command = new SetSupportCommand(UserId.Create(userId), true);
            await commandBus.PublishAsync(command);

            var setVerifiedCommand = new SetVerifiedCommand(UserId.Create(userId), true);
            await commandBus.PublishAsync(setVerifiedCommand);
            logger.LogInformation("Testgram support user created successfully");
        }
    }

    /// <summary>
    /// The peer that authors the messages of a chat history imported from a foreign chat app. The
    /// original author is only a name in <c>fwd_from.from_name</c>, so the messages need a sender of
    /// their own — attributing them to the importing user would make a whole imported history look
    /// like their own messages.
    /// See https://corefork.telegram.org/api/import
    /// </summary>
    private async Task CreateChatImporterBotUserAsync()
    {
        var userId = MyTelegramConsts.ChatImporterBotUserId;

        try
        {
            // Not verified and not support: the production account carries neither flag.
            var created = await CreateUserIfNeededAsync(userId,
                string.Empty,
                "Imported Message",
                null,
                "ChatsImportBot",
                true);

            if (created)
            {
                logger.LogInformation("Chat importer bot user created successfully");
            }
        }
        catch (Exception ex)
        {
            // The username saga of this chain occasionally republishes its command and trips the
            // duplicate-operation guard after the account is already written. That must not take the
            // remaining seeders down with it, so the outcome is checked instead of trusted.
            var user = await queryProcessor.ProcessAsync(new GetUserByIdQuery(userId));
            if (user == null)
            {
                logger.LogError(ex, "Chat importer bot user could not be created");

                return;
            }

            logger.LogWarning(ex, "Chat importer bot user was created, but its seeding chain reported an error");
        }
    }

    private async Task CreateAnonymousUserAsync()
    {
        var userId = MyTelegramConsts.AnonymousUserId;
        var firstName = "Anonymous User";
        await CreateUserIfNeededAsync(userId, string.Empty, firstName, null, null, false);
    }

    /// <summary>
    /// The peer that authors messages sent anonymously by a group admin. Clients resolve it like any
    /// other sender, so it has to exist as a user.
    /// See https://corefork.telegram.org/api/channel#anonymous-admins
    /// </summary>
    private async Task CreateGroupAnonymousBotUserAsync()
    {
        var created = await CreateUserIfNeededAsync(MyTelegramConsts.GroupAnonymousBotUserId,
            string.Empty,
            "Group",
            null,
            null,
            true);

        if (created)
        {
            await commandBus.PublishAsync(
                new SetVerifiedCommand(UserId.Create(MyTelegramConsts.GroupAnonymousBotUserId), true));
        }
    }

    /// <summary>
    /// The bot that creates and manages other bots. Installs that predate this seeder have it inserted
    /// straight into the read model instead, in which case <see cref="CreateUserIfNeededAsync"/> leaves
    /// it alone and <see cref="BotFatherMigrator"/> repairs whatever is around it.
    /// </summary>
    private async Task CreateBotFatherUserAsync()
    {
        var created = await CreateUserIfNeededAsync(MyTelegramConsts.BotFatherUserId,
            "0",
            "BotFather",
            null,
            "botfather",
            true);

        if (created)
        {
            await commandBus.PublishAsync(
                new SetVerifiedCommand(UserId.Create(MyTelegramConsts.BotFatherUserId), true));
            await UpdateUserBioAsync(MyTelegramConsts.BotFatherUserId,
                "I can help you create and manage bots. Use /start to begin.");
        }
    }

    /// <summary>
    /// The <c>@gif</c> bot, which is what <c>config.gif_search_username</c> points clients at for GIF
    /// search. It answers inline queries in-process (<c>GifSearchBotService</c>) rather than through a
    /// webhook, so it needs no token — but it does need <c>InlineEnabled</c>, which is the only thing
    /// <c>messages.getInlineBotResults</c> checks before letting a query through.
    /// See https://corefork.telegram.org/api/gifs#searching-gifs
    /// </summary>
    private async Task CreateGifSearchBotUserAsync()
    {
        var userId = MyTelegramConsts.GifSearchBotUserId;
        var created = await CreateUserIfNeededAsync(userId,
            string.Empty,
            "GIF",
            null,
            "gif",
            true);

        if (!created)
        {
            // Only ever write state for an account that really is this bot. The id is reserved and the
            // allocator skips it, but an install that handed it out before it was reserved would
            // otherwise have some user's bot renamed and given inline rights it never asked for.
            var existing = await queryProcessor.ProcessAsync(new GetUserByIdQuery(userId));
            if (existing != null && !string.IsNullOrEmpty(existing.UserName) &&
                !string.Equals(existing.UserName, "gif", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "User id {UserId} is reserved for the @gif bot but is held by {UserName}; GIF search " +
                    "will not work until that account is moved.", userId, existing.UserName);

                return;
            }
        }

        // The username is what clients resolve config.gif_search_username through. A first attempt that
        // failed - the peer existed before service usernames were allowed to be shorter than five
        // characters - leaves the account without one, so it is registered here rather than only inside
        // the create-user saga.
        await EnsureGifUserNameAsync(userId);

        // Checked against the read model rather than published unconditionally: republishing the same
        // command on every seeding run is how a DuplicateOperationException ends a run.
        var user = await queryProcessor.ProcessAsync(new GetUserByIdQuery(userId));
        if (user is { Verified: false })
        {
            await commandBus.PublishAsync(new SetVerifiedCommand(UserId.Create(userId), true));
        }

        if (string.IsNullOrEmpty(user?.About))
        {
            await UpdateUserBioAsync(userId, "I search GIFs. Type @gif followed by what you are looking for.");
        }

        // Idempotent, and applied even when the user already existed: an install that was seeded before
        // this bot was added would otherwise have the account without the flag that makes it usable.
        await database.GetCollection<BsonDocument>("botfather-bot-state").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("BotUserId", userId),
            Builders<BsonDocument>.Update
                .Set("BotUserId", userId)
                .Set("Username", "gif")
                .Set("Name", "GIF")
                .Set("InlineEnabled", true)
                .SetOnInsert("OwnerId", 0L)
                .SetOnInsert("CreatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            new UpdateOptions { IsUpsert = true });
    }

    /// <summary>
    /// Creates <c>@PremiumBot</c>, the bot <c>premium_bot_username</c> names and clients open with
    /// <c>messages.startBot</c> to buy Premium. It is answered in-process by <c>PremiumBotService</c>.
    /// See https://corefork.telegram.org/api/premium
    /// </summary>
    private async Task CreatePremiumBotUserAsync()
    {
        var userId = MyTelegramConsts.PremiumBotUserId;
        var userName = MyTelegramConsts.PremiumBotUserName;

        // Installs that predate this bot may have let a user register the name through BotFather. That
        // bot keeps its owner and everything else; only the name moves, so the official one can have it.
        if (!await FreeServiceUserNameAsync(userName, userId))
        {
            return;
        }

        var created = await CreateUserIfNeededAsync(userId, string.Empty, "Premium Bot", null, userName, true);
        if (!created)
        {
            var existing = await queryProcessor.ProcessAsync(new GetUserByIdQuery(userId));
            if (existing != null && !string.IsNullOrEmpty(existing.UserName) &&
                !string.Equals(existing.UserName, userName, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("User id {UserId} is reserved for @{BotUserName} but is held by {UserName}",
                    userId, userName, existing.UserName);

                return;
            }
        }

        await EnsureServiceUserNameAsync(userId, userName);

        var user = await queryProcessor.ProcessAsync(new GetUserByIdQuery(userId));
        if (user is { Verified: false })
        {
            await commandBus.PublishAsync(new SetVerifiedCommand(UserId.Create(userId), true));
        }

        if (string.IsNullOrEmpty(user?.About))
        {
            await UpdateUserBioAsync(userId, "I can help you subscribe to Telegram Premium. Send /start to see the options.");
        }

        await database.GetCollection<BsonDocument>("botfather-bot-state").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("BotUserId", userId),
            Builders<BsonDocument>.Update
                .Set("BotUserId", userId)
                .Set("Username", userName)
                .Set("Name", "Premium Bot")
                .SetOnInsert("OwnerId", 0L)
                .SetOnInsert("CreatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            new UpdateOptions { IsUpsert = true });
    }

    /// <summary>
    /// Makes <paramref name="userName"/> available to the service account <paramref name="ownerId"/>.
    /// A user-created bot holding it is renamed to <c>legacy{id}_{name}</c> (still ending in "bot");
    /// anything else holding it is left alone and false is returned.
    /// </summary>
    private async Task<bool> FreeServiceUserNameAsync(string userName, long ownerId)
    {
        var holder = await queryProcessor.ProcessAsync(new GetUserNameByNameQuery(userName.ToLowerInvariant()));
        if (holder == null || (holder.PeerType == PeerType.User && holder.PeerId == ownerId))
        {
            return true;
        }

        var holderUser = holder.PeerType == PeerType.User
            ? await queryProcessor.ProcessAsync(new GetUserByIdQuery(holder.PeerId))
            : null;
        if (holderUser is not { Bot: true } || MyTelegramConsts.IsReservedSystemBotUserId(holder.PeerId))
        {
            logger.LogWarning("The username @{UserName} is held by {PeerType} {PeerId}, which is not a user-created bot; " +
                              "it is not taken over", userName, holder.PeerType, holder.PeerId);

            return false;
        }

        var newUserName = $"legacy{holder.PeerId}_{userName.ToLowerInvariant()}";

        // BotFather writes the bots it creates straight into the read models, with no user or username
        // aggregate behind them, so the rename saga has nothing to act on (it fails with
        // AggregateIsCreatedSpecification). Such a holder cannot be moved through events.
        var holderAggregate = new UserAggregate(UserId.Create(holder.PeerId));
        await holderAggregate.LoadAsync(eventStore, snapshotStore, CancellationToken.None);
        if (holderAggregate.IsNew)
        {
            await DeleteStaleUserNameAsync(newUserName);
            logger.LogWarning("The username @{UserName} is held by bot {BotUserId}, which exists only in the read models " +
                              "(created by BotFather); it cannot be renamed through events, so @{UserName} is not seeded",
                userName, holder.PeerId, userName);

            return false;
        }

        await commandBus.PublishAsync(new UpdateUserNameCommand(
            UserNameId.Create(newUserName.ToLowerInvariant()),
            RequestInfo.Empty with
            {
                RequestId = Guid.NewGuid(),
                ReqMsgId = Random.Shared.NextInt64(1, long.MaxValue) | 1,
                Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            new Peer(PeerType.User, holder.PeerId),
            newUserName,
            holderUser.UserName));

        await database.GetCollection<BsonDocument>("botfather-bot-state").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("BotUserId", holder.PeerId),
            Builders<BsonDocument>.Update.Set("Username", newUserName));

        var after = await queryProcessor.ProcessAsync(new GetUserNameByNameQuery(userName.ToLowerInvariant()));
        if (after != null && after.PeerId == holder.PeerId)
        {
            logger.LogWarning("Moving @{UserName} off bot {BotUserId} did not take effect; retry seeding", userName, holder.PeerId);

            return false;
        }

        logger.LogInformation("Moved @{UserName} off user-created bot {BotUserId}; it is now @{NewUserName}",
            userName, holder.PeerId, newUserName);

        return true;
    }

    /// <summary>Removes a username registered by an earlier, failed attempt to move a name off a bot.</summary>
    private async Task DeleteStaleUserNameAsync(string userName)
    {
        var stale = await queryProcessor.ProcessAsync(new GetUserNameByNameQuery(userName.ToLowerInvariant()));
        if (stale == null)
        {
            return;
        }

        await commandBus.PublishAsync(new DeleteUserNameCommand(UserNameId.Create(userName.ToLowerInvariant())));
        logger.LogInformation("Removed the stale username @{UserName}", userName);
    }

    private async Task EnsureServiceUserNameAsync(long userId, string userName)
    {
        var existing = await queryProcessor.ProcessAsync(new GetUserNameByNameQuery(userName.ToLowerInvariant()));
        if (existing != null && existing.PeerId == userId)
        {
            return;
        }

        if (existing != null)
        {
            logger.LogWarning("The username @{UserName} is held by {PeerType} {PeerId}", userName, existing.PeerType, existing.PeerId);

            return;
        }

        await commandBus.PublishAsync(new CreateUserNameCommand(UserNameId.Create(userName.ToLowerInvariant()),
            new Peer(PeerType.User, userId), userName,
            (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        logger.LogInformation("Registered the username @{UserName} for {UserId}", userName, userId);
    }

    private async Task EnsureGifUserNameAsync(long userId)
    {
        var existing = await queryProcessor.ProcessAsync(new GetUserNameByNameQuery("gif"));
        if (existing != null && existing.PeerId == userId)
        {
            return;
        }

        if (existing != null)
        {
            logger.LogWarning("The username @gif is held by {PeerType} {PeerId}; GIF search will not work",
                existing.PeerType, existing.PeerId);

            return;
        }

        await commandBus.PublishAsync(new CreateUserNameCommand(UserNameId.Create("gif"),
            new Peer(PeerType.User, userId), "gif",
            (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        logger.LogInformation("Registered the username @gif for the GIF search bot");
    }

    private async Task CreateOfficialUserAsync()
    {
        var userId = MyTelegramConsts.NotificationServiceUserId;
        var created = await CreateUserIfNeededAsync(userId,
            "42777",
            "Testgram",
            null,
            null,
            false);

        if (created)
        {
            var command = new SetSupportCommand(UserId.Create(userId), true);
            await commandBus.PublishAsync(command);

            var setVerifiedCommand = new SetVerifiedCommand(UserId.Create(userId), true);
            await commandBus.PublishAsync(setVerifiedCommand);
            logger.LogInformation("Testgram notification user created successfully");
        }

        await UpdateServiceNotificationAccountBioAsync();
    }

    private async Task UpdateServiceNotificationAccountBioAsync()
    {
        var config = await dataSeederHelper.LoadDataSeederConfigAsync();
        if (!config.IsServiceNotificationAccountBioUpdated)
        {
            await UpdateUserBioAsync(MyTelegramConsts.NotificationServiceUserId, "Testgram — fork of MyTelegram open-source project.\nRepository: https://github.com/glebxdlolreal/testgram");
            config.IsServiceNotificationAccountBioUpdated = true;
            await dataSeederHelper.SaveDataSeederConfigAsync();
        }
    }

    private Task UpdateUserBioAsync(long userId, string bio)
    {
        var command = new UpdateAboutCommand(UserId.Create(userId), bio);
        return commandBus.PublishAsync(command);
    }
}
