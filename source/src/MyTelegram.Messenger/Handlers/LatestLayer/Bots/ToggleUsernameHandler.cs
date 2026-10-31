using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;

/// <summary>
/// Activate or deactivate a purchased <a href="https://fragment.com">fragment.com</a> username
/// associated to a bot we own. Unlike a user, a bot may switch off its editable username while
/// another one stays active.
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// 400 USERNAME_INVALID The provided username is not valid.
/// 400 USERNAME_NOT_MODIFIED The username was not modified.
/// 400 USERNAMES_ACTIVE_TOO_MUCH The maximum number of active usernames was reached.
/// See https://corefork.telegram.org/method/bots.toggleUsername and
/// https://corefork.telegram.org/api/fragment
/// </summary>
internal sealed class ToggleUsernameHandler(IOwnedBotResolver ownedBotResolver, ICommandBus commandBus)
    : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestToggleUsername, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Bots.RequestToggleUsername obj)
    {
        var bot = await ownedBotResolver.GetOwnedBotAsync(input, obj.Bot);

        var current = UsernameListEditor.Effective(bot.Usernames.ToUsernameItems(), bot.UserName);
        var updated = UsernameListEditor.Toggle(current, obj.Username, obj.Active, forBot: true);
        if (updated == null)
        {
            RpcErrors.RpcErrors400.UsernameNotModified.ThrowRpcError();
            return null!;
        }

        await commandBus.PublishAsync(new SetUsernamesCommand(UserId.Create(bot.UserId), input.ToRequestInfo(),
            updated.ToList(), current));

        return new TBoolTrue();
    }
}
