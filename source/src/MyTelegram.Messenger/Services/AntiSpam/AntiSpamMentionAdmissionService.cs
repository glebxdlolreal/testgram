using MongoDB.Bson;
using MyTelegram.Messenger.Services.Mentions;

namespace MyTelegram.Messenger.Services.AntiSpam;

/// <summary>Coordinates bounded dialog reservations with the durable job's authorization fence.</summary>
public sealed class AntiSpamMentionAdmissionService(IAggregateStore aggregates, ICommandBus commands,
    IAntiSpamEffectProofReader proofs, AntiSpamJobStore jobs, AntiSpamMentionRecountService recounts,
    IMentionReadStateService readState, IQueryProcessor queries)
{
    public static string Operation(BsonDocument job) => $"{job["_id"].AsString}-{job["request"].AsString}";

    public static IEnumerable<(long User, DialogId Id)> Targets(BsonDocument job)
    {
        if (!job.TryGetValue("effects", out var effects)) yield break;
        var peer = new Peer(PeerType.Channel, job["channel"].ToInt64());
        foreach (var user in effects["mentions"].AsBsonArray
                     .Select(x => x["user"].ToInt64()).Distinct().Order())
            yield return (user, DialogId.Create(user, peer));
    }

    public async Task<bool> AcquireAsync(BsonDocument job, CancellationToken ct)
    {
        var operation = Operation(job);
        foreach (var (_, id) in Targets(job))
        {
            var dialog = await aggregates.LoadAsync<DialogAggregate, DialogId>(id, ct);
            if (dialog.IsNew) continue;
            if (dialog.AntiSpamMentionOperation == operation) continue;
            if (dialog.AntiSpamMentionOperation != null) return false;
            var version = dialog.Version;
            var proof = await proofs.GetMentionConsumptionAsync(id.Value, job["message"].AsInt32, version, ct);
            if (proof.ThroughVersion != version) throw new InvalidOperationException("Mention proof version mismatch");
            // Never move this authorization before the version capture, including on retries.
            if (!await jobs.AuthorizeAdmissionAsync(job, ct)) return false;
            await commands.PublishAsync(new TryAdmitAntiSpamMentionCommand(id, operation,
                job["message"].AsInt32, version, !proof.CreatedAtVersion.HasValue || proof.ConsumedAtVersion.HasValue), ct);
            dialog = await aggregates.LoadAsync<DialogAggregate, DialogId>(id, ct);
            if (dialog.AntiSpamMentionOperation != operation) return false;
        }
        // Empty target lists still require authorization before dispatch.
        return await jobs.AuthorizeAdmissionAsync(job, ct);
    }

    public async Task<bool> ReconcileLegacyAsync(BsonDocument job, CancellationToken ct)
    {
        if (!job.TryGetValue("effects", out var effectsValue) || !effectsValue.IsBsonDocument ||
            !effectsValue.AsBsonDocument.TryGetValue("mentions", out var mentionsValue) || !mentionsValue.IsBsonArray)
            return false;

        var operation = Operation(job);
        var peer = new Peer(PeerType.Channel, job["channel"].ToInt64());
        var messageId = job["message"].AsInt32;
        foreach (var entry in mentionsValue.AsBsonArray)
        {
            if (!entry.IsBsonDocument || !entry.AsBsonDocument.TryGetValue("user", out var userValue) ||
                !userValue.IsNumeric)
                return false;
            var user = userValue.ToInt64();
            var id = DialogId.Create(user, peer);
            var dialog = await aggregates.LoadAsync<DialogAggregate, DialogId>(id, ct);
            if (dialog.IsNew || dialog.MentionReadOperation != null) return false;
            if (dialog.AntiSpamMentionOperation != operation || dialog.AntiSpamMentionMessageId != messageId)
                return false;

            var receipt = await proofs.FindReceiptAsync(id.Value, operation, AntiSpamEffectKind.MentionDeletion,
                dialog.Version, ct);
            if (receipt == null)
            {
                var version = dialog.Version;
                var state = await readState.GetAsync(user, peer);
                var count = await queries.ProcessAsync(new GetUnreadMentionsCountQuery(
                    peer.PeerId, user, peer, null, state?.ReadMaxId ?? 0, state?.ReadIds ?? []), ct);
                await commands.PublishAsync(new ReconcileAntiSpamMentionDeletionCommand(id, operation, messageId,
                    version, count), ct);
                dialog = await aggregates.LoadAsync<DialogAggregate, DialogId>(id, ct);
                receipt = await proofs.FindReceiptAsync(id.Value, operation, AntiSpamEffectKind.MentionDeletion,
                    dialog.Version, ct);
                if (receipt == null) return false;
            }
        }
        return true;
    }

    public async Task CloseAsync(BsonDocument job, CancellationToken ct)
    {
        foreach (var (user, id) in Targets(job))
        {
            // Includes targets never acquired: closes late in-flight admission versions.
            await commands.PublishAsync(new ReleaseAntiSpamMentionAdmissionCommand(id, Operation(job)), ct);
            await recounts.EnqueueAsync(user, new Peer(PeerType.Channel, job["channel"].ToInt64()), ct);
        }
    }
}
