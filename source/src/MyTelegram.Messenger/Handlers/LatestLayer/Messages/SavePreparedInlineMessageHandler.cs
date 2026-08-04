using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Save a <a href="https://corefork.telegram.org/api/bots/inline#21-using-a-prepared-inline-message">prepared inline message</a>, to be shared by the user of the mini app using a <a href="https://corefork.telegram.org/api/web-events#web-app-send-prepared-message">web_app_send_prepared_message event</a>
/// Possible errors
/// Code Type Description
/// 400 RESULT_ID_INVALID One of the specified result IDs is invalid.
/// 400 SEND_MESSAGE_GAME_INVALID An inputBotInlineMessageGame can only be contained in an inputBotInlineResultGame, not in an inputBotInlineResult/inputBotInlineResultPhoto/etc.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.savePreparedInlineMessage"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SavePreparedInlineMessageHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSavePreparedInlineMessage, MyTelegram.Schema.Messages.IBotPreparedInlineMessage>
{
    private const string Collection = "prepared_inline_messages";

    /// <summary>A prepared message stays shareable for 30 days, matching upstream behaviour.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    protected override async Task<MyTelegram.Schema.Messages.IBotPreparedInlineMessage> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSavePreparedInlineMessage obj)
    {
        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (botReadModel == null || !botReadModel.Bot)
        {
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();
        }

        if (obj.UserId is not TInputUser inputUser)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return null!;
        }

        var targetUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(inputUser.UserId));
        if (targetUser == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        // inputBotInlineMessageGame is only valid inside inputBotInlineResultGame.
        if (obj.Result is not TInputBotInlineResultGame &&
            GetSendMessage(obj.Result) is TInputBotInlineMessageGame)
        {
            RpcErrors.RpcErrors400.SendMessageGameInvalid.ThrowRpcError();
        }

        var resultId = GetResultId(obj.Result);
        if (string.IsNullOrEmpty(resultId))
        {
            RpcErrors.RpcErrors400.ResultIdInvalid.ThrowRpcError();
        }

        var id = Guid.NewGuid().ToString("N");
        var expireDate = DateTime.UtcNow.Add(Retention).ToTimestamp();

        await mongoDatabase.GetCollection<BsonDocument>(Collection).InsertOneAsync(new BsonDocument
        {
            ["_id"] = $"prepared-inline-{id}",
            ["id"] = id,
            ["bot_id"] = input.UserId,
            ["user_id"] = inputUser.UserId,
            ["result"] = obj.Result.ToBytes(),
            ["peer_types"] = obj.PeerTypes?.ToBytes() ?? Array.Empty<byte>(),
            ["created_at"] = DateTime.UtcNow.ToTimestamp(),
            ["expire_date"] = expireDate
        });

        return new MyTelegram.Schema.Messages.TBotPreparedInlineMessage
        {
            Id = id,
            ExpireDate = expireDate
        };
    }

    private static string? GetResultId(IInputBotInlineResult result)
    {
        return result switch
        {
            TInputBotInlineResult r => r.Id,
            TInputBotInlineResultPhoto r => r.Id,
            TInputBotInlineResultDocument r => r.Id,
            TInputBotInlineResultGame r => r.Id,
            _ => null
        };
    }

    private static IInputBotInlineMessage? GetSendMessage(IInputBotInlineResult result)
    {
        return result switch
        {
            TInputBotInlineResult r => r.SendMessage,
            TInputBotInlineResultPhoto r => r.SendMessage,
            TInputBotInlineResultDocument r => r.SendMessage,
            TInputBotInlineResultGame r => r.SendMessage,
            _ => null
        };
    }
}
