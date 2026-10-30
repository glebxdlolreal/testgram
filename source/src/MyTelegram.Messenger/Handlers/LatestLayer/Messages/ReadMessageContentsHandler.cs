using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services.Mentions;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Notifies the sender about the recipient having listened a voice message or watched a video, emitting an <a href="https://corefork.telegram.org/constructor/updateReadMessagesContents">updateReadMessagesContents</a>.
/// Also clears the @ badge of the messages, see <a href="https://corefork.telegram.org/api/mentions">mentions</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.readMessageContents"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ReadMessageContentsHandler(
    IQueryProcessor queryProcessor,
    IPtsHelper ptsHelper,
    IObjectMessageSender objectMessageSender,
    MentionReadIntentService mentionReadIntentService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestReadMessageContents, MyTelegram.Schema.Messages.IAffectedMessages>
{
    protected override async Task<MyTelegram.Schema.Messages.IAffectedMessages> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestReadMessageContents obj)
    {
        var messageIds = obj.Id?.Distinct().Where(p => p > 0).ToList() ?? [];
        if (messageIds.Count == 0)
        {
            return new TAffectedMessages { Pts = ptsHelper.GetCachedPts(input.UserId), PtsCount = 0 };
        }

        // Scoped to the caller's own box: this method takes bare message ids, so another user's
        // messages must not be reachable with a guessed id.
        var messages = await queryProcessor.ProcessAsync(
            new GetMessagesByOwnerAndMessageIdListQuery(input.UserId, messageIds));

        await NotifyGeoLiveViewedAsync(input, messages);

        var mentioned = messages
            .Where(p => p.MentionedUserIds?.Contains(input.UserId) ?? false)
            .ToList();

        foreach (var group in mentioned.GroupBy(p => new Peer(p.ToPeerType, p.ToPeerId)))
        {
            var ids = group.Select(p => p.MessageId).ToList();
            await mentionReadIntentService.ReadAsync(input.UserId, group.Key, ids);
        }

        // Advance pts so the other sessions of this user notice the read state.
        var currentPts = ptsHelper.GetCachedPts(input.UserId);
        var pts = await ptsHelper.IncrementPtsAsync(input.UserId, currentPts, 1, input.PermAuthKeyId);

        return new TAffectedMessages { Pts = pts, PtsCount = 1 };
    }

    /// <summary>
    /// Tells the sender of a still-active <a href="https://corefork.telegram.org/api/live-location">live
    /// location</a> that the recipient has opened it, so the sender's client can switch to a higher
    /// update frequency while it is being watched.
    /// </summary>
    /// <remarks>
    /// <c>peer</c> is not simply "who looked": TDLib resolves it as the dialog of the viewed message
    /// (<c>UpdatesManager::on_update</c> builds <c>{DialogId(peer), MessageId(msg_id)}</c> and matches
    /// it against the sender's own active live locations). It therefore has to be the dialog as the
    /// *sender* sees it — the viewer in a private chat, but the group itself in a group — paired with
    /// the message id in the sender's box.
    /// </remarks>
    private async Task NotifyGeoLiveViewedAsync(IRequestInput input, IReadOnlyCollection<IMessageReadModel> messages)
    {
        var now = CurrentDate;
        foreach (var message in messages)
        {
            // Only somebody else's live location can be "viewed" by this user.
            if (message.SenderUserId == input.UserId || message.SenderUserId <= 0)
            {
                continue;
            }

            if (message.Media2 is not TMessageMediaGeoLive geoLive ||
                !GeoLiveHelper.IsActive(geoLive, message.Date, now))
            {
                continue;
            }

            var dialogPeer = await ResolveSenderDialogPeerAsync(input, message);
            if (dialogPeer == null)
            {
                continue;
            }

            var updates = new TUpdates
            {
                Updates = new TVector<IUpdate>(new TUpdateGeoLiveViewed
                {
                    Peer = dialogPeer,
                    MsgId = message.SenderMessageId
                }),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = now,
                Seq = 0
            };

            await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, message.SenderUserId), updates);
        }
    }

    /// <summary>
    /// The dialog holding <paramref name="message"/> from its sender's point of view, or null when no
    /// viewed notification should be produced.
    /// </summary>
    private async Task<IPeer?> ResolveSenderDialogPeerAsync(IRequestInput input, IMessageReadModel message)
    {
        switch (message.ToPeerType)
        {
            // In a private chat the reader's copy points back at the sender, so the sender's own
            // dialog for that message is the reader.
            case PeerType.User:
                return new TPeerUser { UserId = input.UserId };

            case PeerType.Chat:
                return new TPeerChat { ChatId = message.ToPeerId };

            case PeerType.Channel:
                // A supergroup behaves like a group here. A broadcast channel is skipped: its
                // readership is unbounded and the poster would get one notification per reader.
                var channel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(message.ToPeerId));
                return channel?.MegaGroup == true
                    ? new TPeerChannel { ChannelId = message.ToPeerId }
                    : null;

            default:
                return null;
        }
    }
}
