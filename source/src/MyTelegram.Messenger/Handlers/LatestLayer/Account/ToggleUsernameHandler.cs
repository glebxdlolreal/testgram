namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Activate or deactivate a purchased <a href="https://fragment.com">fragment.com</a> username
/// associated to the currently logged-in user.
/// Possible errors
/// Code Type Description
/// 400 USERNAME_INVALID The provided username is not valid.
/// 400 USERNAME_NOT_MODIFIED The username was not modified.
/// 400 USERNAMES_ACTIVE_TOO_MUCH The maximum number of active usernames was reached.
/// See https://corefork.telegram.org/method/account.toggleUsername and
/// https://corefork.telegram.org/api/fragment
/// </summary>
internal sealed class ToggleUsernameHandler(IQueryProcessor queryProcessor, ICommandBus commandBus)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestToggleUsername, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Account.RequestToggleUsername obj)
    {
        var user = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (user == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return null!;
        }

        var current = UsernameListEditor.Effective(user.Usernames.ToUsernameItems(), user.UserName);
        var updated = UsernameListEditor.Toggle(current, obj.Username, obj.Active, forBot: false);
        if (updated == null)
        {
            RpcErrors.RpcErrors400.UsernameNotModified.ThrowRpcError();
            return null!;
        }

        // updateUserName reaches the other sessions and the contacts from the event handler.
        await commandBus.PublishAsync(new SetUsernamesCommand(UserId.Create(user.UserId), input.ToRequestInfo(),
            updated.ToList(), current));

        return new TBoolTrue();
    }
}
