using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers;
/// <summary>
/// Invoke a method using a <a href="https://corefork.telegram.org/api/bots/connected-business-bots">Telegram Business Bot connection, see here » for more info, including a list of the methods that can be wrapped in this constructor</a>.Make sure to always send queries wrapped in a <code>invokeWithBusinessConnection</code> to the datacenter ID, specified in the <code>dc_id</code> field of the <a href="https://corefork.telegram.org/constructor/botBusinessConnection">botBusinessConnection</a> that is being used.
/// <para>The inner request runs as the business account, limited to the wrappable methods, to the rights the
/// account granted and to the private chats its recipients select. Everything it publishes carries the bot in
/// <see cref="RequestInfo.BusinessBotId"/>, so sent messages get <c>via_business_bot_id</c> and the bot is
/// answered with <c>updateBotNewBusinessMessage</c>.</para>
/// <para><c>See <a href="https://corefork.telegram.org/method/invokeWithBusinessConnection"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class InvokeWithBusinessConnectionHandler(
    IHandlerHelper handlerHelper,
    IBusinessConnectionStore connectionStore,
    IPeerHelper peerHelper,
    IUserAppService userAppService,
    IQueryProcessor queryProcessor,
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.RequestInvokeWithBusinessConnection, IObject>
{
    private const int CanReplyWindowSeconds = 24 * 60 * 60;

    protected override async Task<IObject> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.RequestInvokeWithBusinessConnection obj)
    {
        var botId = input.UserId;
        var connection = await connectionStore.GetByConnectionIdAsync(obj.ConnectionId);
        if (connection == null || connection.BotId != botId)
        {
            RpcErrors.RpcErrors400.BusinessConnectionInvalid.ThrowRpcError();
        }

        // A user may not call this at all, and a bot whose business mode was switched off loses access.
        var bot = await userAppService.GetAsync(botId);
        if (bot is not { Bot: true, BotBusiness: true })
        {
            RpcErrors.RpcErrors400.BusinessConnectionNotAllowed.ThrowRpcError();
        }

        var rule = BusinessConnectionRightsMap.GetRule(obj.Query);
        if (rule == null)
        {
            RpcErrors.RpcErrors400.BusinessConnectionNotAllowed.ThrowRpcError();
        }

        if (!rule!.IsAllowed(connection!.Rights))
        {
            RpcErrors.RpcErrors403.BotAccessForbidden.ThrowRpcError();
        }

        var userId = connection.UserId;
        if (rule.SelfPeer != null && rule.SelfPeer is not TInputPeerSelf &&
            peerHelper.GetPeer(rule.SelfPeer, userId).PeerId != userId)
        {
            RpcErrors.RpcErrors400.BusinessPeerInvalid.ThrowRpcError();
        }

        if (rule.ChatPeer != null)
        {
            var peer = peerHelper.GetPeer(rule.ChatPeer, userId);
            if (peer.PeerType != PeerType.User || !await connectionStore.AppliesToAsync(connection, peer.PeerId))
            {
                RpcErrors.RpcErrors400.BusinessPeerInvalid.ThrowRpcError();
            }

            if (rule.NeedsRecentIncomingMessage && !await HasRecentIncomingMessageAsync(userId, peer.PeerId))
            {
                RpcErrors.RpcErrors400.BusinessPeerUsageMissing.ThrowRpcError();
            }
        }

        if (obj.Query is MyTelegram.Schema.Messages.RequestDeleteMessages deleteMessages)
        {
            await CheckDeleteAsync(connection, deleteMessages);
        }

        var businessInput = input is RequestInput ri
            ? ri with { UserId = userId }
            : new RequestInput(
                input.ConnectionId,
                input.ConnectionType,
                input.RequestId,
                input.ObjectId,
                input.ReqMsgId,
                input.SeqNumber,
                userId,
                input.AuthKeyId,
                input.PermAuthKeyId,
                input.Layer,
                input.Date,
                input.DeviceType,
                input.ClientIp,
                input.SessionId,
                input.AccessHashKeyId);

        using (BusinessConnectionScope.Enter(botId))
        {
            // Unresolved inner constructors surface 400 INPUT_CONSTRUCTOR_INVALID instead of throwing.
            return await SubQueryExecutor.ExecuteInnerAsync(handlerHelper, businessInput, obj.Query);
        }
    }

    /// <summary>
    /// Deleting needs <c>delete_sent_messages</c> for the account's own messages and
    /// <c>delete_received_messages</c> for the partner's, and only in chats the bot manages.
    /// </summary>
    private async Task CheckDeleteAsync(BusinessConnection connection, MyTelegram.Schema.Messages.RequestDeleteMessages request)
    {
        var ids = request.Id?.ToList() ?? [];
        if (ids.Count == 0)
        {
            return;
        }

        var messages = await queryProcessor.ProcessAsync(
            new GetMessagesByOwnerAndMessageIdListQuery(connection.UserId, ids));
        foreach (var message in messages)
        {
            if (message.ToPeerType != PeerType.User ||
                !await connectionStore.AppliesToAsync(connection, message.ToPeerId))
            {
                RpcErrors.RpcErrors400.BusinessPeerInvalid.ThrowRpcError();
            }

            var allowed = message.SenderUserId == connection.UserId
                ? connection.Rights.DeleteSentMessages
                : connection.Rights.DeleteReceivedMessages;
            if (!allowed)
            {
                RpcErrors.RpcErrors403.BotAccessForbidden.ThrowRpcError();
            }
        }
    }

    private async Task<bool> HasRecentIncomingMessageAsync(long userId, long peerUserId)
    {
        var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - CanReplyWindowSeconds;
        var builder = Builders<BsonDocument>.Filter;
        return await mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel")
            .Find(builder.And(
                builder.Eq("OwnerPeerId", userId),
                builder.Eq("ToPeerId", peerUserId),
                builder.Eq("SenderUserId", peerUserId),
                builder.Gte("Date", since)))
            .AnyAsync();
    }
}
