using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get full info about a <a href="https://corefork.telegram.org/api/channel#basic-groups">basic group</a>.
/// Possible errors
/// Code Type Description
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getFullChat"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetFullChatHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IChatConverterService chatConverterService,
    IPhotoAppService photoAppService,
    IChannelAppService channelAppService,
    IMongoDatabase mongoDatabase,
    IPeerTranslationSettingsStore peerTranslationSettings,
    IPinnedMessageResolver pinnedMessageResolver)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetFullChat, MyTelegram.Schema.Messages.IChatFull>
{
    protected override async Task<MyTelegram.Schema.Messages.IChatFull> HandleCoreAsync(IRequestInput input, RequestGetFullChat obj)
    {
        var peerType = peerHelper.GetPeerType(obj.ChatId);
        switch (peerType)
        {
            case PeerType.Channel:
            {
                var channel = await channelAppService.GetAsync(obj.ChatId);
                var channelFull = await queryProcessor.ProcessAsync(new GetChannelFullByIdQuery(obj.ChatId));
                var channelMember = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(obj.ChatId, input.UserId));
                var peerNotifySettings = await queryProcessor.ProcessAsync(new GetPeerNotifySettingsByIdQuery(PeerNotifySettingsId.Create(input.UserId, PeerType.Channel, obj.ChatId).Value), CancellationToken.None);
                var photoReadModel = await photoAppService.GetAsync(channel.PhotoId);
                var chatFull = chatConverterService.ToChannelFull(input, channel, photoReadModel, channelFull!, channelMember, peerNotifySettings, null, input.Layer);
                var activeCall = await GroupCallStateHelper.FindActivePeerCallAsync(
                    mongoDatabase,
                    obj.ChatId,
                    PeerType.Channel);
                if (activeCall != null)
                {
                    GroupCallStateHelper.ApplyActiveCallToChatFull(chatFull, activeCall, input.UserId,
                    await GroupCallStateHelper.GetSavedDefaultJoinAsAsync(mongoDatabase, input.UserId, (PeerType)activeCall.PeerType, activeCall.PeerId));
                }

                // ChannelFullReadModel.PinnedMsgId is never written, so the latest pinned message has to
                // be resolved live here just like channels.getFullChannel does.
                var pinnedMsgId = await pinnedMessageResolver.GetChannelPinnedMsgIdAsync(obj.ChatId);
                if (pinnedMsgId.HasValue && chatFull.FullChat is TChannelFull tChannelFull)
                {
                    tChannelFull.PinnedMsgId = pinnedMsgId.Value;
                }

                // The same per-caller translation flag channels.getFullChannel reports. This method is
                // the one Android calls for a group, so leaving it out here means the popup is
                // dismissible in a channel and not in a supergroup.
                // See https://corefork.telegram.org/api/translation
                if (chatFull.FullChat is TChannelFull translatable)
                {
                    translatable.TranslationsDisabled = await peerTranslationSettings.IsDisabledAsync(
                        input.UserId, new Peer(PeerType.Channel, obj.ChatId));
                }

                return chatFull;
            }
        }

        // Testgram keeps no basic groups — messages.createChat emits a CreateChannelCommand — so an
        // id outside the channel range names no group that exists here. That is a client-side
        // mistake, not a server fault, so it must come back as an rpc error rather than a 500.
        // See https://corefork.telegram.org/api/channel#basic-groups
        RpcErrors.RpcErrors400.ChatIdInvalid.ThrowRpcError();
        return null!;
    }
}
