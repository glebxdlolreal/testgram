using EventFlow.ReadStores;
using MyTelegram.ReadModel.Impl;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Mentions;

namespace MyTelegram.Messenger.Services.AntiSpam;

/// <summary>Event-journal-backed deletion effects. No Pts allocation and no eventflow writes.</summary>
public sealed class AntiSpamDeletionEffectsService(ICommandBus commands, IAggregateStore aggregates,
    IAntiSpamEffectProofReader proofs, IMongoDatabase database, IMentionReadStateService mentionState,
    IQueryProcessor queries, AntiSpamMentionRecountService recounts, AntiSpamLinkedPostPairValidator linkedPairs)
{
    public async Task<BsonDocument> CaptureAsync(IMessageReadModel message, CancellationToken ct)
    {
        var mentions = new BsonArray();
        var peer = new Peer(PeerType.Channel, message.OwnerPeerId);
        foreach (var user in (message.MentionedUserIds ?? []).Distinct())
        {
            var dialog = await aggregates.LoadAsync<DialogAggregate, DialogId>(DialogId.Create(user, peer), ct);
            mentions.Add(new BsonDocument { ["user"] = user, ["version"] = dialog.Version, ["absent"] = dialog.IsNew });
        }
        var root = message.TopMsgId is > 0 ? message.TopMsgId.Value : message.ReplyToMsgId ?? 0;
        var metadata = new BsonDocument { ["version"] = 1, ["thread_root"] = root, ["mentions"] = mentions };
        if (root > 0)
        {
            var rootAggregate = await aggregates.LoadAsync<MessageAggregate, MessageId>(MessageId.Create(message.OwnerPeerId, root), ct);
            metadata["root_absent"] = rootAggregate.IsNew || rootAggregate.IsChannelDeleted;
            var rootMessage = await queries.ProcessAsync(new GetMessageByIdQuery(MessageId.Create(message.OwnerPeerId, root).Value), ct);
            if (rootMessage?.FwdHeader?.ForwardFromLinkedChannel == true && rootMessage.PostChannelId.HasValue && rootMessage.PostMessageId.HasValue)
            {
                if (!await linkedPairs.ValidateAsync(MessageId.Create(message.OwnerPeerId, root),
                    rootMessage.PostChannelId.Value, rootMessage.PostMessageId.Value, ct))
                    throw new InvalidOperationException("Anti-spam linked post target does not match its immutable discussion root");
                metadata["post_channel"] = rootMessage.PostChannelId.Value;
                metadata["post_message"] = rootMessage.PostMessageId.Value;
            }
        }
        return metadata;
    }

    public async Task<bool> ApplyAsync(BsonDocument job, int pts, CancellationToken ct)
    {
        if (!job.TryGetValue("effects", out var value) || !value.IsBsonDocument) return false;
        var metadata = value.AsBsonDocument;
        if (metadata.GetValue("version", 0) != 1) return false;
        var channel = job["channel"].ToInt64();
        var messageId = job["message"].AsInt32;
        var operation = $"{job["_id"].AsString}-{job["request"].AsString}";
        var root = metadata["thread_root"].AsInt32;
        if (root > 0 && !metadata.GetValue("root_absent", false).ToBoolean())
        {
            var postChannel = metadata.Contains("post_channel") ? metadata["post_channel"].ToInt64() : (long?)null;
            var postMessage = metadata.Contains("post_message") ? metadata["post_message"].AsInt32 : (int?)null;
            if (!await ApplyRootAsync(MessageId.Create(channel, root), operation, pts, postChannel, postMessage, ct)) return false;
        }
        var peer = new Peer(PeerType.Channel, channel);
        foreach (var entry in metadata["mentions"].AsBsonArray)
        {
            if (entry.AsBsonDocument.GetValue("absent", false).ToBoolean()) continue;
            var user = entry["user"].ToInt64();
            var id = DialogId.Create(user, peer);
            var dialog = await aggregates.LoadAsync<DialogAggregate, DialogId>(id, ct);
            var proof = await proofs.FindReceiptAsync(id.Value, operation, AntiSpamEffectKind.MentionDeletion, dialog.Version, ct);
            if (proof == null || !await ProjectedAsync("eventflow-dialogreadmodel", id.Value, proof.AggregateVersion, ct))
            {
                await commands.PublishAsync(new ApplyAntiSpamMentionDeletionCommand(id, operation, messageId,
                    entry["version"].AsInt32, false), ct);
                dialog = await aggregates.LoadAsync<DialogAggregate, DialogId>(id, ct);
                proof = await proofs.FindReceiptAsync(id.Value, operation, AntiSpamEffectKind.MentionDeletion, dialog.Version, ct);
                if (proof == null || !await ProjectedAsync("eventflow-dialogreadmodel", id.Value, proof.AggregateVersion, ct)) return false;
            }
            // The decrement and its receipt are durable before external state is changed. Retry
            // reads the journal, not the state marker, so a crash cannot strand the counter.
            await mentionState.MarkReadAsync(user, peer, [messageId]);
            await recounts.EnqueueAsync(user, peer, ct);
            await commands.PublishAsync(new ReleaseAntiSpamMentionAdmissionCommand(id, operation), ct);
        }
        return true;
    }

    private async Task<bool> ApplyRootAsync(MessageId id, string operation, int pts, long? postChannel, int? postMessage, CancellationToken ct)
    {
        var root = await aggregates.LoadAsync<MessageAggregate, MessageId>(id, ct);
        if (root.IsChannelDeleted) return true;
        if (root.IsNew) return false;
        string? staleSourceId = null;
        if (postChannel.HasValue && postMessage.HasValue)
        {
            if (!await linkedPairs.ValidateAsync(id, postChannel.Value, postMessage.Value, ct))
                throw new InvalidOperationException("Anti-spam captured root/post pair has no immutable forwarding proof");
            var postProjection = await database.GetCollection<BsonDocument>("eventflow-messagereadmodel")
                .WithReadPreference(ReadPreference.Primary).WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument("_id", MessageId.Create(postChannel.Value, postMessage.Value).Value)).FirstOrDefaultAsync(ct);
            if (postProjection != null && postProjection.TryGetValue("ReplyCountSourceId", out var source) &&
                source.IsString && source.AsString != id.Value)
            {
                if (await linkedPairs.ValidateAsync(new MessageId(source.AsString), postChannel.Value, postMessage.Value, ct))
                    throw new InvalidOperationException("Conflicting immutable discussion roots claim the same linked post");
                staleSourceId = source.AsString;
            }
        }
        var proof = await proofs.FindReceiptAsync(id.Value, operation, AntiSpamEffectKind.ReplyDeletion, root.Version, ct);
        if (proof != null && await RootProjectedAsync(id, proof.AggregateVersion, postChannel, postMessage, ct)) return true;
        await commands.PublishAsync(new ApplyAntiSpamReplyDeletionCommand(id, operation, pts, postChannel, postMessage, staleSourceId), ct);
        root = await aggregates.LoadAsync<MessageAggregate, MessageId>(id, ct);
        proof = await proofs.FindReceiptAsync(id.Value, operation, AntiSpamEffectKind.ReplyDeletion, root.Version, ct);
        return proof != null && await RootProjectedAsync(id, proof.AggregateVersion, postChannel, postMessage, ct);
    }

    private async Task<bool> RootProjectedAsync(MessageId root, long sequence, long? postChannel, int? postMessage, CancellationToken ct)
    {
        if (!await ProjectedAsync("eventflow-messagereadmodel", root.Value, sequence, ct, root.Value)) return false;
        if (!postChannel.HasValue || !postMessage.HasValue) return true;
        var postId = MessageId.Create(postChannel.Value, postMessage.Value);
        var post = await aggregates.LoadAsync<MessageAggregate, MessageId>(postId, ct);
        return post.IsChannelDeleted || await ProjectedAsync("eventflow-messagereadmodel", postId.Value, sequence, ct, root.Value);
    }

    private Task<bool> ProjectedAsync(string collection, string id, long sequence, CancellationToken ct, string? sourceId = null)
    {
        var filter = new BsonDocument { ["_id"] = id, ["AntiSpamEffectsSequence"] = new BsonDocument("$gte", sequence) };
        if (sourceId != null) filter["AntiSpamEffectsSourceId"] = sourceId;
        return database.GetCollection<BsonDocument>(collection).WithReadPreference(ReadPreference.Primary)
            .WithReadConcern(ReadConcern.Majority).Find(filter).AnyAsync(ct);
    }
}
