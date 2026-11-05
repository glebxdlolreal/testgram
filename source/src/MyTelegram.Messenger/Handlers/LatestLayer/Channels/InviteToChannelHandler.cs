using MyTelegram.Messenger.Services.Premium;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Invite users to a channel/supergroup
/// Possible errors
/// Code Type Description
/// 400 BOTS_TOO_MUCH There are too many bots in this chat/channel.
/// 400 BOT_GROUPS_BLOCKED This bot can't be added to groups.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_MONOFORUM_UNSUPPORTED <a href="https://corefork.telegram.org/api/channel#monoforums">Monoforums</a> do not support this feature.
/// 406 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 403 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CHAT_INVALID Invalid chat.
/// 400 CHAT_MEMBER_ADD_FAILED Could not add participants.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 INPUT_USER_DEACTIVATED The specified user was deleted.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 USERS_TOO_MUCH The maximum number of users has been exceeded (to create a chat, for example).
/// 400 USER_BANNED_IN_CHANNEL You're banned from sending messages in supergroups/channels.
/// 400 USER_BLOCKED User blocked.
/// 400 USER_BOT Bots can only be admins in channels.
/// 403 USER_CHANNELS_TOO_MUCH One of the users you tried to add is already in too many channels/supergroups.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// 400 USER_KICKED This user was kicked from this supergroup/channel.
/// 403 USER_NOT_MUTUAL_CONTACT The provided user is not a mutual contact.
/// 403 USER_PRIVACY_RESTRICTED The user's privacy settings do not allow you to do this.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.inviteToChannel"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class InviteToChannelHandler(ICommandBus commandBus, IPeerHelper peerHelper, IPrivacyAppService privacyAppService, IChannelAppService channelAppService, IUserAppService userAppService, IQueryProcessor queryProcessor, IChannelAdminRightsChecker channelAdminRightsChecker, IMongoDatabase mongoDatabase, IPremiumLimitGuard premiumLimitGuard) : RpcResultObjectHandler<RequestInviteToChannel, IInvitedUsers>
{
    protected override async Task<IInvitedUsers> HandleCoreAsync(IRequestInput input, RequestInviteToChannel obj)
    {
        if (obj.Channel is not TInputChannel inputChannel)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            return null!;
        }

        {
            await channelAdminRightsChecker.CheckAdminRightAsync(obj.Channel, input.UserId, adminRights => adminRights.InviteUsers);
            var channelId = inputChannel.ChannelId;
            var channelReadModel = await channelAppService.GetAsync(inputChannel.ChannelId);
            channelReadModel.ThrowExceptionIfChannelDeleted();
            var userReadModel = await userAppService.GetAsync(input.UserId);
            var userIds = new List<long>();
            var botUserIds = new List<long>();
            foreach (var item in obj.Users)
            {
                if (item is TInputUser inputUser)
                {
                    userIds.Add(inputUser.UserId);
                }
            }

            // Turned off with @BotFather's /setjoingroups; channels are still allowed.
            if (!channelReadModel!.Broadcast)
            {
                var invitedBotIds = userIds.Where(peerHelper.IsBotUser).ToList();
                if (invitedBotIds.Count > 0 &&
                    (await userAppService.GetListAsync(invitedBotIds)).Any(p => p.BotNochats))
                {
                    RpcErrors.RpcErrors400.BotGroupsBlocked.ThrowRpcError();
                }
            }

            // Users whose privacy settings forbid the invite are reported back as missingInvitee
            // rather than failing the request, so the remaining users still get added.
            var privacyRestrictedUserIdList = new List<long>();
            await privacyAppService.ApplyPrivacyListAsync(input.UserId, userIds, (_, restrictedUserId) => privacyRestrictedUserIdList.Add(restrictedUserId), new List<PrivacyType> { PrivacyType.ChatInvite });
            userIds.RemoveAll(privacyRestrictedUserIdList.Contains);

            // Users already in as many channels as their channels_limit allows cannot be added.
            var atChannelLimit = new List<long>();
            foreach (var userId in userIds)
            {
                if (await premiumLimitGuard.IsAtChannelLimitAsync(userId))
                {
                    atChannelLimit.Add(userId);
                }
            }

            userIds.RemoveAll(atChannelLimit.Contains);
            if (userIds.Count == 0 && atChannelLimit.Count > 0)
            {
                RpcErrors.RpcErrors400.UserChannelsTooMuch.ThrowRpcError();
            }

            // Nothing left to do means the caller learns nothing from an empty success.
            if (userIds.Count == 0)
            {
                RpcErrors.RpcErrors403.UserPrivacyRestricted.ThrowRpcError();
            }

            var inviterUserId = input.UserId;
            if (channelReadModel!.Broadcast || channelReadModel.HasLink)
            {
                inviterUserId = MyTelegramConsts.GroupAnonymousBotUserId;
            }

            // Members are marked as bots here, the way addChatUser and createChat mark them; group privacy
            // mode and the bot member queries rely on it.
            botUserIds.AddRange(userIds.Where(peerHelper.IsBotUser));

            var command = new StartInviteToChannelCommand(TempId.New, input.ToRequestInfo(), channelId, channelReadModel.Broadcast, channelReadModel.HasLink, inviterUserId, channelReadModel.TopMessageId, channelReadModel.TopMessageId, userIds, botUserIds, ChatJoinType.InvitedByAdmin, privacyRestrictedUserIdList);
            await commandBus.PublishAsync(command);

            // One admin log entry per user actually invited; the privacy-restricted ones were removed
            // from the list above and never join.
            foreach (var invitedUserId in userIds)
            {
                await AdminLogHelper.LogParticipantInvite(mongoDatabase, channelId, input.UserId,
                    new MyTelegram.Schema.TChannelParticipant
                    {
                        UserId = invitedUserId,
                        Date = CurrentDate
                    });
            }

            return null!;
        }
    }
}