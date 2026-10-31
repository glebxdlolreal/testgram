using MyTelegram.Messenger.Services.Premium;
using MyTelegram.Messenger.Services.Fragment;
using EventFlow.Queries;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Change or remove the username of a supergroup/channel
/// Possible errors
/// Code Type Description
/// 400 CHANNELS_ADMIN_PUBLIC_TOO_MUCH You're admin of too many public channels, make some channels private to change the username of this channel.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 403 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 USERNAME_INVALID The provided username is not valid.
/// 400 USERNAME_NOT_MODIFIED The username was not modified.
/// 400 USERNAME_OCCUPIED The provided username is already occupied.
/// 400 USERNAME_PURCHASE_AVAILABLE The specified username can be purchased on <a href="https://fragment.com/">https://fragment.com</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.updateUsername"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateUsernameHandler(ICommandBus commandBus, IQueryProcessor queryProcessor, IChannelAppService channelAppService, IUsernameHelper usernameHelper, IChannelAdminRightsChecker channelAdminRightsChecker, IMongoDatabase mongoDatabase, IFragmentCollectibleCatalog collectibleCatalog, IPremiumLimitGuard premiumLimitGuard) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestUpdateUsername, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestUpdateUsername obj)
    {
        if (obj.Channel is TInputChannel inputChannel)
        {
            if (!string.IsNullOrEmpty(obj.Username))
            {
                if (!usernameHelper.IsValidUsername(obj.Username))
                {
                    RpcErrors.RpcErrors400.UsernameInvalid.ThrowRpcError();
                }

                // A username listed on Fragment that nobody holds can only be bought there.
                if (await collectibleCatalog.IsUsernameForSaleAsync(obj.Username))
                {
                    RpcErrors.RpcErrors400.UsernamePurchaseAvailable.ThrowRpcError();
                }
            }

            await channelAdminRightsChecker.ThrowIfNotChannelOwnerAsync(obj.Channel, input.UserId);
            var channelReadModel = await channelAppService.GetAsync(inputChannel.ChannelId);
            var oldUserName = channelReadModel.UserName;
            if (string.Equals(obj.Username, oldUserName, StringComparison.OrdinalIgnoreCase))
            {
                RpcErrors.RpcErrors400.UsernameNotModified.ThrowRpcError();
            }

            // Making a private channel public counts against channels_public_limit_default / _premium.
            if (string.IsNullOrEmpty(oldUserName) && !string.IsNullOrEmpty(obj.Username))
            {
                await premiumLimitGuard.EnsureCanOwnPublicChannelAsync(input.UserId);
            }

            var userNameForId = string.IsNullOrEmpty(obj.Username) ? oldUserName : obj.Username;

            // Log username change in admin log
            await AdminLogHelper.LogChangeUsername(mongoDatabase, inputChannel.ChannelId, input.UserId, oldUserName ?? string.Empty, obj.Username ?? string.Empty);

            var command = new UpdateUserNameCommand(UserNameId.Create(userNameForId?.ToLower() ?? string.Empty), input.ToRequestInfo(), inputChannel.ChannelId.ToChannelPeer(), obj.Username, oldUserName);
            await commandBus.PublishAsync(command);
            return new TBoolTrue();
        }

        RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        return null!;
    }
}