using MyTelegram.Messenger.Services.Mentions;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Mark mentions as read
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 SAVED_PEER_INVALID
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.readMentions"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ReadMentionsHandler(
    IQueryProcessor queryProcessor,
    IPtsHelper ptsHelper,
    IPeerHelper peerHelper,
    IChannelAppService channelAppService,
    IMentionReadStateService mentionReadStateService,
    MentionReadIntentService mentionReadIntentService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestReadMentions, MyTelegram.Schema.Messages.IAffectedHistory>
{
    /// <summary>
    /// Upper bound for a single topic-scoped read. The counter it clears is a badge, so a dialog is
    /// never expected to hold more unread mentions than this.
    /// </summary>
    private const int MaxTopicMentions = 1000;

    protected override async Task<MyTelegram.Schema.Messages.IAffectedHistory> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestReadMentions obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer.PeerType == PeerType.Channel)
        {
            var channel = await channelAppService.GetAsync((long?)peer.PeerId);
            if (channel == null)
            {
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            }

            // This call advances the channel's pts, so a non-member could inflate it and force every
            // member's client into getDifference resync loops. Require membership first; GetPeer
            // validates no access hash.
            if (await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, channel!))
            {
                return null!;
            }
        }

        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        var readState = await mentionReadStateService.GetAsync(input.UserId, peer);

        if (obj.TopMsgId.HasValue)
        {
            // A topic-scoped read must not clear the mentions of the rest of the dialog, so the
            // affected ids are marked one by one instead of moving the watermark.
            var messageIds = await queryProcessor.ProcessAsync(
                new GetUnreadMentionIdListQuery(
                    ownerPeerId,
                    input.UserId,
                    peer,
                    obj.TopMsgId,
                    readState?.ReadMaxId ?? 0,
                    readState?.ReadIds ?? [],
                    MaxTopicMentions));

            await mentionReadIntentService.ReadAsync(input.UserId, peer, messageIds.ToList());
        }
        else
        {
            // The watermark is the newest mention that is unread right now, never an open-ended
            // value: a mention arriving after this call has to light the badge up again.
            var newest = await queryProcessor.ProcessAsync(
                new GetUnreadMentionIdListQuery(
                    ownerPeerId,
                    input.UserId,
                    peer,
                    null,
                    readState?.ReadMaxId ?? 0,
                    readState?.ReadIds ?? [],
                    1));

            await mentionReadIntentService.ReadAllAsync(
                input.UserId, peer, newest.Count > 0 ? newest.First() : readState?.ReadMaxId ?? 0);
        }

        // Advance pts so the client's difference loop notices the read state on other sessions.
        var currentPts = ptsHelper.GetCachedPts(ownerPeerId);
        var pts = await ptsHelper.IncrementPtsAsync(ownerPeerId, currentPts, 1, input.PermAuthKeyId);

        return new TAffectedHistory
        {
            Pts = pts,
            PtsCount = 1,
            Offset = 0
        };
    }
}
