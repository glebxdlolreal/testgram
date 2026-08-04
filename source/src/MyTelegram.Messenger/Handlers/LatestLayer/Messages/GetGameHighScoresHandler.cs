using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get highscores of a game
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getGameHighScores"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetGameHighScoresHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IGameScoreStore gameScoreStore,
    IUserConverterService userConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetGameHighScores, MyTelegram.Schema.Messages.IHighScores>
{
    protected override async Task<MyTelegram.Schema.Messages.IHighScores> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetGameHighScores obj)
    {
        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (botReadModel == null || !botReadModel.Bot)
        {
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();
        }

        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        var messageReadModel =
            await queryProcessor.ProcessAsync(new GetMessageByIdQuery(MessageId.Create(ownerPeerId, obj.Id).Value));
        if (messageReadModel == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var gameKey = IGameScoreStore.ChatGameKey(peer.PeerId, obj.Id);
        var scores = await gameScoreStore.GetHighScoresAsync(gameKey);

        return await GameHighScoresBuilder.BuildAsync(input, scores, queryProcessor, userConverterService);
    }
}
