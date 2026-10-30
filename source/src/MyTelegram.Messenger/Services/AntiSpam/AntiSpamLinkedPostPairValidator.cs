namespace MyTelegram.Messenger.Services.AntiSpam;

/// <summary>Validates the historical per-post link, independently of today's channel discussion link.</summary>
public sealed class AntiSpamLinkedPostPairValidator(IEventPersistence persistence, IEventJsonSerializer serializer)
{
    public async Task<bool> ValidateAsync(MessageId rootId, long postChannelId, int postMessageId, CancellationToken ct)
    {
        var creation = await persistence.LoadCommittedEventsAsync(rootId, 1, 1, ct);
        if (creation.Count != 1) return false;
        var domainEvent = serializer.Deserialize<MessageAggregate, MessageId>(rootId, creation.Single());
        var item = domainEvent.GetAggregateEvent() switch
        {
            OutboxMessageCreatedEvent e => e.OutboxMessageItem,
            InboxMessageCreatedEvent e => e.InboxMessageItem,
            _ => null
        };
        return Matches(item, postChannelId, postMessageId);
    }

    public static bool Matches(MessageItem? root, long postChannelId, int postMessageId)
    {
        if (root?.FwdHeader?.ForwardFromLinkedChannel != true || postChannelId <= 0 || postMessageId <= 0)
            return false;
        var channel = root.PostChannelId ?? root.FwdHeader.SavedFromPeer?.PeerId;
        var message = root.PostMessageId ?? root.FwdHeader.SavedFromMsgId;
        return channel == postChannelId && message == postMessageId;
    }
}
