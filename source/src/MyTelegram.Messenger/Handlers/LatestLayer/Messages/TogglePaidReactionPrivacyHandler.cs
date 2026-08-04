using EventFlow.Exceptions;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Changes the privacy of already sent <a href="https://corefork.telegram.org/api/reactions#paid-reactions">paid reactions</a> on a specific message.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 REACTION_EMPTY Empty reaction provided.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.togglePaidReactionPrivacy"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class TogglePaidReactionPrivacyHandler(
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IPaidReactionPrivacyAppService paidReactionPrivacyAppService,
    IAccessHashHelper2 accessHashHelper,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestTogglePaidReactionPrivacy, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestTogglePaidReactionPrivacy obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer.PeerType != PeerType.Channel)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var messageReadModel = await queryProcessor.ProcessAsync(
            new GetMessageByPeerIdAndMessageIdQuery(peer.PeerId, obj.MsgId)) as MessageReadModel;
        if (messageReadModel == null)
        {
            RpcErrors.RpcErrors400.MsgIdInvalid.ThrowRpcError();
        }

        // You can only change the privacy of paid reactions you actually sent.
        var ownPaidCount = messageReadModel!.RecentReactions2?
            .Count(r => r.SenderUserId == input.UserId && r.Reaction is TReactionPaid) ?? 0;
        if (ownPaidCount == 0)
        {
            RpcErrors.RpcErrors400.ReactionEmpty.ThrowRpcError();
        }

        var setting = PaidReactionPrivacyConverter.FromTl(obj.Private, peerHelper, input.UserId);
        await paidReactionPrivacyAppService.SetForMessageAsync(input.UserId, peer.PeerId, obj.MsgId, setting);

        // Rebuild the message's reaction list so the top reactors leaderboard picks up the new privacy.
        await RepublishReactionsAsync(input, peer, messageReadModel, obj.MsgId, setting, ownPaidCount);

        var privacyUpdates = new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdatePaidReactionPrivacy
            {
                Private = PaidReactionPrivacyConverter.ToTl(setting, input, accessHashHelper)
            }),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = CurrentDate,
            Seq = 0
        };
        await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, input.UserId), privacyUpdates,
            excludeAuthKeyId: input.PermAuthKeyId);

        return new TBoolTrue();
    }

    private async Task RepublishReactionsAsync(IRequestInput input, Peer peer, MessageReadModel messageReadModel,
        int msgId, PaidReactionPrivacySetting setting, int ownPaidCount)
    {
        var anonymous = setting.Type != PaidReactionPrivacyType.Default;
        var anonymousPeerId = setting.Type == PaidReactionPrivacyType.Peer ? setting.PeerId : 0;

        var reactions = messageReadModel.RecentReactions2?
            .Where(r => !(r.SenderUserId == input.UserId && r.Reaction is TReactionPaid))
            .Select(r => new Reaction(
                r.SenderUserId,
                r.Reaction is TReactionEmoji e ? e.Emoticon : null,
                r.Reaction is TReactionCustomEmoji c ? c.DocumentId : null,
                r.Date,
                IsPaid: r.Reaction is TReactionPaid))
            .ToList() ?? [];

        for (var i = 0; i < ownPaidCount; i++)
        {
            reactions.Add(new Reaction(input.UserId, null, null, CurrentDate, IsPaid: true,
                Anonymous: anonymous, AnonymousPeerId: anonymousPeerId));
        }

        try
        {
            await commandBus.PublishAsync(new UpdateMessageReactionsCommand(
                MessageId.Create(peer.PeerId, msgId), input.ToRequestInfo(), reactions));
        }
        catch (DomainError)
        {
            // Message exists in the read model but its aggregate was never created (legacy message).
        }
    }
}
