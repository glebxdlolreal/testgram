using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;

/// <summary>
/// Reorder usernames associated to a bot we own. The order must list every active username exactly
/// once.
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// 400 ORDER_INVALID The specified username order is invalid.
/// See https://corefork.telegram.org/method/bots.reorderUsernames and
/// https://corefork.telegram.org/api/fragment
/// </summary>
internal sealed class ReorderUsernamesHandler(IOwnedBotResolver ownedBotResolver, ICommandBus commandBus)
    : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestReorderUsernames, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Bots.RequestReorderUsernames obj)
    {
        var bot = await ownedBotResolver.GetOwnedBotAsync(input, obj.Bot);

        var current = UsernameListEditor.Effective(bot.Usernames.ToUsernameItems(), bot.UserName);
        var updated = UsernameListEditor.Reorder(current, obj.Order);
        if (updated != null)
        {
            await commandBus.PublishAsync(new SetUsernamesCommand(UserId.Create(bot.UserId),
                input.ToRequestInfo(), updated.ToList(), current));
        }

        return new TBoolTrue();
    }
}
