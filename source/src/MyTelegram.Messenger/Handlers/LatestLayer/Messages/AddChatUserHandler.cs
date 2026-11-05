namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Adds a user to a chat and sends a service message on it.
/// Possible errors
/// Code Type Description
/// 400 BOT_GROUPS_BLOCKED This bot can't be added to groups.
/// 403 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 CHAT_INVALID Invalid chat.
/// 400 CHAT_MEMBER_ADD_FAILED Could not add participants.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 INPUT_USER_DEACTIVATED The specified user was deleted.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 USERS_TOO_MUCH The maximum number of users has been exceeded (to create a chat, for example).
/// 400 USER_ALREADY_PARTICIPANT The user is already in the group.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// 400 USER_IS_BLOCKED You were blocked by this user.
/// 403 USER_NOT_MUTUAL_CONTACT The provided user is not a mutual contact.
/// 403 USER_PRIVACY_RESTRICTED The user's privacy settings do not allow you to do this.
/// 400 YOU_BLOCKED_USER You blocked this user.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.addChatUser"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// This fork has no separate basic-group aggregate - messages.createChat allocates a channel id -
/// so a chat id here is a supergroup id and the invite runs through the same path as
/// channels.inviteToChannel.
/// </para>
/// <para>
/// fwd_limit is accepted for protocol compatibility but has no effect: how much history a new
/// member sees is governed by the channel's hidden-prehistory setting, not per invite.
/// </para>
/// </remarks>
internal sealed class AddChatUserHandler(
    ICommandBus commandBus,
    IPeerHelper peerHelper,
    IPrivacyAppService privacyAppService,
    IChannelAppService channelAppService,
    IQueryProcessor queryProcessor,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IUserAppService userAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestAddChatUser, MyTelegram.Schema.Messages.IInvitedUsers>
{
    protected override async Task<MyTelegram.Schema.Messages.IInvitedUsers> HandleCoreAsync(IRequestInput input, RequestAddChatUser obj)
    {
        if (obj.ChatId <= 0)
        {
            RpcErrors.RpcErrors400.ChatIdInvalid.ThrowRpcError();
        }

        var channelId = obj.ChatId;
        var channelReadModel = await channelAppService.GetAsync(channelId);
        if (channelReadModel == null)
        {
            RpcErrors.RpcErrors400.ChatIdInvalid.ThrowRpcError();
        }

        channelReadModel.ThrowExceptionIfChannelDeleted();

        await channelAdminRightsChecker.CheckAdminRightAsync(channelId, input.UserId, p => p.InviteUsers, RpcErrors.RpcErrors403.ChatAdminRequired);

        var targetPeer = peerHelper.GetPeer(obj.UserId, input.UserId);
        if (targetPeer.PeerType != PeerType.User || targetPeer.PeerId <= 0)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        var targetUserId = targetPeer.PeerId;
        var channelMember = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(channelId, targetUserId));
        if (channelMember is { Left: false, Kicked: false })
        {
            RpcErrors.RpcErrors400.UserAlreadyParticipant.ThrowRpcError();
        }

        // With a single invitee there is nothing left to add when privacy blocks them, so this
        // reports the error instead of answering with an empty success.
        var privacyRestrictedUserIdList = new List<long>();
        await privacyAppService.ApplyPrivacyListAsync(input.UserId, [targetUserId], (_, restrictedUserId) => privacyRestrictedUserIdList.Add(restrictedUserId), [PrivacyType.ChatInvite]);
        if (privacyRestrictedUserIdList.Count > 0)
        {
            RpcErrors.RpcErrors403.UserPrivacyRestricted.ThrowRpcError();
        }

        var inviterUserId = input.UserId;
        if (channelReadModel!.Broadcast || channelReadModel.HasLink)
        {
            inviterUserId = MyTelegramConsts.GroupAnonymousBotUserId;
        }

        var botUserIds = peerHelper.IsBotUser(targetUserId) ? new List<long> { targetUserId } : [];

        // Turned off with @BotFather's /setjoingroups; channels are still allowed.
        if (botUserIds.Count > 0 && !channelReadModel.Broadcast &&
            (await userAppService.GetAsync(targetUserId))?.BotNochats == true)
        {
            RpcErrors.RpcErrors400.BotGroupsBlocked.ThrowRpcError();
        }

        var command = new StartInviteToChannelCommand(TempId.New,
            input.ToRequestInfo(),
            channelId,
            channelReadModel.Broadcast,
            channelReadModel.HasLink,
            inviterUserId,
            channelReadModel.TopMessageId,
            channelReadModel.TopMessageId,
            [targetUserId],
            botUserIds,
            ChatJoinType.InvitedByAdmin,
            []);
        await commandBus.PublishAsync(command);

        // The reply is pushed by the invite saga once the member has been created.
        return null!;
    }
}
