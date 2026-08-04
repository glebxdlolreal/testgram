using MyTelegram.Domain.Aggregates.UserConfig;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Clear recently used <a href="https://corefork.telegram.org/api/reactions">message reactions</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.clearRecentReactions"/> </c></para>
/// </summary>
internal sealed class ClearRecentReactionsHandler(
    ICommandBus commandBus,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestClearRecentReactions, IBool>
{
    private const string RecentKey = "recent_reactions";

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestClearRecentReactions obj)
    {
        var configId = UserConfigId.Create(input.UserId, RecentKey);
        await commandBus.PublishAsync(new UpdateUserConfigCommand(configId, input.ToRequestInfo(), input.UserId, RecentKey, ""));

        // Tell the user's other sessions the recent list changed.
        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdateRecentReactions()),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = CurrentDate,
            Seq = 0
        };
        await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, input.UserId), updates,
            excludeAuthKeyId: input.PermAuthKeyId);

        return new TBoolTrue();
    }
}
