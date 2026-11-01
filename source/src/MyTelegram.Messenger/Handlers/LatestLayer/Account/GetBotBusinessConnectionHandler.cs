using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Bots may invoke this method to re-fetch the <a href="https://corefork.telegram.org/constructor/updateBotBusinessConnect">updateBotBusinessConnect</a> constructor associated with a specific business <code>connection_id</code>, see here » for more info on connected business bots.<br/>
/// This is needed for example for freshly logged in bots that are receiving some <a href="https://corefork.telegram.org/constructor/updateBotNewBusinessMessage">updateBotNewBusinessMessage</a>, etc. updates because some users have already connected to the bot before it could login.<br/>
/// In this case, the bot is receiving messages from a business connection it doesn't have a cached <code>connection_id</code> for.
/// Possible errors
/// Code Type Description
/// 400 CONNECTION_ID_INVALID The specified connection ID is invalid.
/// <para>The answer is not a qts-sequenced update: tdlib reads it directly as the method's result.</para>
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getBotBusinessConnection"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetBotBusinessConnectionHandler(
    IBusinessConnectionStore connectionStore,
    IBusinessBotNotifier notifier)
    : RpcResultObjectHandler<RequestGetBotBusinessConnection, IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestGetBotBusinessConnection obj)
    {
        var connection = await connectionStore.GetByConnectionIdAsync(obj.ConnectionId);
        if (connection == null || connection.BotId != input.UserId)
        {
            RpcErrors.RpcErrors400.ConnectionIdInvalid.ThrowRpcError();
        }

        var users = await notifier.GetUsersForBotAsync(input.UserId, [connection!.UserId]);
        return new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdateBotBusinessConnect
            {
                Connection = notifier.ToTl(connection, false),
                Qts = 0
            }),
            Users = new TVector<IUser>(users),
            Chats = [],
            Date = CurrentDate,
            Seq = 0
        };
    }
}
