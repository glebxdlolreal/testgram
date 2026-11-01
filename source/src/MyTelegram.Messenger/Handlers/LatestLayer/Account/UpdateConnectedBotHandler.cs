using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Connect a <a href="https://corefork.telegram.org/api/bots/connected-business-bots">business bot »</a> to the current account, or to change the current connection settings.
/// Possible errors
/// Code Type Description
/// 400 BOT_BUSINESS_MISSING The specified bot is not a business bot (the <a href="https://corefork.telegram.org/constructor/user">user</a>.<code>bot_business</code> flag is not set).
/// 400 BUSINESS_RECIPIENTS_EMPTY You didn't set any flag in inputBusinessBotRecipients, thus the bot cannot work with <em>any</em> peer.
/// <para>An account has one connected bot: connecting another one disconnects the previous bot. Every change
/// reaches the bot as <c>updateBotBusinessConnect</c> with a new <c>connection_id</c>, as the method documents
/// ("the connection ID ... was changed by a change of the business connection parameters").</para>
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updateConnectedBot"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateConnectedBotHandler(
    IAccessHashHelper2 accessHashHelper,
    IPeerHelper peerHelper,
    IUserAppService userAppService,
    IUserConverterService userConverterService,
    IBusinessConnectionStore connectionStore,
    IBusinessRecipientsReader recipientsReader,
    IBusinessBotNotifier notifier)
    : RpcResultObjectHandler<RequestUpdateConnectedBot, IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestUpdateConnectedBot obj)
    {
        var userId = input.UserId;
        if (obj.Bot is not TInputUser)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        await accessHashHelper.CheckAccessHashAsync(input, obj.Bot);
        var botId = peerHelper.GetPeer(obj.Bot, userId).PeerId;
        var bot = await userAppService.GetAsync(botId);
        if (bot is not { Bot: true })
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        var existing = await connectionStore.GetByUserAsync(userId);
        if (obj.Deleted)
        {
            if (existing != null && existing.BotId == botId)
            {
                await connectionStore.DeleteAsync(userId);
                await connectionStore.ClearPausedAsync(userId);
                await notifier.NotifyConnectionAsync(existing, true);
            }

            return await ResultAsync(input, botId);
        }

        if (!bot!.BotBusiness)
        {
            RpcErrors.RpcErrors400.BotBusinessMissing.ThrowRpcError();
        }

        var recipients = obj.Recipients as TInputBusinessBotRecipients;
        var include = await recipientsReader.ReadUsersAsync(input, recipients?.Users);
        var exclude = await recipientsReader.ReadUsersAsync(input, recipients?.ExcludeUsers);
        var recipientsItem = new BusinessRecipientsItem(recipients?.ExistingChats ?? false,
            recipients?.NewChats ?? false, recipients?.Contacts ?? false, recipients?.NonContacts ?? false,
            recipients?.ExcludeSelected ?? false, include);
        if (BusinessRecipientsReader.IsEmpty(recipientsItem))
        {
            RpcErrors.RpcErrors400.BusinessRecipientsEmpty.ThrowRpcError();
        }

        var sameBot = existing != null && existing.BotId == botId;
        var rights = obj.Rights != null
            ? BusinessBotRightsItem.From(obj.Rights)
            : sameBot ? existing!.Rights : BusinessBotRightsItem.None;
        var connection = new BusinessConnection(userId, botId, connectionStore.NewConnectionId(), recipientsItem,
            exclude, rights, sameBot ? existing!.Date : CurrentDate);

        await connectionStore.SaveAsync(connection);
        if (existing != null && !sameBot)
        {
            await connectionStore.ClearPausedAsync(userId);
            await notifier.NotifyConnectionAsync(existing, true);
        }

        await notifier.NotifyConnectionAsync(connection, false);

        return await ResultAsync(input, botId);
    }

    private async Task<IUpdates> ResultAsync(IRequestInput input, long botId)
    {
        var bot = await userConverterService.GetUserAsync(input, botId, layer: input.Layer);
        return new TUpdates
        {
            Updates = [],
            Users = new TVector<IUser>(bot),
            Chats = [],
            Date = CurrentDate,
            Seq = 0
        };
    }
}
