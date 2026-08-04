using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Returns attachment menu entry for a <a href="https://corefork.telegram.org/api/bots/attach">bot mini app that can be launched from the attachment menu »</a>
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getAttachMenuBot"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetAttachMenuBotHandler(
    IQueryProcessor queryProcessor,
    IAccessHashHelper accessHashHelper,
    IAttachMenuBotStore attachMenuBotStore,
    IUserConverterService userConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetAttachMenuBot, MyTelegram.Schema.IAttachMenuBotsBot>
{
    protected override async Task<MyTelegram.Schema.IAttachMenuBotsBot> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetAttachMenuBot obj)
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

        // A missing entry is not an error: the client asks about bots the user has not added yet,
        // and the inactive flag on the result is what tells it to prompt for confirmation.
        var entry = await attachMenuBotStore.GetAsync(input.UserId, inputBot.UserId);

        return new TAttachMenuBotsBot
        {
            Bot = attachMenuBotStore.ToAttachMenuBot(inputBot.UserId, botReadModel!.UserName ?? string.Empty, entry),
            Users = new TVector<IUser>(userConverterService.ToUser(input, botReadModel, layer: input.Layer))
        };
    }
}
