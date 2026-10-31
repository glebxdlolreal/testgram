using MyTelegram.Messenger.Services.Premium;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services.StarsSubscriptions;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Import a chat invite and join a private chat/supergroup/channel
/// Possible errors
/// Code Type Description
/// 400 CHANNELS_TOO_MUCH You have joined too many channels/supergroups.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CHAT_INVALID Invalid chat.
/// 400 INVITE_HASH_EMPTY The invite hash is empty.
/// 406 INVITE_HASH_EXPIRED The invite link has expired.
/// 400 INVITE_HASH_INVALID The invite hash is invalid.
/// 400 INVITE_REQUEST_SENT You have successfully requested to join this chat or channel.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 STARS_PAYMENT_REQUIRED To import this chat invite link, you must first <a href="https://corefork.telegram.org/api/subscriptions#channel-subscriptions">pay for the associated Telegram Star subscription »</a>.
/// 400 USERS_TOO_MUCH The maximum number of users has been exceeded (to create a chat, for example).
/// 400 USER_ALREADY_PARTICIPANT The user is already in the group.
/// 400 USER_CHANNELS_TOO_MUCH One of the users you tried to add is already in too many channels/supergroups.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.importChatInvite"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ImportChatInviteHandler(ICommandBus commandBus, IChannelAppService channelAppService, IQueryProcessor queryProcessor, IStarsSubscriptionService starsSubscriptionService, IChatInvitePeekService chatInvitePeekService, IChatInviteExportedConverterService chatInviteExportedConverterService, IMongoDatabase mongoDatabase, IPremiumLimitGuard premiumLimitGuard) : RpcResultObjectHandler<RequestImportChatInvite, IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestImportChatInvite obj)
    {
        if (string.IsNullOrEmpty(obj.Hash))
        {
            RpcErrors.RpcErrors400.InviteHashEmpty.ThrowRpcError();
        }

        var chatInviteReadModel = await queryProcessor.ProcessAsync(new GetChatInviteByLinkQuery(obj.Hash));
        if (chatInviteReadModel == null)
        {
            RpcErrors.RpcErrors400.InviteHashInvalid.ThrowRpcError();
        }

        var channelId = chatInviteReadModel!.PeerId;
        if (chatInviteReadModel.ExpireDate > 0)
        {
            if (chatInviteReadModel.ExpireDate.Value < CurrentDate)
            {
                RpcErrors.RpcErrors400.InviteHashExpired.ThrowRpcError();
            }
        }

        if (chatInviteReadModel.Revoked)
        {
            RpcErrors.RpcErrors400.InviteHashInvalid.ThrowRpcError();
        }

        if (chatInviteReadModel.UsageLimit > 0)
        {
            if (chatInviteReadModel.Usage >= chatInviteReadModel.UsageLimit)
            {
                RpcErrors.RpcErrors400.UsersTooMuch.ThrowRpcError();
            }
        }

        var channelReadModel = await channelAppService.GetAsync(channelId);
        var admin = channelReadModel.AdminList.FirstOrDefault(p => p.UserId == chatInviteReadModel.AdminId);
        if (admin == null)
        {
            RpcErrors.RpcErrors400.InviteHashInvalid.ThrowRpcError();
        }

        var channelMember = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(channelId, input.UserId));
        if (channelMember is { Left: false, Kicked: false })
        {
            RpcErrors.RpcErrors400.UserAlreadyParticipant.ThrowRpcError();
        }

        // A ban whose until_date has passed no longer blocks the invite link.
        if (MyTelegram.Messenger.Helpers.BannedRightsHelper.IsCurrentlyKicked(channelMember, CurrentDate))
        {
            RpcErrors.RpcErrors400.InviteHashInvalid.ThrowRpcError();
        }

        var joinRequestReadModel = await queryProcessor.ProcessAsync(new GetJoinRequestQuery(chatInviteReadModel.PeerId, input.UserId));
        if (joinRequestReadModel is { IsJoinRequestProcessed: false })
        {
            RpcErrors.RpcErrors400.InviteRequestSent.ThrowRpcError();
        }

        // channels_limit applies when the link joins outright; a join request is checked on approval.
        if (!chatInviteReadModel.RequestNeeded && !channelReadModel.JoinRequest)
        {
            await premiumLimitGuard.EnsureCanJoinChannelAsync(input.UserId);
        }

        // A paid invite link can only be imported once the Star subscription has been paid for
        // through payments.sendStarsForm, or while a previously paid one is still valid.
        int? subscriptionUntilDate = null;
        if (chatInviteReadModel.SubscriptionPricingAmount is > 0)
        {
            var subscription = await starsSubscriptionService.GetActiveSubscriptionAsync(input.UserId, chatInviteReadModel.PeerId);
            if (subscription == null)
            {
                RpcErrors.RpcErrors400.StarsPaymentRequired.ThrowRpcError();
            }

            subscriptionUntilDate = subscription!.UntilDate;
        }

        var requestState = chatInviteReadModel.RequestNeeded ? ChatInviteRequestState.WaitingForApproval : ChatInviteRequestState.NoApprovalRequired;
        if (!chatInviteReadModel.RequestNeeded)
        {
            if (channelReadModel.JoinRequest)
            {
                requestState = ChatInviteRequestState.WaitingForApproval;
            }
        }

        // Membership now decides what the user may read, so the preview window granted by
        // messages.checkChatInvite must not outlive it and keep working after they leave again.
        await chatInvitePeekService.RevokeAsync(input.UserId, chatInviteReadModel.PeerId);

        var command = new ImportChatInviteCommand(ChatInviteId.Create(chatInviteReadModel.PeerId, chatInviteReadModel.InviteId), input.ToRequestInfo(), requestState, CurrentDate, subscriptionUntilDate);
        await commandBus.PublishAsync(command);

        // A join still awaiting approval is not a join yet: it is logged as participantJoinByRequest
        // once an admin lets it through.
        if (requestState == ChatInviteRequestState.NoApprovalRequired)
        {
            var exportedInvite = await ChatInviteExportedFiller.ToExportedChatInviteAsync(
                chatInviteExportedConverterService, queryProcessor, chatInviteReadModel, input.Layer);

            await AdminLogHelper.LogParticipantJoinByInvite(mongoDatabase, channelId, input.UserId,
                viaChatlist: false, exportedInvite);
        }

        return null !;
    }
}