using MongoDB.Bson;

using MongoDB.Driver;

using MyTelegram.Messenger.Services.Mentions;



namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>

/// Deletes messages by their identifiers.

/// Possible errors

/// Code Type Description

/// 403 BOT_ACCESS_FORBIDDEN The specified method <em>can</em> be used over a <a href="https://corefork.telegram.org/api/bots/connected-business-bots">business connection</a> for some operations, but the specified query attempted an operation that is not allowed over a business connection.

/// 400 BUSINESS_CONNECTION_INVALID The <code>connection_id</code> passed to the wrapping <a href="https://corefork.telegram.org/api/business">invokeWithBusinessConnection</a> call is invalid.

/// 403 MESSAGE_DELETE_FORBIDDEN You can't delete one of the messages you tried to delete, most likely because it is a service message.

/// 400 MESSAGE_ID_INVALID The provided message id is invalid.

/// 400 SELF_DELETE_RESTRICTED Business bots can't delete messages just for the user, <code>revoke</code> <strong>must</strong> be set.

/// <para><c>See <a href="https://corefork.telegram.org/method/messages.deleteMessages"/> </c></para>

/// </summary>

/// <remarks>

/// Access: [User ✔] [Bot ✔] [Anonymous ✖]

/// </remarks>

internal sealed class DeleteMessagesHandler(ICommandBus commandBus, IPtsHelper ptsHelper, IQueryProcessor queryProcessor, IMongoDatabase mongoDatabase, IMentionCleanupService mentionCleanupService, IObjectMessageSender objectMessageSender) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestDeleteMessages, MyTelegram.Schema.Messages.IAffectedMessages>

{

    /// <summary>
    /// How long a dice resists "delete for everyone" in a private chat. TDLib and tdesktop both hardcode
    /// 24 hours for it.
    /// </summary>
    private const long DiceRevokeRestrictionSeconds = 86400;

    protected override async Task<IAffectedMessages> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestDeleteMessages obj)

    {

        if (obj.Id.Count > 0)

        {

            var messageIds = obj.Id.ToList();

            var messageItemsToBeDeletedList = await queryProcessor.ProcessAsync(new GetMessageItemListToBeDeletedQuery(input.UserId, messageIds, obj.Revoke));

            int? newTopMessageId = null;

            int? newTopMessageIdForOtherParticipant = null;

            // Not set top message id for group chat

            if (messageItemsToBeDeletedList.Any(p => p.ToPeerType == PeerType.User))

            {

                newTopMessageId = await queryProcessor.ProcessAsync(new GetTopMessageIdQuery(input.UserId, messageIds));

                if (obj.Revoke)

                {

                    var toPeerMessageItem = messageItemsToBeDeletedList.FirstOrDefault(p => p.OwnerUserId != input.UserId);

                    if (toPeerMessageItem != null)

                    {

                        var toPeerMessageIds = messageItemsToBeDeletedList.Where(p => p.OwnerUserId != input.UserId).Select(p => p.MessageId).ToList();

                        newTopMessageIdForOtherParticipant = await queryProcessor.ProcessAsync(new GetTopMessageIdQuery(toPeerMessageItem.OwnerUserId, toPeerMessageIds));

                    }

                }

            }



            // Refused before anything is mutated: ClearMentionsAsync below settles mention counters that
            // cannot be put back, so a rejection has to happen first.
            if (obj.Revoke)
            {
                await EnsureDiceCanBeRevokedAsync(input.UserId, messageItemsToBeDeletedList);
            }

            // Read models are gone once the delete command lands, so the mention counters of everyone
            // mentioned in them have to be settled while the messages still exist.
            await ClearMentionsAsync(messageItemsToBeDeletedList);

            var command = new StartDeleteMessagesCommand(TempId.New, input.ToRequestInfo(), messageItemsToBeDeletedList, obj.Revoke, obj.Revoke, newTopMessageId, newTopMessageIdForOtherParticipant);

            await commandBus.PublishAsync(command);






            return null !;

        }



        var pts = ptsHelper.GetCachedPts(input.UserId);

        return new TAffectedMessages

        {

            Pts = pts,

            PtsCount = 0

        };

    }



    /// <summary>
    /// A <a href="https://corefork.telegram.org/api/dice">dice</a> in a private chat cannot be deleted for
    /// everyone for its first 24 hours, so a roll cannot be taken back once the other side has seen it.
    /// </summary>
    /// <remarks>
    /// This is the service's own rule, and the clients mirror it locally rather than owning it: TDLib
    /// refuses revoke while <c>unix_time() - m-&gt;date &lt; 86400</c> for <c>MessageContentType::Dice</c>
    /// (<c>MessagesManager.cpp</c>) and tdesktop's <c>MediaDice::allowsRevoke</c> applies the same 24-hour
    /// window. Without the check here a modified client could still hide a losing throw. Saved Messages,
    /// groups and channels are all exempt — <c>allowsRevoke</c> returns true immediately for
    /// <c>peer-&gt;isSelf() || !peer-&gt;isUser()</c>, and there is nobody to hide a roll from in a chat with
    /// yourself.
    /// </remarks>
    private async Task EnsureDiceCanBeRevokedAsync(long userId,
        IReadOnlyCollection<MessageItemToBeDeleted> itemsToBeDeleted)
    {
        var messageIds = itemsToBeDeleted
            .Where(p => p.ToPeerType == PeerType.User && p.OwnerUserId == userId && p.ToPeerId != userId)
            .Select(p => p.MessageId)
            .ToList();

        if (messageIds.Count == 0)
        {
            return;
        }

        var messages = await queryProcessor.ProcessAsync(
            new GetMessagesByOwnerAndMessageIdListQuery(userId, messageIds));

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (messages.Any(p => p.Media2 is TMessageMediaDice && now - p.Date < DiceRevokeRestrictionSeconds))
        {
            RpcErrors.RpcErrors403.MessageDeleteForbidden.ThrowRpcError();
        }
    }

    private async Task ClearMentionsAsync(IReadOnlyCollection<MessageItemToBeDeleted> itemsToBeDeleted)

    {

        foreach (var group in itemsToBeDeleted.GroupBy(p => p.OwnerUserId))

        {

            var messages = await queryProcessor.ProcessAsync(

                new GetMessagesByOwnerAndMessageIdListQuery(group.Key, group.Select(p => p.MessageId).ToList()));

            await mentionCleanupService.OnMessagesDeletedAsync(messages);

        }

    }



}