using EventFlow.Exceptions;
using MyTelegram.Domain.Aggregates.UserConfig;
using MyTelegram.Messenger.Extensions;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// React to message.
/// Possible errors
/// Code Type Description
/// 403 ANONYMOUS_REACTIONS_DISABLED Sorry, anonymous administrators cannot leave reactions or participate in polls.
/// 400 CUSTOM_REACTIONS_TOO_MANY Too many custom reactions were specified.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 403 PREMIUM_ACCOUNT_REQUIRED A premium account is required to execute this action.
/// 400 REACTIONS_TOO_MANY The message already has exactly reactions_uniq_max reaction emojis.
/// 400 REACTION_INVALID The specified reaction is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.sendReaction"/> </c></para>
/// </summary>
internal sealed class SendReactionHandler(
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IUserAppService userAppService,
    IChannelAppService channelAppService,
    IAppConfigHelper appConfigHelper,
    ISavedReactionTagAppService savedReactionTagAppService,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSendReaction, MyTelegram.Schema.IUpdates>
{
    private const string RecentKey = "recent_reactions";
    private const int RecentReactionsMaxCount = 20;

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSendReaction obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        // For private chats, OwnerPeerId is the current user
        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        var messageReadModel = await queryProcessor.ProcessAsync(new GetMessageByPeerIdAndMessageIdQuery(ownerPeerId, obj.MsgId)) as MessageReadModel;
        if (messageReadModel == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var newUserReactions = ParseRequestedReactions(obj);
        await ValidateAsync(input, peer, messageReadModel!, newUserReactions);

        var otherReactions = messageReadModel!.RecentReactions2?
            .Where(r => r.SenderUserId != input.UserId)
            .Select(r => new Reaction(r.SenderUserId, r.Reaction is TReactionEmoji e ? e.Emoticon : null,
                r.Reaction is TReactionCustomEmoji c ? c.DocumentId : null, r.Date,
                IsPaid: r.Reaction is TReactionPaid))
            .ToList() ?? [];

        // Paid reactions are managed by messages.sendPaidReaction and must survive a plain sendReaction.
        var ownPaidReactions = messageReadModel.RecentReactions2?
            .Where(r => r.SenderUserId == input.UserId && r.Reaction is TReactionPaid)
            .Select(r => new Reaction(r.SenderUserId, null, null, r.Date, IsPaid: true))
            .ToList() ?? [];

        var previousOwnReactions = messageReadModel.RecentReactions2?
            .Where(r => r.SenderUserId == input.UserId && r.Reaction is not TReactionPaid)
            .Select(r => r.Reaction)
            .ToList() ?? [];

        var newReactions = new List<Reaction>(otherReactions);
        newReactions.AddRange(ownPaidReactions);
        newReactions.AddRange(newUserReactions.Select(r => r switch
        {
            TReactionEmoji emoji => new Reaction(input.UserId, emoji.Emoticon, null, CurrentDate, obj.Big),
            TReactionCustomEmoji custom => new Reaction(input.UserId, null, custom.DocumentId, CurrentDate, obj.Big),
            _ => throw new InvalidOperationException("Unsupported reaction type")
        }));

        var messageId = MessageId.Create(ownerPeerId, obj.MsgId);
        await PublishReactionsAsync(messageId, input, newReactions);

        // For PM inbox messages, also update the outbox copy at the sender's side
        if (peer.PeerType == PeerType.User && !messageReadModel.Out && messageReadModel.SenderMessageId > 0)
        {
            var outboxMessageId = MessageId.Create(messageReadModel.SenderUserId, messageReadModel.SenderMessageId);
            await PublishReactionsAsync(outboxMessageId, input, newReactions);
        }

        // Reactions on Saved Messages act as tags, so keep the per-tag message counters in sync.
        if (peer.PeerType == PeerType.User && peer.PeerId == input.UserId)
        {
            await savedReactionTagAppService.UpdateTagCountsAsync(input.UserId, previousOwnReactions, newUserReactions);
        }

        if (obj.AddToRecent && newUserReactions.Count > 0)
        {
            await UpdateRecentReactionsAsync(input, newUserReactions);
        }

        return null!;
    }

    private static List<IReaction> ParseRequestedReactions(MyTelegram.Schema.Messages.RequestSendReaction obj)
    {
        var reactions = new List<IReaction>();
        if (obj.Reaction == null)
        {
            return reactions;
        }

        foreach (var reaction in obj.Reaction)
        {
            switch (reaction)
            {
                case TReactionEmoji emoji:
                    if (string.IsNullOrEmpty(emoji.Emoticon))
                    {
                        RpcErrors.RpcErrors400.ReactionEmpty.ThrowRpcError();
                    }

                    reactions.Add(emoji);
                    break;
                case TReactionCustomEmoji custom:
                    reactions.Add(custom);
                    break;
                case TReactionEmpty:
                    // reactionEmpty inside the vector means "remove all", handled by an empty list.
                    break;
                default:
                    RpcErrors.RpcErrors400.ReactionInvalid.ThrowRpcError();
                    break;
            }
        }

        return reactions;
    }

    /// <summary>
    /// Enforces the client configuration limits documented at
    /// https://corefork.telegram.org/api/config#client-configuration plus the per-chat reaction whitelist.
    /// </summary>
    private async Task ValidateAsync(IRequestInput input, Peer peer, MessageReadModel messageReadModel,
        List<IReaction> newUserReactions)
    {
        if (newUserReactions.Count == 0)
        {
            // Removing all of your reactions is always allowed.
            return;
        }

        var user = await userAppService.GetAsync(input.UserId);
        if (user == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        var isPremium = user!.Premium;

        var userMax = isPremium
            ? appConfigHelper.GetInt32("reactions_user_max_premium", 3)
            : appConfigHelper.GetInt32("reactions_user_max_default", 1);
        if (newUserReactions.Count > userMax)
        {
            if (!isPremium)
            {
                RpcErrors.RpcErrors403.PremiumAccountRequired.ThrowRpcError();
            }

            RpcErrors.RpcErrors400.ReactionsTooMany.ThrowRpcError();
        }

        var customReactions = newUserReactions.OfType<TReactionCustomEmoji>().ToList();
        if (customReactions.Count > 0 && !isPremium)
        {
            RpcErrors.RpcErrors403.PremiumAccountRequired.ThrowRpcError();
        }

        if (customReactions.Count > userMax)
        {
            RpcErrors.RpcErrors400.CustomReactionsTooMany.ThrowRpcError();
        }

        await ValidateChatWhitelistAsync(input, peer, newUserReactions, customReactions.Count > 0);
        ValidateUniqueReactionLimit(input, messageReadModel, newUserReactions);
    }

    private async Task ValidateChatWhitelistAsync(IRequestInput input, Peer peer, List<IReaction> newUserReactions,
        bool hasCustomReactions)
    {
        if (peer.PeerType != PeerType.Channel)
        {
            return;
        }

        var channel = await channelAppService.GetAsync(peer.PeerId);
        if (channel == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        // Anonymous administrators have no peer to attribute the reaction to.
        var admin = channel!.AdminList?.FirstOrDefault(p => p.UserId == input.UserId);
        if (admin?.AdminRights?.Anonymous == true)
        {
            RpcErrors.RpcErrors403.AnonymousReactionsDisabled.ThrowRpcError();
        }

        var channelFull = await channelAppService.GetChannelFullAsync(peer.PeerId);
        if (channelFull == null)
        {
            return;
        }

        switch (channelFull.ReactionType)
        {
            // Not explicitly configured: everything is allowed, matching ChannelFullMapper.
            case ReactionType.ReactionNone:
                return;

            case ReactionType.ReactionAll:
                if (hasCustomReactions && !channelFull.AllowCustomReaction)
                {
                    RpcErrors.RpcErrors400.ReactionInvalid.ThrowRpcError();
                }

                return;

            case ReactionType.ReactionSome:
            {
                // Custom emoji are never part of a chatReactionsSome whitelist.
                if (hasCustomReactions)
                {
                    RpcErrors.RpcErrors400.ReactionInvalid.ThrowRpcError();
                }

                var allowed = channelFull.AvailableReactions;
                foreach (var reaction in newUserReactions.OfType<TReactionEmoji>())
                {
                    if (allowed == null || !allowed.Contains(reaction.Emoticon))
                    {
                        RpcErrors.RpcErrors400.ReactionInvalid.ThrowRpcError();
                    }
                }

                return;
            }
        }
    }

    private void ValidateUniqueReactionLimit(IRequestInput input, MessageReadModel messageReadModel,
        List<IReaction> newUserReactions)
    {
        var uniqMax = appConfigHelper.GetInt32("reactions_uniq_max", 11);

        // Count distinct reactions that will remain on the message: everyone else's, plus ours.
        var reactionIds = new HashSet<long>();
        foreach (var existing in messageReadModel.RecentReactions2 ?? [])
        {
            if (existing.SenderUserId == input.UserId && existing.Reaction is not TReactionPaid)
            {
                // Our own non-paid reactions are being replaced by this request.
                continue;
            }

            reactionIds.Add(existing.Reaction.GetReactionId());
        }

        foreach (var reaction in newUserReactions)
        {
            reactionIds.Add(reaction.GetReactionId());
        }

        if (reactionIds.Count > uniqMax)
        {
            RpcErrors.RpcErrors400.ReactionsTooMany.ThrowRpcError();
        }
    }

    private async Task PublishReactionsAsync(MessageId messageId, IRequestInput input, List<Reaction> reactions)
    {
        try
        {
            await commandBus.PublishAsync(new UpdateMessageReactionsCommand(messageId, input.ToRequestInfo(), reactions));
        }
        catch (DomainError)
        {
            // Message exists in the read model but its aggregate was never created (legacy message).
        }
    }

    private async Task UpdateRecentReactionsAsync(IRequestInput input, List<IReaction> newUserReactions)
    {
        var recentConfig = await queryProcessor.ProcessAsync(new GetUserConfigByKeyQuery(input.UserId, RecentKey));
        var existing = recentConfig?.Value?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [];

        foreach (var reaction in newUserReactions)
        {
            if (reaction is not TReactionEmoji emoji) continue;
            existing.Remove(emoji.Emoticon);
            existing.Insert(0, emoji.Emoticon);
        }

        if (existing.Count > RecentReactionsMaxCount)
        {
            existing = existing.Take(RecentReactionsMaxCount).ToList();
        }

        var configId = UserConfigId.Create(input.UserId, RecentKey);
        await commandBus.PublishAsync(new UpdateUserConfigCommand(configId, input.ToRequestInfo(), input.UserId, RecentKey, string.Join(',', existing)));

        // Tell the user's other sessions the recent list changed.
        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdateRecentReactions()),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = CurrentDate,
            Seq = 0
        };
        await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, input.UserId), updates,
            excludeAuthKeyId: input.PermAuthKeyId);
    }
}
