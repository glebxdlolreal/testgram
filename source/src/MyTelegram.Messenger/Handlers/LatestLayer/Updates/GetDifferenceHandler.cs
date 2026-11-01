using MyTelegram.Messenger.Services.SecretChat;
using MyTelegram.Schema.Updates;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Updates;
/// <summary>
/// Get new <a href="https://corefork.telegram.org/api/updates">updates</a>.
/// Possible errors
/// Code Type Description
/// 400 CDN_METHOD_INVALID You can't call this method in a CDN DC.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 DATE_EMPTY Date empty.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PERSISTENT_TIMESTAMP_EMPTY Persistent timestamp empty.
/// 400 PERSISTENT_TIMESTAMP_INVALID Persistent timestamp invalid.
/// 500 RANDOM_ID_DUPLICATE You provided a random ID that was already used.
/// 400 USERNAME_INVALID The provided username is not valid.
/// 400 USER_NOT_PARTICIPANT You're not a member of this supergroup/channel.
/// <para><c>See <a href="https://corefork.telegram.org/method/updates.getDifference"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetDifferenceHandler(
    MyTelegram.Messenger.Services.Bots.IBotQtsCounter botQtsCounter,
    IMessageAppService messageAppService, IPtsHelper ptsHelper, IQueryProcessor queryProcessor, IAckCacheService ackCacheService, IDifferenceConverterService differenceConverterService, ISecretChatMessageStore secretChatMessageStore) : RpcResultObjectHandler<RequestGetDifference, IDifference>
{
    protected override async Task<IDifference> HandleCoreAsync(IRequestInput input, RequestGetDifference obj)
    {
        var userId = input.UserId;
        if (userId == 0)
        {
            return new TDifferenceEmpty
            {
                Date = CurrentDate
            };
        }

        var cachedPts = ptsHelper.GetCachedPts(userId);
        var ptsReadModel = await queryProcessor.ProcessAsync(new GetPtsByPeerIdQuery(userId));
        var ptsForAuthKeyIdReadModel = await queryProcessor.ProcessAsync(new GetPtsByPermAuthKeyIdQuery(userId, input.PermAuthKeyId));
        // A device with no cursor row of its own must not replay the channel stream from sequence 0:
        // that returns the entire history, truncated to a full page, which is reported as a slice
        // forever while the cursor stays absent — the client polls getDifference without converging.
        // The user's own box sequence is the correct "current as of now" starting point.
        var globalSeqNo = ptsForAuthKeyIdReadModel?.GlobalSeqNo ?? ptsReadModel?.GlobalSeqNo ?? 0;
        var joinedChannelIdList = await queryProcessor.ProcessAsync(new GetChannelIdListByMemberUserIdQuery(input.UserId));
        // Clamp both ends. A negative pts_total_limit used to survive Math.Min and reach the read-model
        // queries, where FindOptions.Limit is only applied when > 0 — so a negative value meant NO limit
        // and replayed the caller's entire update box in one response.
        var limit = obj.PtsTotalLimit is > 0
            ? Math.Min(obj.PtsTotalLimit.Value, MyTelegramConsts.DefaultPtsTotalLimit)
            : MyTelegramConsts.DefaultPtsTotalLimit;

        // A session that is further behind than a page-by-page catch-up can realistically close is
        // told to resync from scratch. Paging only advances the server cursor once the client acks
        // the page it was sent, so a gap this wide otherwise means hundreds of slice round-trips that
        // restart on any dropped ack — the client polls getDifference forever without progressing.
        //
        // pts = 0 means the caller holds no state at all. It cannot be used as a lower bound (the
        // read-model filter is skipped for it and the whole box comes back, truncated to a full page
        // that forever re-reports itself as a slice), so it takes the same resync path.
        var boxPts = Math.Max(ptsReadModel?.Pts ?? 0, cachedPts);
        if (boxPts > 0 && (obj.Pts <= 0 || boxPts - obj.Pts > MyTelegramConsts.DifferenceTooLongPtsGap))
        {
            return new TDifferenceTooLong { Pts = boxPts };
        }

        // Secret-chat handshake updates (updateEncryption etc.). They carry pts = 0 and are device-scoped,
        // so they live outside the pts box and are replayed by GlobalSeqNo instead. A caller with no
        // permanent Authorization_Key is skipped entirely: the ExcludeAuthKeyId predicate would match
        // every row against 0, and such a device cannot hold a secret chat in the first place.
        IReadOnlyCollection<IUpdatesReadModel> userUpdates = input.PermAuthKeyId == 0
            ? []
            : await queryProcessor.ProcessAsync(
                new GetUpdatesByGlobalSeqNoQuery(input.UserId, input.PermAuthKeyId, globalSeqNo, limit));
        var updatesReadModels = await queryProcessor.ProcessAsync(new GetUpdatesQuery(input.UserId, input.UserId, obj.Pts, obj.Date, limit));
        var messageIds = updatesReadModels.Where(p => p.UpdatesType == UpdatesType.NewMessages).Select(p => p.MessageId ?? 0).ToList();
        // all channel updates
        var channelUpdatesReadModels = await queryProcessor.ProcessAsync(new GetChannelUpdatesByGlobalSeqNoQuery(joinedChannelIdList.ToList(), globalSeqNo, limit, input.UserId));
        if (channelUpdatesReadModels.Any(p => p.OnlySendToUserId.HasValue))
        {
            var tempChannelReadModels = channelUpdatesReadModels.ToList();
            tempChannelReadModels.RemoveAll(p => p.OnlySendToUserId.HasValue && p.OnlySendToUserId != input.UserId);
            channelUpdatesReadModels = tempChannelReadModels;
        }

        var users = updatesReadModels.SelectMany(p => p.Users ?? []).ToList();
        var chats = updatesReadModels.SelectMany(p => p.Chats ?? []).ToList();
        users.AddRange(channelUpdatesReadModels.SelectMany(p => p.Users ?? []).ToList());
        chats.AddRange(channelUpdatesReadModels.SelectMany(p => p.Chats ?? []).ToList());
        chats.AddRange(channelUpdatesReadModels.Select(p => p.OwnerPeerId));
        // Replayed bot updates (business messages and the like) name their users and chats too.
        users.AddRange(userUpdates.SelectMany(p => p.Users ?? []));
        chats.AddRange(userUpdates.SelectMany(p => p.Chats ?? []));
        var dto = await messageAppService.GetChannelDifferenceAsync(new GetDifferenceInput(input.UserId, input.UserId, obj.Pts, limit, messageIds, users, chats));
        var allUpdateList = updatesReadModels.Where(p => p.UpdatesType == UpdatesType.Updates).SelectMany(p => p.Updates ?? []).ToList();
        allUpdateList.AddRange(channelUpdatesReadModels.Where(p => p.UpdatesType == UpdatesType.Updates).SelectMany(p => p.Updates ?? []));
        allUpdateList.AddRange(userUpdates.SelectMany(p => p.Updates ?? []));
        allUpdateList = StoredUpdateNormalizer.Normalize(allUpdateList).ToList();
        // Both replayed streams are capped by the same limit and truncate independently of each other.
        var channelTruncated = channelUpdatesReadModels.Count >= limit;
        var userTruncated = userUpdates.Count >= limit;
        if (updatesReadModels.Count > 0 || channelUpdatesReadModels.Count > 0 || userUpdates.Count > 0)
        {
            var maxPts = updatesReadModels.Count > 0 ? updatesReadModels.Max(p => p.Pts) : obj.Pts;
            var channelMaxGlobalSeqNo = channelUpdatesReadModels.Count > 0 ? channelUpdatesReadModels.Max(p => p.GlobalSeqNo) : 0; //updatesReadModels.Max(p => p.GlobalSeqNo);
            var userGlobalSeqNo = userUpdates.Count > 0 ? userUpdates.Max(p => p.GlobalSeqNo) : 0;
            var maxGlobalSeqNo = Math.Max(channelMaxGlobalSeqNo, userGlobalSeqNo);

            // One GlobalSeqNo cursor is shared by two streams that truncate independently. Taking the
            // plain max would let a full channel page be skipped past by a higher secret-chat seq, losing
            // every channel update in between for good. Clamp to a truncated stream's own maximum: at
            // worst a handful of updateEncryption rows are re-delivered next round, which is idempotent.
            if (channelTruncated)
            {
                maxGlobalSeqNo = Math.Min(maxGlobalSeqNo, channelMaxGlobalSeqNo);
            }

            if (userTruncated)
            {
                maxGlobalSeqNo = Math.Min(maxGlobalSeqNo, userGlobalSeqNo);
            }

            await ackCacheService.AddRpcPtsToCacheAsync(input.ReqMsgId, maxPts, maxGlobalSeqNo, new Peer(PeerType.User, input.UserId), true);
        }

        dto.MessageList = dto.MessageList.OrderBy(p => p.MessageId).ToList();

        // Secret-chat updates: unacked messages with qts > obj.Qts, plus the per-Authorization_Key qts watermark.
        // qts_limit bounds this page independently of pts_total_limit. TDLib confirms its qts by calling
        // getDifference(pts, pts_limit: 1, qts, qts_limit: 1) purely as an ack ping, so honouring it keeps
        // that ping from dragging back a full page of ciphertext.
        // The watermark is read FIRST and bounds the page: it is min(delivered, lowest in-flight
        // allocation - 1), so any row above it belongs to a send whose predecessor is not yet written, and
        // returning it would let the truncated-page cursor below step over that predecessor permanently.
        var safeQts = await secretChatMessageStore.GetHighestQtsAsync(input.UserId, input.PermAuthKeyId);
        var qtsLimit = obj.QtsLimit is > 0 ? Math.Min(obj.QtsLimit.Value, limit) : limit;
        var encryptedMessages = await secretChatMessageStore.GetForDifferenceAsync(input.UserId, input.PermAuthKeyId, obj.Qts, qtsLimit, maxQts: safeQts);

        // A full page means the tail was cut off. Advertising the global watermark here would make the
        // client skip past the messages it did not receive, losing them permanently, so report only the
        // qts actually covered by this response and force the slice form so the client asks again.
        // This must compare against the SAME bound that was passed to GetForDifferenceAsync.
        var encryptedMessagesTruncated = qtsLimit > 0 && encryptedMessages.Count >= qtsLimit;
        var secretChatQts = encryptedMessagesTruncated
            ? encryptedMessages[^1].Qts
            : safeQts;

        // A bot's qts updates (callback queries, business messages) count from the shared bot counter.
        if (!encryptedMessagesTruncated)
        {
            secretChatQts = Math.Max(secretChatQts, await botQtsCounter.GetAsync(input.UserId));
        }

        var r = differenceConverterService.ToDifference(input, dto, ptsReadModel, cachedPts, limit, allUpdateList, [], encryptedMessages, secretChatQts, encryptedMessagesTruncated, updatesTruncated: channelTruncated || userTruncated, layer: input.Layer);
        //logger.LogInformation("{UserId},Layer={Layer},res:{@Res}", input.UserId, input.Layer, r);
        return r;
    }
}