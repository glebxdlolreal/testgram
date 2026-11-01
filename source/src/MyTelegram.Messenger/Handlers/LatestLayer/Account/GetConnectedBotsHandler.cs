using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// List all currently connected <a href="https://corefork.telegram.org/api/bots/connected-business-bots">business bots »</a>
/// <para>At most one: tdlib refuses a list with more than one bot.</para>
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getConnectedBots"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetConnectedBotsHandler(
    IBusinessConnectionStore connectionStore,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<RequestGetConnectedBots, IConnectedBots>
{
    protected override async Task<IConnectedBots> HandleCoreAsync(IRequestInput input, RequestGetConnectedBots obj)
    {
        var connection = await connectionStore.GetByUserAsync(input.UserId);
        if (connection == null)
        {
            return new TConnectedBots { ConnectedBots = [], Users = [] };
        }

        var bot = await userConverterService.GetUserAsync(input, connection.BotId, layer: input.Layer);
        var r = connection.Recipients;
        return new TConnectedBots
        {
            ConnectedBots = new TVector<IConnectedBot>(new TConnectedBot
            {
                BotId = connection.BotId,
                Recipients = new TBusinessBotRecipients
                {
                    ExistingChats = r.ExistingChats,
                    NewChats = r.NewChats,
                    Contacts = r.Contacts,
                    NonContacts = r.NonContacts,
                    ExcludeSelected = r.ExcludeSelected,
                    Users = r.UserIds.Count > 0 ? new TVector<long>(r.UserIds) : null,
                    ExcludeUsers = connection.ExcludeUserIds.Count > 0
                        ? new TVector<long>(connection.ExcludeUserIds)
                        : null
                },
                Rights = connection.Rights.ToTl()
            }),
            Users = new TVector<IUser>(bot)
        };
    }
}
