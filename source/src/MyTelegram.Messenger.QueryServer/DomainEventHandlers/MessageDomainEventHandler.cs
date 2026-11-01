using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Interfaces;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

public partial class MessageDomainEventHandler(
    IObjectMessageSender objectMessageSender,
    ICommandBus commandBus,
    IIdGenerator idGenerator,
    IAckCacheService ackCacheService,
    IChatEventCacheHelper chatEventCacheHelper,
    ILogger<MessageDomainEventHandler> logger,
    IQueryProcessor queryProcessor,
    IUpdatesConverterService updatesConverterService,
    IMessageConverterService messageConverterService,
    MyTelegram.Messenger.Services.Business.IBusinessBotNotifier businessBotNotifier,
    IUserConverterService userConverterService,
    IChatConverterService chatConverterService,
    IChannelAppService channelAppService,
    ISendMessageConverterService sendMessageConverterService,
    IEditMessageConverterService editMessageConverterService,
    IInviteToChannelConverterService inviteToChannelConverterService,
    IJoinChannelConverterService joinChannelConverterService,
    IPhotoAppService photoAppService,
    MessagePushDataBuilder pushDataBuilder,
    IMongoDatabase mongoDatabase)
    : DomainEventHandlerBase(objectMessageSender,
            commandBus,
            idGenerator,
            ackCacheService),
        ISubscribeSynchronousTo<EditMessageSaga, EditMessageSagaId, OutboxMessageEditCompletedSagaEvent>,
        ISubscribeSynchronousTo<EditMessageSaga, EditMessageSagaId, InboxMessageEditCompletedSagaEvent>,
        ISubscribeSynchronousTo<MessageAggregate, MessageId, ChannelMessagePinnedEvent>,
        ISubscribeSynchronousTo<MessageAggregate, MessageId, MessageReplyUpdatedEvent>,
        ISubscribeSynchronousTo<SendMessageSaga, SendMessageSagaId, SendOutboxMessageCompletedSagaEvent>,
        ISubscribeSynchronousTo<SendMessageSaga, SendMessageSagaId, ReceiveInboxMessageCompletedSagaEvent>
{
    public Task HandleAsync(
        IDomainEvent<EditMessageSaga, EditMessageSagaId, InboxMessageEditCompletedSagaEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var toPeer = domainEvent.AggregateEvent.NewMessageItem.OwnerPeer;
        var updates = editMessageConverterService.ToEditMessageUpdates(domainEvent.AggregateEvent);
        return PushUpdatesToPeerAsync(toPeer,
            updates,
            pts: domainEvent.AggregateEvent.NewMessageItem.Pts,
            updatesType: UpdatesType.Updates
        );
    }

    public async Task HandleAsync(
        IDomainEvent<EditMessageSaga, EditMessageSagaId, OutboxMessageEditCompletedSagaEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        if (domainEvent.AggregateEvent.NewMessageItem.QuickReplyItem != null)
        {
            await HandleEditQuickReplyMessageAsync(domainEvent.AggregateEvent);
            return;
        }

        var updates = editMessageConverterService.ToEditMessageUpdates(domainEvent.AggregateEvent.RequestInfo.UserId, domainEvent.AggregateEvent, domainEvent.AggregateEvent.RequestInfo.Layer);
        var updatesForOtherParticipant = editMessageConverterService.ToEditMessageUpdates(-1, domainEvent.AggregateEvent, 0);

        var businessResult = await businessBotNotifier.BuildSentResultAsync(domainEvent.AggregateEvent.RequestInfo,
            [domainEvent.AggregateEvent.NewMessageItem]);
        if (businessResult != null)
        {
            await SendRpcMessageToClientAsync(domainEvent.AggregateEvent.RequestInfo, businessResult);
        }
        else
        {
            await SendRpcMessageToClientAsync(domainEvent.AggregateEvent.RequestInfo,
                updates,
                domainEvent.AggregateEvent.RequestInfo.UserId,
                domainEvent.AggregateEvent.NewMessageItem.Pts,
                domainEvent.AggregateEvent.NewMessageItem.ToPeer.PeerType
            );
        }
        await PushUpdatesToPeerAsync(
            domainEvent.AggregateEvent.NewMessageItem.SenderUserId.ToUserPeer(),
            updatesForOtherParticipant,
            domainEvent.AggregateEvent.RequestInfo.AuthKeyId,
            pts: domainEvent.AggregateEvent.NewMessageItem.Pts,
            updatesType: UpdatesType.Updates);

        // Channel message shares the same message,edit out message should notify channel member
        if (domainEvent.AggregateEvent.NewMessageItem.ToPeer.PeerType == PeerType.Channel)
        {
            var channelEditUpdates = editMessageConverterService.ToEditMessageUpdates(-1, domainEvent.AggregateEvent, 0);

            await PushUpdatesToPeerAsync(domainEvent.AggregateEvent.NewMessageItem.ToPeer,
                channelEditUpdates,
                pts: domainEvent.AggregateEvent.NewMessageItem.Pts);
        }
    }

    public Task HandleAsync(IDomainEvent<MessageAggregate, MessageId, ChannelMessagePinnedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task HandleAsync(IDomainEvent<MessageAggregate, MessageId, MessageReplyUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var messageReadModel = await queryProcessor.ProcessAsync(new GetMessageByIdQuery(MessageId
                .Create(domainEvent.AggregateEvent.OwnerChannelId, domainEvent.AggregateEvent.MessageId).Value),
            cancellationToken);

        if (messageReadModel != null)
        {
            //var message = messageLayeredService.Converter.ToMessage(messageReadModel, null, null, 0);

            var message = messageConverterService.ToMessage(messageReadModel.SenderUserId, messageReadModel);
            if (message is TMessage tMessage)
            {
                tMessage.EditDate = DateTime.UtcNow.ToTimestamp();
                tMessage.EditHide = true;
            }

            var update = new TUpdateEditChannelMessage
            {
                Message = message,
                Pts = domainEvent.AggregateEvent.Pts,
                PtsCount = 1
            };
            var updates = new TUpdates
            {
                Updates = new TVector<IUpdate>(update),
                Users = [],
                Chats = [],
                Date = DateTime.UtcNow.ToTimestamp()
            };

            await PushUpdatesToPeerAsync(domainEvent.AggregateEvent.OwnerChannelId.ToChannelPeer(), updates);
        }
    }

    private Task<ILayeredUser> GetUserAsync(IRequestWithAccessHashKeyId request, long userId/*, long selfUserId*/)
    {
        return userConverterService.GetUserAsync(request, userId);
    }

    private async Task HandleCreateChannelAsync(SendOutboxMessageCompletedSagaEvent aggregateEvent)
    {
        var item = aggregateEvent.MessageItem;
        if (chatEventCacheHelper.TryRemoveChannelCreatedEvent(item.ToPeer.PeerId,
                out var eventData))
        {
            var channelId = eventData.ChannelId;
            //var updates = updatesLayeredService.GetConverter(aggregateEvent.RequestInfo.Layer)
            //    .ToCreateChannelUpdates(eventData, aggregateEvent);
            var updates = await updatesConverterService.ToCreateChannelUpdatesAsync(eventData, aggregateEvent, true, aggregateEvent.RequestInfo.Layer);
            var updatesForSelfOtherDevices = await updatesConverterService.ToCreateChannelUpdatesAsync(eventData, aggregateEvent, true, 0);
            IObject rpcData = updates;

            if (item.MessageSubType == MessageSubType.AutoCreateChannelFromChat)
            {
                var invitedUsers = new TInvitedUsers
                {
                    Updates = updates,
                    // Users messages.createChat had to skip for privacy reasons.
                    MissingInvitees = [.. eventData.MissingInviteeUserIds.Select(userId => new TMissingInvitee { UserId = userId })]
                };

                rpcData = invitedUsers;
            }

            await SendRpcMessageToClientAsync(aggregateEvent.RequestInfo,
                rpcData,
                item.SenderPeer.PeerId
            );

            await PushUpdatesToChannelSingleMemberAsync(channelId, item.SenderPeer,
                updatesForSelfOtherDevices,
                aggregateEvent.RequestInfo.AuthKeyId,
                pts: item.Pts
            );
        }
        else
        {
            logger.LogWarning("Cannot find cached channel info, channelId: {ChannelId}",
                item.ToPeer.PeerId);
        }
    }

    /// <summary>
    /// A message sent into a <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">quick
    /// reply shortcut</a>. tdlib accepts the answer only as <c>updates</c> holding an <c>updateMessageID</c>
    /// per random_id, the stored messages as <c>updateQuickReplyMessage</c>, and exactly one
    /// <c>updateNewQuickReply</c> when the shortcut was created by this send.
    /// </summary>
    private async Task HandleSendQuickReplyMessageAsync(SendOutboxMessageCompletedSagaEvent aggregateEvent)
    {
        var items = aggregateEvent.MessageItems.Where(p => p.QuickReplyItem != null).ToList();
        var requestInfo = aggregateEvent.RequestInfo;
        var selfUserId = requestInfo.UserId;
        var date = DateTime.UtcNow.ToTimestamp();

        TUpdates Build(bool withMessageIds, int layer)
        {
            var updates = new List<IUpdate>();
            if (withMessageIds)
            {
                updates.AddRange(items.Select(p => (IUpdate)new TUpdateMessageID
                {
                    Id = p.MessageId,
                    RandomId = p.RandomId
                }));
            }

            var created = items.FirstOrDefault(p => p.QuickReplyItem!.ShouldCreateNewQuickReply);
            if (created != null)
            {
                updates.Add(new TUpdateNewQuickReply
                {
                    QuickReply = new TQuickReply
                    {
                        ShortcutId = created.QuickReplyItem!.ShortcutId,
                        Shortcut = created.QuickReplyItem.Title,
                        TopMessage = items.Min(p => p.MessageId),
                        Count = items.Count
                    }
                });
            }

            updates.AddRange(items.Select(p => (IUpdate)new TUpdateQuickReplyMessage
            {
                Message = messageConverterService.ToMessage(selfUserId, p, layer: layer)
            }));

            return new TUpdates
            {
                Updates = new TVector<IUpdate>(updates),
                Users = [],
                Chats = [],
                Date = date,
                Seq = 0
            };
        }

        if (requestInfo.ReqMsgId != 0)
        {
            await SendRpcMessageToClientAsync(requestInfo, Build(true, requestInfo.Layer));
        }

        // Other sessions only reload what changed; tdlib re-fetches shortcuts on start anyway, so these
        // are not stored for getDifference.
        await PushUpdatesToPeerAsync(selfUserId.ToUserPeer(), Build(false, 0), requestInfo.PermAuthKeyId,
            skipSaveUpdates: true);
    }

    private async Task HandleEditQuickReplyMessageAsync(OutboxMessageEditCompletedSagaEvent aggregateEvent)
    {
        var updates = editMessageConverterService.ToEditQuickReplyMessageUpdates(aggregateEvent, aggregateEvent.RequestInfo.Layer);
        var updatesForSelfOtherDevices = editMessageConverterService.ToEditQuickReplyMessageUpdates(aggregateEvent, 0);
        await SendRpcMessageToClientAsync(aggregateEvent.RequestInfo, updates);
        await PushUpdatesToPeerAsync(aggregateEvent.RequestInfo.UserId.ToUserPeer(), updatesForSelfOtherDevices,
            aggregateEvent.RequestInfo.PermAuthKeyId);
    }
    private async Task HandleForwardMessageAsync(ReceiveInboxMessageCompletedSagaEvent aggregateEvent)
    {
        var item = aggregateEvent.MessageItem;

        var updates = updatesConverterService.ToInboxForwardMessageUpdates(aggregateEvent);
        if (aggregateEvent.MessageItem.FwdHeader?.FromId?.PeerType == PeerType.Channel)
        {
            if (updates is TUpdates tUpdates)
            {
                var channelId = aggregateEvent.MessageItem.FwdHeader.FromId.PeerId;
                var channel = await chatConverterService.GetChannelAsync(RequestInfo.Empty, channelId, true, null);

                tUpdates.Chats.Add(channel);
            }
        }
        await PushUpdatesToPeerAsync(item.OwnerPeer,
            updates,
            pts: item.Pts);
    }

    private async Task HandleInviteToChannelAsync(SendOutboxMessageCompletedSagaEvent aggregateEvent)
    {
        var item = aggregateEvent.MessageItem;
        var invitedUsers = inviteToChannelConverterService.ToInvitedUsers(aggregateEvent);
        var invitedUserIds = new List<long>();
        if (item.MessageAction is TMessageActionChatAddUser messageActionChatAddUser)
        {
            invitedUserIds = messageActionChatAddUser.Users.ToList();
            invitedUserIds.Add(item.SenderUserId);
        }

        // Invitees the invite saga had to drop for privacy reasons are reported alongside the
        // successful ones instead of failing the whole request.
        if (invitedUsers is TInvitedUsers tInvitedUsers &&
            chatEventCacheHelper.TryRemoveMissingInvitees(aggregateEvent.RequestInfo.RequestId, out var missingInviteeUserIds))
        {
            tInvitedUsers.MissingInvitees = [.. missingInviteeUserIds.Select(userId => new TMissingInvitee { UserId = userId })];
        }

        await UpdateChannelAndUserAsync(aggregateEvent.RequestInfo, invitedUsers.Updates, item.ToPeer.PeerId, invitedUserIds, layer: aggregateEvent.RequestInfo.Layer);
        await SendRpcMessageToClientAsync(aggregateEvent.RequestInfo,
            invitedUsers,
            item.SenderPeer.PeerId);
        var updatesForSelfOtherDevices = inviteToChannelConverterService.ToInviteToChannelUpdates(aggregateEvent, 0);
        await UpdateChannelAndUserAsync(aggregateEvent.RequestInfo, updatesForSelfOtherDevices, item.ToPeer.PeerId, invitedUserIds, layer: aggregateEvent.RequestInfo.Layer);

        await PushMessageToPeerAsync(item.ToPeer, updatesForSelfOtherDevices, excludeAuthKeyId: aggregateEvent.RequestInfo.PermAuthKeyId);


        var updatesForChannelMember = inviteToChannelConverterService.ToInviteToChannelUpdates(aggregateEvent, 0);
        await UpdateChannelAndUserAsync(RequestInfo.Empty with { UserId = -1 }, updatesForChannelMember, item.ToPeer.PeerId, invitedUserIds);

        foreach (var userId in invitedUserIds)
        {
            if (userId == aggregateEvent.RequestInfo.UserId)
            {
                continue;
            }

            await PushUpdatesToChannelSingleMemberAsync(item.ToPeer.PeerId, userId.ToUserPeer(),
                updatesForChannelMember);
        }
        if (item.Post)
        {
            return;
        }
        await PushMessageToPeerAsync(item.ToPeer, updatesForChannelMember, excludeUserId: aggregateEvent.RequestInfo.UserId);
    }

    private async Task HandleReceiveMessageAsync(ReceiveInboxMessageCompletedSagaEvent aggregateEvent)
    {
        var updates = sendMessageConverterService.ToUpdates(aggregateEvent);
        var item = aggregateEvent.MessageItems.Last();

        // tdesktop's `Updates::applyUpdatesNoPtsCheck` (api_updates.cpp) routes a
        // `updateShortMessage` / `updateShortChatMessage` push through `DataIsLoaded`
        // which short-circuits to `getDifference()` whenever the sender or chat is
        // not yet cached on the client. The Desktop fork in particular ends up
        // silently dropping the message render until the next manual refresh.
        // Promote inbox short-form updates to a full `TUpdates` with the sender
        // resolved inline so clients can render immediately regardless of cache
        // state.
        updates = await EnrichInboxUpdatesAsync(aggregateEvent, updates);

        // Build a PUSH payload for the recipient(s) so offline devices still get notified.
        // Only personal (user-to-user) messages produce a push here; group/channel pushes are
        // generated in their respective handlers.
        PushData? pushData = null;
        if (item.OwnerPeer.PeerType == PeerType.User && item.ToPeer.PeerType == PeerType.User)
        {
            pushData = await pushDataBuilder.BuildForPersonalMessageAsync(item);
        }

        await PushUpdatesToPeerAsync(item.OwnerPeer,
            updates,
            pts: item.Pts,
            senderUserId: item.SenderPeer.PeerId,
            pushData: pushData
        );

        // Update MessageId in star-gift-offers by RandomId for recipient's message
        if (item.MessageAction is TMessageActionStarGiftPurchaseOffer && item.RandomId != 0)
        {
            var offerCol = mongoDatabase.GetCollection<StarGiftOfferDocument>("star-gift-offers");
            // Update recipient's message id (in case sender's id was already stored)
            var updateResult = await offerCol.UpdateOneAsync(
                Builders<StarGiftOfferDocument>.Filter.Eq(d => d.RandomId, item.RandomId),
                Builders<StarGiftOfferDocument>.Update.Set(d => d.MessageId, item.MessageId)
            );
        }
    }

    private async Task<IUpdates> EnrichInboxUpdatesAsync(
        ReceiveInboxMessageCompletedSagaEvent aggregateEvent,
        IUpdates updates)
    {
        // Already a full TUpdates (media / forward / service / grouped) -- just make
        // sure the sender's TUser is embedded so tdesktop doesn't trigger a
        // getDifference round-trip when the peer hasn't been cached yet.
        if (updates is TUpdates tUpdates)
        {
            await EnsureSenderUserAsync(aggregateEvent, tUpdates);
            return tUpdates;
        }

        // Short-form updates need to be promoted to full TUpdates with the
        // sender resolved so the client can render the message without a
        // round-trip.
        var item = aggregateEvent.MessageItems.Last();
        var senderPeer = item.SenderPeer;
        if (senderPeer.PeerType != PeerType.User && senderPeer.PeerType != PeerType.Self &&
            senderPeer.PeerType != PeerType.Chat)
        {
            return updates;
        }

        var fullUpdates = new TUpdates
        {
            Chats = [],
            Users = [],
            Date = item.Date,
            Seq = 0,
            Updates = new TVector<IUpdate>(aggregateEvent.MessageItems.Select(p => (IUpdate)new TUpdateNewMessage
            {
                Pts = p.Pts,
                PtsCount = 1,
                Message = messageConverterService.ToMessage(0, p)
            }))
        };

        if (item.MessageActionType == MessageActionType.SetMessagesTtl &&
            item.MessageAction is TMessageActionSetMessagesTTL messageActionSetMessagesTtl)
        {
            fullUpdates.Updates.Insert(0, new TUpdatePeerHistoryTTL
            {
                Peer = item.ToPeer.ToPeer(),
                TtlPeriod = messageActionSetMessagesTtl.Period == 0 ? null : messageActionSetMessagesTtl.Period
            });
        }

        await EnsureSenderUserAsync(aggregateEvent, fullUpdates);
        return fullUpdates;
    }

    private async Task EnsureSenderUserAsync(
        ReceiveInboxMessageCompletedSagaEvent aggregateEvent,
        TUpdates tUpdates)
    {
        var senderUserId = aggregateEvent.MessageItem.SenderPeer.PeerType switch
        {
            PeerType.User or PeerType.Self => aggregateEvent.MessageItem.SenderPeer.PeerId,
            PeerType.Chat => aggregateEvent.MessageItem.SenderUserId,
            _ => 0L
        };

        if (senderUserId <= 0)
        {
            return;
        }

        if (tUpdates.Users.Any(u => u.Id == senderUserId))
        {
            return;
        }

        try
        {
            // ReceiveInboxMessageCompletedSagaEvent has no RequestInfo (the recipient is
            // not the request initiator), so use RequestInfo.Empty -- GetUserAsync only
            // needs the user id and resolves the rest from read models. Access hash is
            // regenerated client-side by the embedded user object inside the push.
            var user = await userConverterService.GetUserAsync(
                RequestInfo.Empty,
                senderUserId,
                skipSetContactProperties: false,
                skipPrivacy: false);
            tUpdates.Users.Add(user);
        }
        catch (Exception ex)
        {
            // Don't break the inbox push if the sender can't be resolved -- log and fall
            // back to the original short-form push behavior.
            logger.LogWarning(ex,
                "Failed to embed sender {SenderUserId} in inbox updates for owner {OwnerUserId}",
                senderUserId, aggregateEvent.MessageItem.OwnerPeer.PeerId);
        }
    }

    private Task HandleReceiveMessageCompletedAsync(ReceiveInboxMessageCompletedSagaEvent aggregateEvent)
    {
        var item = aggregateEvent.MessageItem;
        return item.MessageSubType switch
        {
            MessageSubType.UpdatePinnedMessage => HandleUpdatePinnedMessageAsync(aggregateEvent),
            MessageSubType.ForwardMessage => HandleForwardMessageAsync(aggregateEvent),
            _ => HandleReceiveMessageAsync(aggregateEvent)
        };
    }

    private async Task HandleSendMessageAsync(SendOutboxMessageCompletedSagaEvent aggregateEvent)
    {
        var item = aggregateEvent.MessageItem;
        if (item.ToPeer.PeerType == PeerType.Channel)
        {
            await HandleSendMessageToChannelAsync(aggregateEvent);
            return;
        }

        var layer = aggregateEvent.RequestInfo.Layer;
        if (aggregateEvent.RequestInfo.ReqMsgId == 0 || item.MessageSubType == MessageSubType.PhoneCall)
        {
            layer = 0;
        }

        var selfUpdates = sendMessageConverterService.ToUpdates(aggregateEvent, layer);
        var selfOtherDeviceUpdates = sendMessageConverterService.ToSelfOtherDeviceUpdates(aggregateEvent, 0);

        var updatesType = UpdatesType.Updates;
        if (item.MessageSubType == MessageSubType.Normal ||
            item.MessageSubType == MessageSubType.ForwardMessage)
        {
            updatesType = UpdatesType.NewMessages;
        }

        // Sent by a connected business bot for its owner: the bot is answered in the business form, the
        // owner's sessions get the ordinary updates below.
        var businessResult = aggregateEvent.RequestInfo.ReqMsgId != 0
            ? await businessBotNotifier.BuildSentResultAsync(aggregateEvent.RequestInfo, aggregateEvent.MessageItems)
            : null;
        if (businessResult != null)
        {
            await SendRpcMessageToClientAsync(aggregateEvent.RequestInfo, businessResult);
        }
        // when reqMsgId==0?
        // forward message/bot message
        else if (aggregateEvent.RequestInfo.ReqMsgId == 0 || item.MessageSubType == MessageSubType.PhoneCall)
        {
            await PushUpdatesToPeerAsync(item.SenderPeer,
                selfUpdates,
                pts: item.Pts,
                updatesType: updatesType
            );
        }
        else
        {
            await ReplyRpcResultToSenderAsync(aggregateEvent.RequestInfo,
                item.SenderPeer,
                selfUpdates,
                item.SenderPeer.PeerId,
                item.Pts
            );
        }


        long? excludeAuthKeyId = aggregateEvent.RequestInfo.AuthKeyId;
        if (item.MessageActionType == MessageActionType.SetChatTheme)
        {
            excludeAuthKeyId = null;
        }

        await PushUpdatesToPeerAsync(item.SenderPeer,
            selfOtherDeviceUpdates,
            excludeAuthKeyId,
            pts: item.Pts,
            updatesType: updatesType
        );

        // Update MessageId in saved-star-gifts by RandomId
        if (item.MessageAction is TMessageActionStarGift && item.RandomId != 0)
        {
            var col = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
            await col.UpdateOneAsync(
                Builders<SavedStarGiftDocument>.Filter.And(
                    Builders<SavedStarGiftDocument>.Filter.Eq(d => d.RandomId, item.RandomId),
                    Builders<SavedStarGiftDocument>.Filter.Eq(d => d.MessageId, 0)
                ),
                Builders<SavedStarGiftDocument>.Update.Set(d => d.MessageId, item.MessageId)
            );
        }

        // Update MessageId in star-gift-offers by RandomId
        if (item.MessageAction is TMessageActionStarGiftPurchaseOffer && item.RandomId != 0)
        {
            var offerCol = mongoDatabase.GetCollection<StarGiftOfferDocument>("star-gift-offers");
            var updateResult = await offerCol.UpdateOneAsync(
                Builders<StarGiftOfferDocument>.Filter.And(
                    Builders<StarGiftOfferDocument>.Filter.Eq(d => d.RandomId, item.RandomId),
                    Builders<StarGiftOfferDocument>.Filter.Eq(d => d.MessageId, 0)
                ),
                Builders<StarGiftOfferDocument>.Update.Set(d => d.MessageId, item.MessageId)
            );
        }
    }

    private void SetChannelInfo(IRequestWithAccessHashKeyId request,
        IUpdates updates,
        IChannelReadModel? channelReadModel,
        IPhotoReadModel? photoReadModel,
        int layer)
    {
        if (channelReadModel == null)
        {
            return;
        }

        if (updates is TUpdates tUpdates)
        {
            if (tUpdates.Chats.All(p => p.Id != channelReadModel.ChannelId))
            {
                var channel =
                    chatConverterService.ToChannel(request, channelReadModel, photoReadModel, null, false, layer);
                tUpdates.Chats.Add(channel);
            }
        }
    }

    private async Task HandleSendMessageToChannelAsync(SendOutboxMessageCompletedSagaEvent aggregateEvent)
    {
        var item = aggregateEvent.MessageItem;
        var selfUpdates = sendMessageConverterService.ToUpdates(aggregateEvent, aggregateEvent.RequestInfo.Layer);
        var selfOtherDeviceUpdates = sendMessageConverterService.ToSelfOtherDeviceUpdates(aggregateEvent, 0);
        //var channelUpdates = updatesConverterService.ToChannelMessageUpdates(aggregateEvent.RequestInfo.UserId, aggregateEvent, aggregateEvent.RequestInfo.Layer);
        var channelAdminUpdates = updatesConverterService.ToChannelMessageUpdates(-1, aggregateEvent, 0);
        var channelMemberUpdates = updatesConverterService.ToChannelMessageUpdates(-1, aggregateEvent, 0);
        var channelReadModel = await channelAppService.GetAsync(item.ToPeer.PeerId);
        IChannelReadModel? sendAsReadModel = null;
        IChannelReadModel? forwardFromChannelReadModel = null;

        IPhotoReadModel? sendAsPhotoReadModel = null;
        IPhotoReadModel? forwardFromChannelPhotoReadModel = null;

        if (item.SendAs?.PeerType == PeerType.Channel)
        {
            sendAsReadModel = await channelAppService.GetAsync(item.SendAs.PeerId);
            sendAsPhotoReadModel = await photoAppService.GetAsync(sendAsReadModel?.PhotoId);
        }

        if (item.FwdHeader?.FromId?.PeerType == PeerType.Channel)
        {
            forwardFromChannelReadModel = await channelAppService.GetAsync(item.FwdHeader.FromId.PeerId);
            forwardFromChannelPhotoReadModel = await photoAppService.GetAsync((long?)item.FwdHeader.FromId.PeerId);
        }

        var photoReadModel = await photoAppService.GetAsync(channelReadModel.PhotoId);
        var layer = aggregateEvent.RequestInfo.Layer;

        SetChannelInfo(aggregateEvent.RequestInfo, selfUpdates, channelReadModel, photoReadModel, layer);
        SetChannelInfo(aggregateEvent.RequestInfo, selfUpdates, sendAsReadModel, sendAsPhotoReadModel, layer);
        SetChannelInfo(aggregateEvent.RequestInfo, selfUpdates, forwardFromChannelReadModel, forwardFromChannelPhotoReadModel, layer);

        SetChannelInfo(aggregateEvent.RequestInfo, selfOtherDeviceUpdates, channelReadModel, photoReadModel, 0);
        SetChannelInfo(aggregateEvent.RequestInfo, selfOtherDeviceUpdates, sendAsReadModel, sendAsPhotoReadModel, 0);
        SetChannelInfo(aggregateEvent.RequestInfo, selfOtherDeviceUpdates, forwardFromChannelReadModel, forwardFromChannelPhotoReadModel, 0);

        SetChannelInfo(RequestInfo.Empty with { UserId = -1 }, channelMemberUpdates, channelReadModel, photoReadModel, 0);
        SetChannelInfo(RequestInfo.Empty with { UserId = -1 }, channelMemberUpdates, sendAsReadModel, sendAsPhotoReadModel, 0);
        SetChannelInfo(RequestInfo.Empty with { UserId = -1 }, channelMemberUpdates, forwardFromChannelReadModel, forwardFromChannelPhotoReadModel, 0);

        ILayeredUser? user = null;
        if (item.FwdHeader?.FromId?.PeerType == PeerType.User)
        {
            var userId = item.FwdHeader.FromId.PeerId;
            user = await userConverterService.GetUserAsync(aggregateEvent.RequestInfo, userId, false, false,
                aggregateEvent.RequestInfo.Layer);

            if (selfUpdates is TUpdates tUpdates)
            {
                tUpdates.Users.Add(user);
            }

            if (selfOtherDeviceUpdates is TUpdates tUpdates2)
            {
                tUpdates2.Users.Add(user);
            }

            if (channelAdminUpdates is TUpdates tUpdates3)
            {
                tUpdates3.Users.Add(user);
            }
        }

        var updatesType = UpdatesType.Updates;
        if (item.MessageSubType == MessageSubType.Normal || item.MessageSubType == MessageSubType.ForwardMessage)
        {
            updatesType = UpdatesType.NewMessages;
        }

        var globalSeqNo = await SavePushUpdatesAsync(item.ToPeer.PeerId,
            channelMemberUpdates,
            item.Pts,
            aggregateEvent.RequestInfo.AuthKeyId,
            aggregateEvent.RequestInfo.UserId,
            messageId: item.MessageId
        );
        await AddRpcGlobalSeqNoForAuthKeyIdAsync(aggregateEvent.RequestInfo.ReqMsgId, item.SenderPeer.PeerId,
                globalSeqNo)
            ;

        if (aggregateEvent.RequestInfo.ReqMsgId == 0 /*|| item.MessageSubType == MessageSubType.ForwardMessage*/)
        {
            await PushUpdatesToPeerAsync(new Peer(PeerType.User, aggregateEvent.RequestInfo.UserId),
                selfOtherDeviceUpdates,
                pts: item.Pts,
                updatesType: updatesType
            );
        }
        else
        {
            await ReplyRpcResultToSenderAsync(aggregateEvent.RequestInfo,
                item.SenderUserId.ToUserPeer(),
                selfUpdates,
                aggregateEvent.RequestInfo.UserId,
                item.Pts
            );

            await PushUpdatesToPeerAsync(item.SenderUserId.ToUserPeer(),
                selfOtherDeviceUpdates,
                aggregateEvent.RequestInfo.PermAuthKeyId,
                pts: item.Pts,
                updatesType: updatesType
            );
        }

        // notify mentioned users
        if (aggregateEvent.MentionedUserIds?.Count > 0)
        {
            // Channel mentions are high-priority notifications; build a per-mention push so each
            // mentioned user's offline devices ring.
            PushData? mentionPush = await pushDataBuilder.BuildForChannelMessageAsync(item, channelReadModel.Title);

            foreach (var mentionedUserId in aggregateEvent.MentionedUserIds)
            {
                var mentionedUpdates = updatesConverterService.ToChannelMessageUpdates(mentionedUserId, aggregateEvent, 0, true);
                SetChannelInfo(RequestInfo.Empty with { UserId = mentionedUserId }, mentionedUpdates, channelReadModel, photoReadModel, 0);
                SetChannelInfo(RequestInfo.Empty with { UserId = mentionedUserId }, mentionedUpdates, sendAsReadModel, sendAsPhotoReadModel, 0);
                SetChannelInfo(RequestInfo.Empty with { UserId = mentionedUserId }, mentionedUpdates, forwardFromChannelReadModel, forwardFromChannelPhotoReadModel, 0);

                if (user != null)
                {
                    if (mentionedUpdates is TUpdates tUpdates)
                    {
                        tUpdates.Users.Add(user);
                    }
                }

                // Stamp the push with the actual recipient so the dispatcher targets the right devices.
                var targetedPush = mentionPush is null ? null : mentionPush with { UserId = mentionedUserId };
                await PushUpdatesToPeerAsync(mentionedUserId.ToUserPeer(), mentionedUpdates, pushData: targetedPush);
            }
        }

        var adminUserIds = channelReadModel.AdminList.Select(p => p.UserId).ToList();
        adminUserIds.Insert(0, channelReadModel.CreatorId);

        foreach (var adminUserId in adminUserIds)
        {
            if (adminUserId == aggregateEvent.RequestInfo.UserId)
            {
                continue;
            }

            if (aggregateEvent.MentionedUserIds?.Contains(adminUserId) ?? false)
            {
                continue;
            }

            SetChannelInfo(RequestInfo.Empty with { UserId = adminUserId }, channelAdminUpdates, channelReadModel, photoReadModel, 0);
            await PushUpdatesToPeerAsync(item.ToPeer,
                channelAdminUpdates,
                aggregateEvent.RequestInfo.AuthKeyId,
                updatesType: updatesType,
                skipSaveUpdates: true
            );
        }

        // Only send member left message to group admins
        if (aggregateEvent.MessageItem.MessageActionType == MessageActionType.ChatDeleteUser)
        {
            return;
        }

        await PushUpdatesToPeerAsync(item.ToPeer,
                channelMemberUpdates,
                excludeUserId:aggregateEvent.RequestInfo.UserId,
                updatesType: updatesType,
                skipSaveUpdates: true,
                excludeUserIds: aggregateEvent.MentionedUserIds
            )
            ;
    }

    private Task HandleSendOutboxMessageCompletedAsync(SendOutboxMessageCompletedSagaEvent aggregateEvent)
    {
        var item = aggregateEvent.MessageItem;
        if (item.QuickReplyItem != null)
        {
            return HandleSendQuickReplyMessageAsync(aggregateEvent);
        }

        return item.MessageSubType switch
        {
            //MessageSubType.CreateChat => HandleCreateChatAsync(aggregateEvent),
            MessageSubType.CreateChannel => HandleCreateChannelAsync(aggregateEvent),
            MessageSubType.AutoCreateChannelFromChat => HandleCreateChannelAsync(aggregateEvent),
            MessageSubType.InviteToChannel => HandleInviteToChannelAsync(aggregateEvent),
            MessageSubType.UpdatePinnedMessage => HandleUpdatePinnedMessageAsync(aggregateEvent),
            MessageSubType.ChatJoinByLink => HandleJoinChannelAsync(aggregateEvent),
            MessageSubType.ChatJoinBySelf => HandleJoinChannelAsync(aggregateEvent),
            _ => HandleSendMessageAsync(aggregateEvent)
        };
    }

    private async Task HandleUpdatePinnedMessageAsync(ReceiveInboxMessageCompletedSagaEvent aggregateEvent)
    {
        var updates = updatesConverterService.ToUpdatePinnedMessageUpdates(aggregateEvent);
        var item = aggregateEvent.MessageItem;
        await PushUpdatesToPeerAsync(item.OwnerPeer,
            updates,
            pts: item.Pts);
    }

    private async Task HandleUpdatePinnedMessageAsync(SendOutboxMessageCompletedSagaEvent aggregateEvent)
    {
        var item = aggregateEvent.MessageItem;
        var updates = updatesConverterService.ToUpdatePinnedMessageUpdates(aggregateEvent, 0);

        await PushUpdatesToPeerAsync(item.SenderPeer,
            updates,
            pts: item.Pts);

        if (item.ToPeer.PeerType == PeerType.Channel)
        {
            var channelUpdates = updatesConverterService.ToUpdatePinnedMessageServiceUpdates(0, aggregateEvent, 0);
            if (channelUpdates is TUpdates tUpdates)
            {
                var user = await GetUserAsync(aggregateEvent.RequestInfo, aggregateEvent.RequestInfo.UserId);
                tUpdates.Users.Add(user);
            }

            await PushUpdatesToPeerAsync(item.ToPeer,
                channelUpdates,
                aggregateEvent.RequestInfo.AuthKeyId,
                pts: item.Pts
            );
        }
    }

    public Task HandleAsync(
        IDomainEvent<SendMessageSaga, SendMessageSagaId, ReceiveInboxMessageCompletedSagaEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        return HandleReceiveMessageCompletedAsync(domainEvent.AggregateEvent);
    }

    public Task HandleAsync(
        IDomainEvent<SendMessageSaga, SendMessageSagaId, SendOutboxMessageCompletedSagaEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        return HandleSendOutboxMessageCompletedAsync(domainEvent.AggregateEvent);
    }

    private async Task UpdateChannelAndUserAsync(RequestInfo requestInfo, IUpdates updates, long channelId, List<long>? userIds, int layer = 0)
    {
        if (updates is TUpdates tUpdates)
        {
            if (tUpdates.Chats.Count == 0)
            {
                var channel = await chatConverterService.GetChannelAsync(requestInfo, channelId, false, false, layer);
                tUpdates.Chats = [channel];
            }

            if (userIds?.Count > 0)
            {
                var users = await userConverterService.GetUserListAsync(requestInfo, userIds, layer: layer);
                foreach (var layeredUser in users)
                {
                    tUpdates.Users.Add(layeredUser);
                }
            }
        }
    }
}
