namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Reorder usernames associated with the currently logged-in user. The order must list every active
/// username exactly once.
/// Possible errors
/// Code Type Description
/// 400 ORDER_INVALID The specified username order is invalid.
/// See https://corefork.telegram.org/method/account.reorderUsernames and
/// https://corefork.telegram.org/api/fragment
/// </summary>
internal sealed class ReorderUsernamesHandler(IQueryProcessor queryProcessor, ICommandBus commandBus)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestReorderUsernames, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Account.RequestReorderUsernames obj)
    {
        var user = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (user == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return null!;
        }

        var current = UsernameListEditor.Effective(user.Usernames.ToUsernameItems(), user.UserName);
        var updated = UsernameListEditor.Reorder(current, obj.Order);
        if (updated != null)
        {
            await commandBus.PublishAsync(new SetUsernamesCommand(UserId.Create(user.UserId),
                input.ToRequestInfo(), updated.ToList(), current));
        }

        return new TBoolTrue();
    }
}
