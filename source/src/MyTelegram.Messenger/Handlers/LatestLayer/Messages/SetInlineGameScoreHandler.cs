using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Use this method to set the score of the specified user in a game sent as an inline message (bots only).
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.setInlineGameScore"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SetInlineGameScoreHandler(
    IQueryProcessor queryProcessor,
    IGameScoreStore gameScoreStore) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSetInlineGameScore, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSetInlineGameScore obj)
    {
        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (botReadModel == null || !botReadModel.Bot)
        {
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();
        }

        if (obj.Score < 0)
        {
            RpcErrors.RpcErrors400.ScoreInvalid.ThrowRpcError();
        }

        if (obj.UserId is not TInputUser inputUser)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return null!;
        }

        var messageId = GetInlineMessageId(obj.Id);
        if (messageId == 0)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var gameKey = IGameScoreStore.InlineGameKey(messageId);
        await gameScoreStore.SetScoreAsync(gameKey, inputUser.UserId, obj.Score, obj.Force);

        // Unlike setGameScore this method returns Bool, so an unchanged score is not an error.
        return new TBoolTrue();
    }

    private static long GetInlineMessageId(IInputBotInlineMessageID id)
    {
        return id switch
        {
            TInputBotInlineMessageID v => v.Id,
            TInputBotInlineMessageID64 v => v.Id,
            _ => 0
        };
    }
}
