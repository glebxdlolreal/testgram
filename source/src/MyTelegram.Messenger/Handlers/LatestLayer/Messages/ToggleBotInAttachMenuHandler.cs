using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Enable or disable <a href="https://corefork.telegram.org/api/bots/attach">web bot attachment menu »</a>
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.toggleBotInAttachMenu"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleBotInAttachMenuHandler(
    IQueryProcessor queryProcessor,
    IAccessHashHelper accessHashHelper,
    IAttachMenuBotStore attachMenuBotStore,
    IObjectMessageSender objectMessageSender) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestToggleBotInAttachMenu, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestToggleBotInAttachMenu obj)
    {
        if (obj.Bot is not TInputUser inputBot)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
            return null!;
        }

        await accessHashHelper.CheckAccessHashAsync(input, inputBot.UserId, inputBot.AccessHash);

        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(inputBot.UserId));
        if (botReadModel == null || !botReadModel.Bot)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        await attachMenuBotStore.SetEnabledAsync(input.UserId, inputBot.UserId, obj.Enabled, obj.WriteAllowed);

        // Other sessions of this user need to refetch the menu.
        await objectMessageSender.PushMessageToPeerAsync(
            new Peer(PeerType.User, input.UserId),
            new TUpdates
            {
                Updates = new TVector<IUpdate>(new TUpdateAttachMenuBots()),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = DateTime.UtcNow.ToTimestamp(),
                Seq = 0
            });

        return new TBoolTrue();
    }
}
