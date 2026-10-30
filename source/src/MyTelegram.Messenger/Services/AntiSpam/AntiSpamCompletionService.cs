using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.AntiSpamCompletion;
using MyTelegram.Messenger.Services.Pts;

namespace MyTelegram.Messenger.Services.AntiSpam;

/// <summary>Resumes durable deletion completion without allocating another channel pts.</summary>
public sealed class AntiSpamCompletionService(AntiSpamJobStore jobs, IChannelPtsAllocator allocator,
    IIdGenerator ids, ICommandBus commands, IAggregateStore aggregates, IMongoDatabase database,
    IObjectMessageSender sender, IQueryProcessor queries, AntiSpamDeletionEffectsService effects,
    AntiSpamMentionAdmissionService admission)
{
    private IMongoCollection<BsonDocument> DurableCollection(string name) => database
        .GetCollection<BsonDocument>(name).WithReadPreference(ReadPreference.Primary)
        .WithReadConcern(ReadConcern.Majority);

    public async Task<bool> CompleteAsync(BsonDocument job, CancellationToken ct)
    {
        var channel = job["channel"].ToInt64();
        var messageId = job["message"].AsInt32;
        var requestId = Guid.Parse(job["request"].AsString);
        // Confirmation in the job alone is not authority, including legacy jobs.
        var message = await aggregates.LoadAsync<MessageAggregate, MessageId>(MessageId.Create(channel, messageId), ct);
        if (!message.HasChannelDeletionProof || !message.IsChannelDeleted || message.ChannelDeletionRequestId != requestId)
            return false;
        // Legacy deletions with mentions predate admission fencing. Their scalar count cannot
        // safely be decremented until an explicit fenced reconciliation establishes membership.
        if (!job.Contains("admission_protocol") && job.TryGetValue("effects", out var legacyEffects) &&
            legacyEffects.IsBsonDocument && legacyEffects.AsBsonDocument.TryGetValue("mentions", out var mentions) &&
            mentions.IsBsonArray && mentions.AsBsonArray.Count != 0 &&
            !await admission.ReconcileLegacyAsync(job, ct))
            return false;

        var operation = $"antispamcompletion-{channel}-{messageId}-{requestId:N}";
        var reservation = await allocator.TryReserveForPublicationAsync(channel, operation, 1, ct);
        if (!reservation.HasValue) return false;
        var pts = reservation.Value;
        var assignment = job.GetValue("completion", BsonNull.Value);
        BsonDocument completion;
        if (assignment.IsBsonNull)
        {
            // Global sequence candidates can be discarded; only the CAS winner is ever published.
            var seq = await ids.NextLongIdAsync(IdType.GlobalSeqNo, cancellationToken: ct);
            var top = await queries.ProcessAsync(new GetTopMessageIdQuery(channel, [messageId]), ct);
            completion = await jobs.AssignCompletionAsync(job, pts, seq, ct, top);
        }
        else completion = assignment.AsBsonDocument;
        if (completion["pts"].AsInt32 != pts)
            throw new InvalidOperationException("Anti-spam reservation does not match its immutable completion");
        var date = completion["date"].AsInt32;
        var globalSeq = completion["global_seq"].ToInt64();
        await commands.PublishAsync(new CreateAntiSpamCompletionCommand(new AntiSpamCompletionId(operation),
            operation, channel, messageId, pts, globalSeq, date, completion.GetValue("new_top", 0).AsInt32), ct);

        // Wait for normal read-model projection, never write eventflow collections directly.
        // A lost event transport is repaired by the next immutable command replay.
        var saved = await DurableCollection("eventflow-updatesreadmodel")
            .Find(new BsonDocument { ["_id"] = operation, ["Pts"] = pts, ["GlobalSeqNo"] = globalSeq })
            .AnyAsync(ct);
        if (!saved) return false;
        var messages = DurableCollection("eventflow-messagereadmodel");
        if (await messages.Find(new BsonDocument("_id", MessageId.Create(channel, messageId).Value)).AnyAsync(ct))
            return false;
        var channelProjection = await DurableCollection("eventflow-channelreadmodel")
            .Find(new BsonDocument("_id", ChannelId.Create(channel).Value)).FirstOrDefaultAsync(ct);
        if (channelProjection == null) return false;
        var currentTop = channelProjection.GetValue("TopMessageId", 0).AsInt32;
        var liveTop = await queries.ProcessAsync(new GetTopMessageIdQuery(channel, [messageId]), ct);
        var currentTopExists = currentTop != 0 && await messages
            .Find(new BsonDocument("_id", MessageId.Create(channel, currentTop).Value)).AnyAsync(ct);
        if ((!currentTopExists && currentTop != 0) || (currentTop == 0 && liveTop != 0))
        {
            await commands.PublishAsync(new RepairAntiSpamCompletionTopCommand(
                new AntiSpamCompletionId(operation), currentTop, liveTop), ct);
            return false;
        }
        if (!await effects.ApplyAsync(job, pts, ct)) return false;
        await commands.PublishAsync(new PublishAntiSpamCompletionCommand(new AntiSpamCompletionId(operation)), ct);
        // Advance the channel Pts read model through its own aggregate (monotonic there and in the
        // projection), never by projecting a foreign aggregate into the PtsAggregate read model.
        await commands.PublishAsync(new UpdatePtsCommand(PtsId.Create(channel), channel, 0, pts, globalSeq, 0, messageId), ct);
        var projectedPts = await DurableCollection("eventflow-ptsreadmodel")
            .Find(new BsonDocument { ["PeerId"] = channel, ["Pts"] = new BsonDocument("$gte", pts) }).AnyAsync(ct);
        if (!projectedPts) return false;
        await allocator.MarkPublishedAsync(channel, operation, pts, ct);

        var updates = new TUpdates { Updates = new TVector<IUpdate>(new TUpdateDeleteChannelMessages {
                ChannelId = channel, Messages = new TVector<int>(messageId), Pts = pts, PtsCount = 1 }),
            Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = date, Seq = 0 };
        // At-least-once transport with the SAME pts/global sequence. Acceptance is not client receipt.
        await sender.PushMessageToPeerAsync(channel.ToChannelPeer(), updates, pts: pts, globalSeqNo: globalSeq);
        await jobs.MarkTransportAcceptedAsync(job, ct);
        await jobs.WriteConfirmedLogAsync(job, ct);
        AntiSpamMetrics.DeletionConfirmed();
        return true;
    }
}
