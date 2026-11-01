using MyTelegram.Messenger.Services.SecretChat;
using MyTelegram.Schema.Updates;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Updates;
/// <summary>
/// Returns a current state of updates.
/// <para><c>See <a href="https://corefork.telegram.org/method/updates.getState"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetStateHandler(IPtsHelper ptsHelper, ISecretChatMessageStore secretChatMessageStore, MyTelegram.Messenger.Services.Bots.IBotQtsCounter botQtsCounter) : RpcResultObjectHandler<MyTelegram.Schema.Updates.RequestGetState, MyTelegram.Schema.Updates.IState>
{
    protected override async Task<IState> HandleCoreAsync(IRequestInput input, RequestGetState obj)
    {
        if (input.UserId == 0)
        {
            RpcErrors.RpcErrors403.UserInvalid.ThrowRpcError();
        //RpcErrors.RpcErrors401.AuthKeyInvalid.ThrowRpcError();
        }

        var cacheItem = await ptsHelper.GetPtsForUserAsync(input.UserId);
        // qts is the secret-chat per-Authorization_Key sequence (QtsInitialValue-1 == 0 when unused).
        var secretChatQts = await secretChatMessageStore.GetHighestQtsAsync(input.UserId, input.PermAuthKeyId);
        var state = new TState
        {
            Date = CurrentDate,
            Pts = cacheItem.Pts,
            // A bot's qts comes from the shared counter (business, callback and inline updates).
            Qts = Math.Max(Math.Max(cacheItem.Qts, secretChatQts), await botQtsCounter.GetAsync(input.UserId)),
            Seq = cacheItem.Seq,
            UnreadCount = cacheItem.UnreadCount,
        };
        return state;
    }
}