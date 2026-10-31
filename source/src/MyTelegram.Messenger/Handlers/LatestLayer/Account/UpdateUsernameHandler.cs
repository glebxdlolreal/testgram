using MyTelegram.Messenger.Services.Fragment;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Changes username for the current user.
/// Possible errors
/// Code Type Description
/// 400 USERNAME_INVALID The provided username is not valid.
/// 400 USERNAME_NOT_MODIFIED The username was not modified.
/// 400 USERNAME_OCCUPIED The provided username is already occupied.
/// 400 USERNAME_PURCHASE_AVAILABLE The specified username can be purchased on <a href="https://fragment.com/">https://fragment.com</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updateUsername"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateUsernameHandler(ICommandBus commandBus, IQueryProcessor queryProcessor, IUserAppService userAppService, IUsernameHelper usernameHelper, IFragmentCollectibleCatalog collectibleCatalog) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestUpdateUsername, MyTelegram.Schema.IUser>
{
    protected override async Task<IUser> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestUpdateUsername obj)
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

        var userReadModel = await userAppService.GetAsync(input.UserId);
        var oldUserName = userReadModel.UserName;
        if (string.Equals(obj.Username, oldUserName, StringComparison.OrdinalIgnoreCase))
        {
            RpcErrors.RpcErrors400.UsernameNotModified.ThrowRpcError();
        }

        // When clearing username (empty), use oldUserName as the aggregate ID to locate and delete the existing record.
        // Using empty string would create a wrong aggregate ID causing MongoDB _id: null error.
        var userNameForId = string.IsNullOrEmpty(obj.Username) ? oldUserName : obj.Username;
        var command = new UpdateUserNameCommand(UserNameId.Create(userNameForId?.ToLower() ?? string.Empty), input.ToRequestInfo(), input.UserId.ToUserPeer(), obj.Username, oldUserName);
        await commandBus.PublishAsync(command);
        return null !;
    }
}