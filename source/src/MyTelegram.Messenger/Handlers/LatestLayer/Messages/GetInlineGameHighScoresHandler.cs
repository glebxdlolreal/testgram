using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get highscores of a game sent using an inline bot
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getInlineGameHighScores"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetInlineGameHighScoresHandler(
    IQueryProcessor queryProcessor,
    IGameScoreStore gameScoreStore,
    IUserConverterService userConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetInlineGameHighScores, MyTelegram.Schema.Messages.IHighScores>
{
    protected override async Task<MyTelegram.Schema.Messages.IHighScores> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetInlineGameHighScores obj)
    {
        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (botReadModel == null || !botReadModel.Bot)
        {
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();
        }

        var messageId = GetInlineMessageId(obj.Id);
        if (messageId == 0)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var gameKey = IGameScoreStore.InlineGameKey(messageId);
        var scores = await gameScoreStore.GetHighScoresAsync(gameKey);

        return await GameHighScoresBuilder.BuildAsync(input, scores, queryProcessor, userConverterService);
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
