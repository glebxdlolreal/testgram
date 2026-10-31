using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Fragment;

/// <summary>
/// Attaches and detaches <a href="https://corefork.telegram.org/api/fragment">Fragment collectible
/// usernames</a>. With no Fragment marketplace behind this server, the administrator records a sale
/// in the <c>fragment_collectibles</c> catalogue (<c>scripts/assign-nft-username.sh</c>): the entry
/// names its new owner and is marked <c>pending</c> (or <c>revoke_pending</c> when it is taken back).
/// This service turns that into domain commands, so the username is reserved for its owner and
/// survives a read model rebuild, and the clients are told through the usual updates.
/// <para>
/// On start it also records, once, the collectibles an earlier version of the script wrote straight
/// into the user and channel read models, which no event knew about.
/// </para>
/// </summary>
public class FragmentCollectibleAssignmentService(
    IMongoDatabase database,
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    ILogger<FragmentCollectibleAssignmentService> logger) : BackgroundService
{
    private const string MigrationsCollectionName = "fragment_migrations";
    private const string ReadModelGrantsMigrationId = "read-model-usernames-to-events-v1";

    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(5);

    private IMongoCollection<BsonDocument> Catalog =>
        database.GetCollection<BsonDocument>(IFragmentCollectibleCatalog.CollectionName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await MigrateReadModelGrantsAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not migrate the Fragment usernames stored in the read models");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error while applying Fragment collectible assignments");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }

    internal async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await Catalog
            .Find(Builders<BsonDocument>.Filter.Eq("type", "username") &
                  Builders<BsonDocument>.Filter.In("assign_state", new[]
                  {
                      IFragmentCollectibleCatalog.AssignStatePending,
                      IFragmentCollectibleCatalog.AssignStateRevokePending
                  }))
            .ToListAsync(cancellationToken);

        foreach (var doc in pending)
        {
            var collectible = FragmentCollectibleCatalog.Parse(doc);
            string? error;
            try
            {
                error = collectible.AssignState == IFragmentCollectibleCatalog.AssignStatePending
                    ? await AssignAsync(collectible)
                    : await RevokeAsync(collectible);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not apply the Fragment collectible {Id}", collectible.Id);
                error = ex.Message;
            }

            await MarkAsync(doc["_id"], collectible, error, cancellationToken);
        }
    }

    private async Task<string?> AssignAsync(FragmentCollectible collectible)
    {
        if (string.IsNullOrEmpty(collectible.Username) || collectible.OwnerPeerType == null ||
            collectible.OwnerPeerId is not { } ownerPeerId)
        {
            return "The entry names no username or no owner";
        }

        var owner = new Peer(collectible.OwnerPeerType.Value, ownerPeerId);
        var current = await GetUsernamesAsync(owner);
        if (current == null)
        {
            return $"{owner.PeerType} {owner.PeerId} does not exist";
        }

        var holder = await queryProcessor.ProcessAsync(new GetUserNameByNameQuery(collectible.Username.ToLowerInvariant()));
        if (holder != null && (holder.PeerType != owner.PeerType || holder.PeerId != owner.PeerId))
        {
            return $"@{collectible.Username} is held by {holder.PeerType} {holder.PeerId}";
        }

        if (holder == null)
        {
            await commandBus.PublishAsync(new CreateUserNameCommand(
                UserNameId.Create(collectible.Username.ToLowerInvariant()), owner, collectible.Username,
                DateTime.UtcNow.ToTimestamp()));
        }

        var updated = UsernameListEditor.AddCollectible(current.Value.Usernames, current.Value.LegacyUserName,
            collectible.Username);
        if (updated != null)
        {
            await SetUsernamesAsync(owner, current.Value.RequestUserId, updated,
                UsernameListEditor.Effective(current.Value.Usernames, current.Value.LegacyUserName));
        }

        logger.LogInformation("Attached the Fragment username @{Username} to {PeerType} {PeerId}",
            collectible.Username, owner.PeerType, owner.PeerId);

        return null;
    }

    private async Task<string?> RevokeAsync(FragmentCollectible collectible)
    {
        if (string.IsNullOrEmpty(collectible.Username) || collectible.OwnerPeerType == null ||
            collectible.OwnerPeerId is not { } ownerPeerId)
        {
            return "The entry names no username or no owner";
        }

        var owner = new Peer(collectible.OwnerPeerType.Value, ownerPeerId);
        var current = await GetUsernamesAsync(owner);
        if (current != null)
        {
            var updated = UsernameListEditor.RemoveCollectible(current.Value.Usernames, current.Value.LegacyUserName,
                collectible.Username);
            if (updated != null)
            {
                await SetUsernamesAsync(owner, current.Value.RequestUserId, updated,
                    UsernameListEditor.Effective(current.Value.Usernames, current.Value.LegacyUserName));
            }
        }

        var holder = await queryProcessor.ProcessAsync(new GetUserNameByNameQuery(collectible.Username.ToLowerInvariant()));
        if (holder != null && holder.PeerType == owner.PeerType && holder.PeerId == owner.PeerId)
        {
            await commandBus.PublishAsync(new DeleteUserNameCommand(
                UserNameId.Create(collectible.Username.ToLowerInvariant())));
        }

        logger.LogInformation("Detached the Fragment username @{Username} from {PeerType} {PeerId}",
            collectible.Username, owner.PeerType, owner.PeerId);

        return null;
    }

    private Task MarkAsync(BsonValue id, FragmentCollectible collectible, string? error,
        CancellationToken cancellationToken)
    {
        var update = error != null
            ? Builders<BsonDocument>.Update
                .Set("assign_state", IFragmentCollectibleCatalog.AssignStateFailed)
                .Set("assign_error", error)
            : collectible.AssignState == IFragmentCollectibleCatalog.AssignStatePending
                ? Builders<BsonDocument>.Update
                    .Set("assign_state", IFragmentCollectibleCatalog.AssignStateApplied)
                    .Unset("assign_error")
                : Builders<BsonDocument>.Update
                    .Set("assign_state", IFragmentCollectibleCatalog.AssignStateRevoked)
                    .Unset("assign_error")
                    .Unset("owner_peer_type")
                    .Unset("owner_peer_id");

        if (error != null)
        {
            logger.LogWarning("Fragment collectible {Id} was not applied: {Error}", collectible.Id, error);
        }

        // Only an entry still in the state we read is updated, so a newer request is not overwritten.
        return Catalog.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id) &
            Builders<BsonDocument>.Filter.Eq("assign_state", collectible.AssignState),
            update.Set("assign_updated_at", DateTime.UtcNow.ToTimestamp()),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The usernames a peer has now, read from the store rather than the cache, and the user to name
    /// as the actor (the channel creator, for a channel). Null when the peer does not exist.
    /// </summary>
    private async Task<(List<UsernameItem>? Usernames, string? LegacyUserName, long RequestUserId)?> GetUsernamesAsync(
        Peer peer)
    {
        switch (peer.PeerType)
        {
            case PeerType.User:
                var user = await queryProcessor.ProcessAsync(new GetUserByIdQuery(peer.PeerId));
                return user == null || user.IsDeleted == true
                    ? null
                    : (user.Usernames.ToUsernameItems(), user.UserName, user.UserId);
            case PeerType.Channel:
                var channel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(peer.PeerId));
                return channel == null ? null : (channel.Usernames.ToUsernameItems(), channel.UserName, channel.CreatorId);
            default:
                return null;
        }
    }

    private Task SetUsernamesAsync(Peer owner, long requestUserId, List<UsernameItem> usernames,
        List<UsernameItem>? oldUsernames)
    {
        // No client request is behind this: a fresh ReqMsgId keeps the command from being taken for a
        // duplicate, and no rpc result goes anywhere.
        var requestInfo = RequestInfo.Empty with
        {
            UserId = requestUserId,
            ReqMsgId = DateTime.UtcNow.Ticks,
            RequestId = Guid.NewGuid(),
            Date = DateTime.UtcNow.ToTimestamp()
        };

        return owner.PeerType == PeerType.Channel
            ? commandBus.PublishAsync(new SetChannelUsernamesCommand(ChannelId.Create(owner.PeerId), requestInfo,
                usernames, oldUsernames))
            : commandBus.PublishAsync(new SetUsernamesCommand(UserId.Create(owner.PeerId), requestInfo, usernames,
                oldUsernames));
    }

    /// <summary>
    /// The first version of <c>assign-nft-username.sh</c> wrote collectibles straight into
    /// <c>eventflow-userreadmodel</c>. They are replayed here through the aggregates, once, so that
    /// rebuilding the read model keeps them and the usernames resolve.
    /// </summary>
    internal async Task MigrateReadModelGrantsAsync(CancellationToken cancellationToken)
    {
        var migrations = database.GetCollection<BsonDocument>(MigrationsCollectionName);
        if (await migrations.Find(Builders<BsonDocument>.Filter.Eq("_id", ReadModelGrantsMigrationId))
                .AnyAsync(cancellationToken))
        {
            return;
        }

        var withCollectibles = Builders<BsonDocument>.Filter.ElemMatch<BsonDocument>("Usernames",
            Builders<BsonDocument>.Filter.Eq("Editable", false));

        var userIds = await database.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .Find(withCollectibles)
            .Project(Builders<BsonDocument>.Projection.Include("UserId"))
            .ToListAsync(cancellationToken);
        var channelIds = await database.GetCollection<BsonDocument>("eventflow-channelreadmodel")
            .Find(withCollectibles)
            .Project(Builders<BsonDocument>.Projection.Include("ChannelId"))
            .ToListAsync(cancellationToken);

        var peers = userIds.Where(p => p.Contains("UserId")).Select(p => new Peer(PeerType.User, p["UserId"].ToInt64()))
            .Concat(channelIds.Where(p => p.Contains("ChannelId"))
                .Select(p => new Peer(PeerType.Channel, p["ChannelId"].ToInt64())))
            .ToList();

        foreach (var peer in peers)
        {
            try
            {
                await MigratePeerAsync(peer, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not migrate the Fragment usernames of {PeerType} {PeerId}", peer.PeerType,
                    peer.PeerId);
            }
        }

        await migrations.InsertOneAsync(new BsonDocument
        {
            ["_id"] = ReadModelGrantsMigrationId,
            ["peers"] = peers.Count,
            ["date"] = DateTime.UtcNow.ToTimestamp()
        }, cancellationToken: cancellationToken);

        logger.LogInformation("Recorded the Fragment usernames of {Count} peers through their aggregates", peers.Count);
    }

    private async Task MigratePeerAsync(Peer peer, CancellationToken cancellationToken)
    {
        var current = await GetUsernamesAsync(peer);
        if (current?.Usernames is not { Count: > 0 } stored)
        {
            return;
        }

        var normalized = NormalizeLegacyList(stored, current.Value.LegacyUserName);

        foreach (var collectible in normalized.Where(p => !p.Editable))
        {
            var id = collectible.Username.ToLowerInvariant();
            var holder = await queryProcessor.ProcessAsync(new GetUserNameByNameQuery(id), cancellationToken);
            if (holder == null)
            {
                await commandBus.PublishAsync(new CreateUserNameCommand(UserNameId.Create(id), peer,
                    collectible.Username, DateTime.UtcNow.ToTimestamp()), cancellationToken);
            }
            else if (holder.PeerType != peer.PeerType || holder.PeerId != peer.PeerId)
            {
                logger.LogWarning("@{Username} is attached to {PeerType} {PeerId} but held by {HolderType} {HolderId}",
                    collectible.Username, peer.PeerType, peer.PeerId, holder.PeerType, holder.PeerId);
                continue;
            }

            // Tie the catalogue entry to its owner, so it no longer reads as for sale.
            await Catalog.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("type", "username") &
                Builders<BsonDocument>.Filter.Eq("username", id) &
                Builders<BsonDocument>.Filter.Exists("assign_state", false),
                Builders<BsonDocument>.Update
                    .Set("owner_peer_type", peer.PeerType == PeerType.Channel ? "channel" : "user")
                    .Set("owner_peer_id", peer.PeerId)
                    .Set("assign_state", IFragmentCollectibleCatalog.AssignStateApplied)
                    .Set("assign_updated_at", DateTime.UtcNow.ToTimestamp()),
                cancellationToken: cancellationToken);
        }

        await SetUsernamesAsync(peer, current.Value.RequestUserId, normalized, null);
    }

    /// <summary>
    /// Brings a hand-written list into the shape the editor keeps: no duplicates, the editable entry
    /// matching the basic username, and the active usernames ahead of the disabled ones.
    /// </summary>
    internal static List<UsernameItem> NormalizeLegacyList(IReadOnlyList<UsernameItem> stored, string? legacyUserName)
    {
        var distinct = stored
            .Where(p => !string.IsNullOrEmpty(p.Username))
            .DistinctBy(p => p.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Only one editable entry is meaningful; any other is kept as a collectible.
        var editableIndex = distinct.FindIndex(p => p.Editable);
        distinct = distinct.Select((p, i) => p.Editable && i != editableIndex ? p with { Editable = false } : p)
            .ToList();

        if (!string.IsNullOrEmpty(legacyUserName))
        {
            distinct = UsernameListEditor.ChangeEditable(distinct, legacyUserName)!
                .DistinctBy(p => p.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // The basic username cannot be disabled.
        distinct = distinct.Select(p => p.Editable ? p with { Active = true } : p).ToList();

        return distinct.Where(p => p.Active).Concat(distinct.Where(p => !p.Active)).ToList();
    }
}
