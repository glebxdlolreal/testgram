using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Mentions;

namespace MyTelegram.Messenger.Services.AntiSpam;

/// <summary>Durable, generation-fenced retries for scalar recounts; no job TTL or in-memory-only queue.</summary>
public sealed class AntiSpamMentionRecountService(IMongoDatabase database, IAggregateStore aggregates,
    ICommandBus commands, IQueryProcessor queries, IMentionReadStateService readState,
    ILogger<AntiSpamMentionRecountService> logger) : BackgroundService
{
    private IMongoCollection<BsonDocument> Queue => database.GetCollection<BsonDocument>("mention_recount_jobs")
        .WithReadPreference(ReadPreference.Primary).WithReadConcern(ReadConcern.Majority)
        .WithWriteConcern(WriteConcern.WMajority);

    public async Task EnqueueAsync(long userId, Peer peer, CancellationToken ct)
    {
        var id = DialogId.Create(userId, peer).Value;
        try
        {
            await Queue.UpdateOneAsync(new BsonDocument("_id", id), new BsonDocument {
                ["$set"] = new BsonDocument { ["user"] = userId, ["peer"] = peer.PeerId,
                    ["peer_type"] = (int)peer.PeerType, ["due"] = DateTime.UtcNow },
                ["$inc"] = new BsonDocument("generation", 1L) }, new UpdateOptions { IsUpsert = true }, ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            await EnqueueAsync(userId, peer, ct);
        }
    }

    public async Task<bool> RecountAsync(BsonDocument job, CancellationToken ct)
    {
        var user = job["user"].ToInt64();
        var peer = new Peer((PeerType)job["peer_type"].AsInt32, job["peer"].ToInt64());
        var id = DialogId.Create(user, peer);
        var dialog = await aggregates.LoadAsync<DialogAggregate, DialogId>(id, ct);
        if (dialog.AntiSpamMentionOperation != null) return false;
        var version = dialog.Version;
        var state = await readState.GetAsync(user, peer);
        var count = await queries.ProcessAsync(new GetUnreadMentionsCountQuery(
            peer.PeerType == PeerType.Channel ? peer.PeerId : user, user, peer, null,
            state?.ReadMaxId ?? 0, state?.ReadIds ?? []), ct);
        await commands.PublishAsync(new SyncUnreadMentionsCountCommand(id, count, version), ct);
        dialog = await aggregates.LoadAsync<DialogAggregate, DialogId>(id, ct);
        if (dialog.MentionRecountRequired || dialog.AntiSpamMentionOperation != null) return false;
        await Queue.DeleteOneAsync(new BsonDocument { ["_id"] = job["_id"],
            ["generation"] = job["generation"] }, ct);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialized = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!initialized)
                {
                    await Queue.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys.Ascending("due")), cancellationToken: stoppingToken);
                    initialized = true;
                }
                var now = DateTime.UtcNow;
                var job = await Queue.FindOneAndUpdateAsync<BsonDocument>(
                    new BsonDocument("due", new BsonDocument("$lte", now)),
                    new BsonDocument("$set", new BsonDocument("due", now.AddSeconds(30))),
                    new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, stoppingToken);
                if (job != null) await RecountAsync(job, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Mention recount failed; durable queue will retry");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
