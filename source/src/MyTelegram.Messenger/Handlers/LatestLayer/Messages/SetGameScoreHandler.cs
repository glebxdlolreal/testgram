using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Use this method to set the score of the specified user in a game sent as a normal message (bots only).
/// Possible errors
/// Code Type Description
/// 400 BOT_SCORE_NOT_MODIFIED The score wasn't modified.
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 SCORE_INVALID The specified game score is invalid.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.setGameScore"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SetGameScoreHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IGameScoreStore gameScoreStore,
    IMessageAppService messageAppService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSetGameScore, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSetGameScore obj)
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

        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        var messageReadModel =
            await queryProcessor.ProcessAsync(new GetMessageByIdQuery(MessageId.Create(ownerPeerId, obj.Id).Value));
        if (messageReadModel == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var gameKey = IGameScoreStore.ChatGameKey(peer.PeerId, obj.Id);
        var changed = await gameScoreStore.SetScoreAsync(gameKey, inputUser.UserId, obj.Score, obj.Force);
        if (!changed)
        {
            RpcErrors.RpcErrors400.BotScoreNotModified.ThrowRpcError();
        }

        if (obj.EditMessage)
        {
            // Other chat members learn about the new score through this service message, which is
            // the cue clients use to re-query the leaderboard.
            var sendInput = new SendMessageInput(
                input.ToRequestInfo() with { ReqMsgId = 0 },
                input.UserId,
                peer,
                string.Empty,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: new TMessageActionGameScore
                {
                    GameId = 0,
                    Score = obj.Score
                });

            await messageAppService.SendMessageAsync([sendInput]);
        }

        // The service message, if any, reaches clients through the push pipeline.
        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = DateTime.UtcNow.ToTimestamp(),
            Seq = 0
        };
    }
}
