using System.Buffers.Binary;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Mentions;
using MyTelegram.Messenger.Services.PaidMedia;

namespace MyTelegram.Messenger.Converters.ConverterServices;

public class MessageConverterService(
    IMessageMediaResponseService messageMediaResponseService,
    ILayeredService<IMessageConverter> messageLayeredService,
    ILayeredService<IMessageServiceConverter> messageServiceLayeredService,
    ILayeredService<IMessageFwdHeaderConverter> messageFwdHeaderLayeredService,
    ILayeredService<IPollConverter> pollLayeredService,
    IDataEncryptionHelper dataEncryptionHelper,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    IMongoDatabase mongoDatabase,
    ILogger<MessageConverterService> logger
) : IMessageConverterService, ITransientDependency
{
    private static Dictionary<int, byte[]> _keys = [];
    public IMessage ToMessage(
        long selfUserId,
        IMessageReadModel readModel,
        IPollReadModel? pollReadModel = null,
        List<string>? chosenOptions = null,
        List<IUserReactionReadModel>? userReactionReadModels = null,
        int layer = 0
    )
    {
        IMessageMedia? media = null;
        if (pollReadModel != null)
        {
            media = new TMessageMediaPoll
            {
                Poll = pollLayeredService.GetConverter(layer).ToPoll(pollReadModel),
                Results = pollLayeredService
                    .GetConverter(layer)
                    .ToPollResults(pollReadModel, chosenOptions),
            };
        }

        return ToMessage(selfUserId, readModel, media, userReactionReadModels, layer);
    }

    private IMessage ToMessage(
        long selfUserId,
        IMessageReadModel readModel,
        IMessageMedia? pollMedia = null,
        IReadOnlyCollection<IUserReactionReadModel>? userReactionReadModels = null,
        int layer = 0,
        IReadOnlyDictionary<string, IFactCheck>? factChecks = null,
        IReadOnlyDictionary<string, int>? threadReadMaxIds = null,
        IReadOnlyDictionary<string, string>? fromRanks = null,
        IReadOnlyDictionary<string, MentionReadStateDocument>? mentionReadStates = null
    )
    {
        threadReadMaxIds ??= LoadThreadReadMaxIds(selfUserId, [readModel]);
        fromRanks ??= LoadFromRanks(ToFromRankKeys([readModel]));
        mentionReadStates ??= LoadMentionReadStates(selfUserId, [readModel]);
        var fromId = new Peer(PeerType.User, readModel.SenderPeerId).ToPeer();
        switch (readModel.SendMessageType)
        {
            case SendMessageType.MessageService:
                {
                    var m = messageServiceLayeredService.GetConverter(layer).ToMessage(readModel);
                    if (readModel.ToPeerType == PeerType.Channel)
                    {
                        if (
                            readModel.Post
                            || readModel.MessageActionType == MessageActionType.ChannelCreate
                            || readModel.MessageActionType == MessageActionType.SetMessagesTtl
                            || readModel.MessageActionType == MessageActionType.PaidMessagesPriceUpdated
                        )
                        {
                            fromId = null;
                        }
                    }
                    if (readModel.SendAs != null)
                    {
                        fromId = readModel.SendAs.ToPeer();
                    }

                    m.FromId = fromId;
                    m.Out = readModel.SenderPeerId == selfUserId;

                    if (readModel.MentionedUserIds?.Contains(selfUserId) ?? false)
                    {
                        m.Mentioned = true;
                        m.MediaUnread = IsMentionUnread(mentionReadStates, readModel);
                    }

                    return m;
                }

            default:
                {
                    var m = messageLayeredService.GetConverter(layer).ToMessage(readModel);
                    var media = readModel.Media2 ?? readModel.Media.ToTObject<IMessageMedia>();

                    // Fix null Stickerset in already deserialized documents
                    if (media is TMessageMediaDocument { Document: TDocument doc })
                    {
                        // Ensure Attributes is not null
                        if (doc.Attributes == null)
                        {
                            doc.Attributes = new TVector<IDocumentAttribute>();
                        }

                        foreach (var attr in doc.Attributes)
                        {
                            if (attr is TDocumentAttributeSticker sticker && sticker.Stickerset == null)
                            {
                                sticker.Stickerset = new TInputStickerSetEmpty();
                            }
                            else if (attr is TDocumentAttributeCustomEmoji emoji && emoji.Stickerset == null)
                            {
                                emoji.Stickerset = new TInputStickerSetEmpty();
                            }
                        }
                    }

                    // Enrich documents in media with Attributes2 from database
                    media = EnrichMediaDocuments(media);
                    media = RevealPaidMediaIfPurchased(media, selfUserId, readModel);

                    m.Media = messageMediaResponseService.ToLayeredData(media, layer);
                    m.Out = readModel.SenderPeerId == selfUserId;
                    m.FromId = fromId;
                    if (readModel.FwdHeader != null)
                    {
                        m.FwdFrom = messageFwdHeaderLayeredService
                            .GetConverter(layer)
                            .ToMessageFwdHeader(readModel.FwdHeader);
                    }

                    if (readModel.ToPeerType == PeerType.Channel)
                    {
                        m.Replies = ToMessageReplies(readModel.Post, readModel.Reply,
                            GetThreadReadMaxId(threadReadMaxIds, readModel));
                        if (m.Replies != null && readModel.FwdHeader != null) // forward from linked channel
                        {
                            m.FromId = readModel.FwdHeader.FromId.ToPeer();
                            m.Out = false;
                        }

                        if (readModel.Post)
                        {
                            m.FromId = null;
                        }

                        if (readModel.SendAs != null)
                        {
                            m.FromId = readModel.SendAs.ToPeer();
                        }
                    }

                    if (m.QuickReplyShortcutId.HasValue)
                    {
                        // tdlib rejects a quick reply message that carries from_id.
                        m.Date = 0;
                        m.FromId = null;
                    }

                    if (m.Out)
                    {
                        m.FromScheduled = readModel.FromScheduled;
                    }

                    if (pollMedia != null)
                    {
                        m.Media = pollMedia;
                    }

                    if (readModel.MentionedUserIds?.Contains(selfUserId) ?? false)
                    {
                        m.Mentioned = true;
                        m.MediaUnread = IsMentionUnread(mentionReadStates, readModel);
                    }

                    if (readModel.EncryptedData?.Length > 0)
                    {
                        m.Message = DecryptMessage(readModel.OwnerPeerId, readModel.MessageId, readModel.EncryptedData);
                    }

                    if (string.IsNullOrEmpty(m.Message))
                    {
                        m.Message = string.Empty;
                    }

                    if (m is TMessage message)
                    {
                        ApplyFactCheck(message, factChecks, readModel.OwnerPeerId, readModel.MessageId);
                    }

                    ApplyFromRank(m, fromRanks, readModel.ToPeerType, readModel.ToPeerId, readModel.Post);

                    // Set ChosenOrder for current user's reactions
                    if (m.Reactions is TMessageReactions mr && mr.Results?.Count > 0 && readModel.RecentReactions2 != null)
                    {
                        var myReactionIds = readModel.RecentReactions2
                            .Where(r => r.SenderUserId == selfUserId)
                            .Select((r, i) => (Id: r.Reaction.GetReactionId(), Order: i))
                            .GroupBy(x => x.Id)
                            .ToDictionary(g => g.Key, g => g.First().Order);
                        foreach (var rc in mr.Results.OfType<TReactionCount>())
                        {
                            if (myReactionIds.TryGetValue(rc.Reaction.GetReactionId(), out var order))
                                rc.ChosenOrder = order;
                        }
                        // Mark my reactions in recent_reactions
                        if (mr.RecentReactions != null)
                        {
                            foreach (var rr in mr.RecentReactions.OfType<TMessagePeerReaction>())
                            {
                                if (rr.PeerId is TPeerUser pu && pu.UserId == selfUserId)
                                    rr.My = true;
                            }
                        }

                        // Mark my entry in the paid reaction leaderboard. Anonymous entries carry no
                        // peer in the response, so match them by the server-side sender id.
                        if (mr.TopReactors != null && readModel.TopReactors != null)
                        {
                            var myIndexes = readModel.TopReactors
                                .Select((r, i) => (r, i))
                                .Where(x => x.r.SenderUserId == selfUserId)
                                .Select(x => x.i)
                                .ToHashSet();

                            var index = 0;
                            foreach (var reactor in mr.TopReactors.OfType<TMessageReactor>())
                            {
                                if (myIndexes.Contains(index))
                                {
                                    reactor.My = true;
                                }

                                index++;
                            }
                        }
                    }

                    return m;
                }
        }
    }

    public List<IMessage> ToMessageList(
        long selfUserId,
        IReadOnlyCollection<IMessageReadModel> messageReadModels,
        IReadOnlyCollection<IPollReadModel>? pollReadModels,
        IReadOnlyCollection<IPollAnswerVoterReadModel>? pollAnswerVoterReadModels,
        IReadOnlyCollection<IUserReactionReadModel>? userReactionReadModels,
        int layer = 0
    )
    {
        var messages = new List<IMessage>();
        var pollReadModelsDict = pollReadModels?.ToDictionary(p => p.PollId) ?? [];
        var pollAnswerVoterReadModelsDict =
            pollAnswerVoterReadModels
                ?.GroupBy(p => p.PollId)
                .ToDictionary(k => k.Key, v => v.Select(x => x.Option).ToList()) ?? [];

        var pollConverter = pollLayeredService.GetConverter(layer);
        var factChecks = LoadFactChecks(messageReadModels);
        var threadReadMaxIds = LoadThreadReadMaxIds(selfUserId, messageReadModels);
        var fromRanks = LoadFromRanks(ToFromRankKeys(messageReadModels));
        var mentionReadStates = LoadMentionReadStates(selfUserId, messageReadModels);

        foreach (var readModel in messageReadModels)
        {
            IMessageMedia? media = null;
            if (readModel.PollId.HasValue)
            {
                pollReadModelsDict.TryGetValue(readModel.PollId.Value, out var poll);
                pollAnswerVoterReadModelsDict.TryGetValue(
                    readModel.PollId.Value,
                    out var chosenOptions
                );

                if (poll != null)
                {
                    media = new TMessageMediaPoll
                    {
                        Poll = pollConverter.ToPoll(poll),
                        Results = pollConverter.ToPollResults(poll, chosenOptions),
                    };
                }
            }

            messages.Add(ToMessage(selfUserId, readModel, media, null, layer, factChecks, threadReadMaxIds,
                fromRanks, mentionReadStates));
        }

        return messages;
    }

    public IMessage ToMessage(
        long selfUserId,
        MessageItem item,
        List<long>? userReactionIds = null,
        bool mentioned = false,
        int layer = 0
    )
    {
        var isOut = item.IsOut;
        var fromId = item.SenderPeer.ToPeer();

        if (item.ToPeer.PeerType == PeerType.Channel /*&& selfUserId != 0*/)
        {
            isOut = item.SenderPeer.PeerId == selfUserId;
        }

        switch (item.SendMessageType)
        {
            case SendMessageType.MessageService:
                {
                    if (item.ToPeer.PeerType == PeerType.Channel)
                    {
                        if (
                            item.Post
                            || item.MessageSubType == MessageSubType.CreateChannel
                            || item.MessageSubType == MessageSubType.SetHistoryTtl
                        )
                        {
                            fromId = null;
                        }
                    }

                    var m = messageServiceLayeredService.GetConverter(layer).ToMessage(item);

                    m.Out = isOut;
                    m.Mentioned = mentioned;
                    m.MediaUnread = mentioned;
                    m.FromId = fromId;
                    if (item.SendAs != null)
                    {
                        m.FromId = item.SendAs.ToPeer();
                    }

                    return m;
                }

            default:
                {
                    // Enrich documents in media with Attributes2 from database
                    var enrichedMedia = EnrichMediaDocuments(item.Media);
                    var media = messageMediaResponseService.ToLayeredData(enrichedMedia, layer);
                    var m = messageLayeredService.GetConverter(layer).ToMessage(item);

                    m.Media = media;
                    m.Out = isOut;
                    m.Mentioned = mentioned;
                    m.MediaUnread = mentioned;
                    m.FromId = fromId;

                    if (item.FwdHeader != null)
                    {
                        m.FwdFrom = messageFwdHeaderLayeredService
                            .GetConverter(layer)
                            .ToMessageFwdHeader(item.FwdHeader);
                    }

                    if (item.Post)
                    {
                        m.FromId = null;
                    }

                    if (item.ToPeer.PeerType == PeerType.Channel)
                    {
                        m.Replies = ToMessageReplies(item.Post, item.Reply);
                        if (m.Replies != null && item.FwdHeader?.SavedFromPeer != null) // forward from linked channel
                        {
                            //m.FromId = _peerHelper.ToPeer(PeerType.Channel, item.FwdHeader.SavedFromPeer.PeerId);
                            m.FromId = item.FwdHeader.SavedFromPeer.ToPeer();
                            m.Out = false;
                        }

                        if (item.Post)
                        {
                            m.FromId = null;
                        }

                        if (item.SendAs != null)
                        {
                            m.FromId = item.SendAs.ToPeer();
                        }
                    }


                    if (m.QuickReplyShortcutId.HasValue)
                    {
                        // tdlib rejects a quick reply message that carries from_id.
                        m.Date = 0;
                        m.FromId = null;
                    }

                    if (m.Out)
                    {
                        m.FromScheduled = item.ScheduleDate.HasValue || item.FromScheduled;
                    }

                    if (item.EncryptedData?.Length > 0)
                    {
                        m.Message = DecryptMessage(item.OwnerPeer.PeerId, item.MessageId, item.EncryptedData);
                    }

                    if (item.ToPeer.PeerType == PeerType.Channel && !item.Post)
                    {
                        ApplyFromRank(m, LoadFromRanks([(item.ToPeer.PeerId, item.SenderUserId)]),
                            item.ToPeer.PeerType, item.ToPeer.PeerId, item.Post);
                    }

                    return m;
                }
        }
    }

    private IReadOnlyDictionary<string, IFactCheck> LoadFactChecks(
        IReadOnlyCollection<IMessageReadModel> messageReadModels)
    {
        try
        {
            var keys = messageReadModels
                .Where(p => p.SendMessageType != SendMessageType.MessageService)
                .Select(p => (p.OwnerPeerId, p.MessageId))
                .Distinct()
                .ToList();
            var docs = FactCheckHelper.FindMany(mongoDatabase, keys);
            return docs.ToDictionary(
                p => FactCheckHelper.Key(
                    p.GetValue("OwnerPeerId", 0L).ToInt64(),
                    p.GetValue("MessageId", 0).ToInt32()),
                p => (IFactCheck)FactCheckHelper.ToFactCheck(p, needCheck: true),
                StringComparer.Ordinal);
        }
        catch (MongoException ex)
        {
            logger.LogWarning(ex, "Failed to load factcheck batch");
            return new Dictionary<string, IFactCheck>();
        }
        catch (BsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize factcheck batch");
            return new Dictionary<string, IFactCheck>();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to map factcheck batch");
            return new Dictionary<string, IFactCheck>();
        }
    }

    private static void ApplyFactCheck(
        TMessage message,
        IReadOnlyDictionary<string, IFactCheck>? factChecks,
        long ownerPeerId,
        int messageId)
    {
        if (factChecks?.TryGetValue(FactCheckHelper.Key(ownerPeerId, messageId), out var factCheck) == true)
        {
            message.Factcheck = factCheck;
        }
    }

    /// <summary>
    /// How far the user has read each thread whose root is in <paramref name="messageReadModels"/>,
    /// keyed by <c>{channelId}-{rootMessageId}</c>. Only threads living in the chat itself are looked
    /// up: the comment count shown on a channel post belongs to a thread in the linked discussion
    /// group, whose read state the client obtains from messages.getDiscussionMessage instead.
    /// See https://corefork.telegram.org/api/threads
    /// </summary>
    private IReadOnlyDictionary<string, int> LoadThreadReadMaxIds(long selfUserId,
        IReadOnlyCollection<IMessageReadModel> messageReadModels)
    {
        var empty = new Dictionary<string, int>(StringComparer.Ordinal);
        if (selfUserId <= 0)
        {
            return empty;
        }

        var ids = messageReadModels
            .Where(p => p is { Post: false, Reply: not null, ToPeerType: PeerType.Channel })
            .Select(p => $"{selfUserId}-{p.OwnerPeerId}-{p.MessageId}")
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            return empty;
        }

        try
        {
            var documents = mongoDatabase.GetCollection<BsonDocument>("thread_read_state")
                .Find(Builders<BsonDocument>.Filter.In("_id", ids))
                .ToList();

            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var document in documents)
            {
                var key = $"{document.GetValue("ChannelId", 0L).ToInt64()}-{document.GetValue("TopMsgId", 0).ToInt32()}";
                result[key] = document.GetValue("ReadInboxMaxId", 0).ToInt32();
            }

            return result;
        }
        catch (MongoException ex)
        {
            logger.LogWarning(ex, "Failed to load thread read state batch");
            return empty;
        }
    }

    /// <summary>
    /// Which mentions in <paramref name="messageReadModels"/> the user has already read, keyed by
    /// <c>{peerType}-{peerId}</c>. Without it every mention would keep <c>media_unread</c> set and the
    /// @ badge would come back on the next sync. See https://corefork.telegram.org/api/mentions
    /// </summary>
    private IReadOnlyDictionary<string, MentionReadStateDocument> LoadMentionReadStates(long selfUserId,
        IReadOnlyCollection<IMessageReadModel> messageReadModels)
    {
        var empty = new Dictionary<string, MentionReadStateDocument>(StringComparer.Ordinal);
        if (selfUserId <= 0)
        {
            return empty;
        }

        var peers = messageReadModels
            .Where(p => p.MentionedUserIds?.Contains(selfUserId) ?? false)
            .Select(p => new Peer(p.ToPeerType, p.ToPeerId))
            .Distinct()
            .ToList();

        if (peers.Count == 0)
        {
            return empty;
        }

        try
        {
            var ids = peers.Select(p => $"{selfUserId}-{(int)p.PeerType}-{p.PeerId}").ToList();
            var documents = mongoDatabase.GetCollection<MentionReadStateDocument>("mention_read_state")
                .Find(Builders<MentionReadStateDocument>.Filter.In(p => p.Id, ids))
                .ToList();

            return documents.ToDictionary(
                p => IMentionReadStateService.Key(new Peer((PeerType)p.PeerType, p.PeerId)),
                p => p,
                StringComparer.Ordinal);
        }
        catch (MongoException ex)
        {
            logger.LogWarning(ex, "Failed to load mention read state batch");
            return empty;
        }
    }

    private static bool IsMentionUnread(IReadOnlyDictionary<string, MentionReadStateDocument>? mentionReadStates,
        IMessageReadModel readModel)
    {
        if (mentionReadStates == null)
        {
            return true;
        }

        mentionReadStates.TryGetValue(IMentionReadStateService.Key(new Peer(readModel.ToPeerType, readModel.ToPeerId)),
            out var state);

        return IMentionReadStateService.IsUnread(state, readModel.MessageId);
    }

    /// <summary>
    /// The tag of every message sender listed in <paramref name="senders"/>, keyed by
    /// <c>{channelId}-{userId}</c>. message.from_rank always has to carry the <i>current</i> tag, so
    /// it is resolved when the message is read instead of being frozen into it when it was sent.
    /// See https://corefork.telegram.org/api/rank
    /// </summary>
    private IReadOnlyDictionary<string, string> LoadFromRanks(
        IReadOnlyCollection<(long ChannelId, long UserId)> senders)
    {
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        var pairs = senders.Where(p => p is { ChannelId: > 0, UserId: > 0 }).Distinct().ToList();
        if (pairs.Count == 0)
        {
            return empty;
        }

        try
        {
            var ids = pairs.Select(p => ChannelMemberId.Create(p.ChannelId, p.UserId).Value).ToList();
            var documents = mongoDatabase.GetCollection<BsonDocument>("eventflow-channelmemberreadmodel")
                .Find(Builders<BsonDocument>.Filter.In("_id", ids))
                .Project(Builders<BsonDocument>.Projection
                    .Include("ChannelId")
                    .Include("UserId")
                    .Include("Rank"))
                .ToList();

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var document in documents)
            {
                var rank = document.GetValue("Rank", BsonNull.Value);
                if (rank.BsonType != BsonType.String || rank.AsString.Length == 0)
                {
                    continue;
                }

                var key = FromRankKey(document.GetValue("ChannelId", 0L).ToInt64(),
                    document.GetValue("UserId", 0L).ToInt64());
                result[key] = rank.AsString;
            }

            return result;
        }
        catch (MongoException ex)
        {
            logger.LogWarning(ex, "Failed to load from_rank batch");
            return empty;
        }
    }

    private static string FromRankKey(long channelId, long userId)
    {
        return $"{channelId}-{userId}";
    }

    /// <summary>
    /// The (supergroup, sender) pairs whose tag has to be resolved. Channel posts carry no sender and
    /// therefore no tag.
    /// </summary>
    private static List<(long ChannelId, long UserId)> ToFromRankKeys(
        IReadOnlyCollection<IMessageReadModel> messageReadModels)
    {
        return messageReadModels
            .Where(p => p is { ToPeerType: PeerType.Channel, Post: false, SendMessageType: not SendMessageType.MessageService })
            .Select(p => (p.ToPeerId, p.SenderPeerId))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Sets message.from_rank for a message sent by a member of a supergroup. Channel posts and
    /// messages sent on behalf of a peer other than a user carry no tag.
    /// </summary>
    private static void ApplyFromRank(IMessage message,
        IReadOnlyDictionary<string, string>? fromRanks,
        PeerType toPeerType,
        long toPeerId,
        bool post)
    {
        if (fromRanks == null || fromRanks.Count == 0 || post || toPeerType != PeerType.Channel)
        {
            return;
        }

        if (message is not TMessage tMessage || tMessage.FromId is not TPeerUser fromUser)
        {
            return;
        }

        if (fromRanks.TryGetValue(FromRankKey(toPeerId, fromUser.UserId), out var rank))
        {
            tMessage.FromRank = rank;
        }
    }

    private static int? GetThreadReadMaxId(IReadOnlyDictionary<string, int>? threadReadMaxIds,
        IMessageReadModel readModel)
    {
        if (threadReadMaxIds == null)
        {
            return null;
        }

        return threadReadMaxIds.TryGetValue($"{readModel.OwnerPeerId}-{readModel.MessageId}", out var readMaxId)
            ? readMaxId
            : null;
    }

    protected IMessageReplies? ToMessageReplies(bool post, MessageReply? reply, int? readMaxId = null)
    {
        if (reply == null)
        {
            return null;
        }

        if (post)
        {
            if (reply.ChannelId == null)
            {
                return null;
            }
        }

        var messageReplies = new TMessageReplies
        {
            Comments = post,
            MaxId = reply.MaxId,
            Replies = reply.Replies,
            RepliesPts = reply.RepliesPts,
            ReadMaxId = readMaxId
        };

        if (post)
        {
            messageReplies.ChannelId = reply.ChannelId;
            messageReplies.RecentRepliers = [];
            if (reply.RecentRepliers?.Count > 0)
            {
                messageReplies.RecentRepliers = [.. reply.RecentRepliers.Select(p => p.ToPeer())];
            }
        }

        return messageReplies;
    }

    private void InitKeys()
    {
        if (_keys.Count == 0)
        {
            _keys = options.CurrentValue.EncryptionConfig.MessageKeys.ToDictionary(k => k.Id,
                v => Encoding.UTF8.GetBytes(v.Key));
        }
    }

    public string DecryptMessage(long ownerPeerId, int messageId, ReadOnlyMemory<byte>? encryptedData)
    {
        if (encryptedData == null || encryptedData.Value.IsEmpty)
        {
            return string.Empty;
        }
        //var key=op

        InitKeys();

        var encryptedSpan = encryptedData.Value.Span;
        var keyId = BinaryPrimitives.ReadInt32LittleEndian(encryptedSpan);

        if (!_keys.TryGetValue(keyId, out var masterKey))
        {
            return string.Empty;
        }
        var tempBytes = ArrayPool<byte>.Shared.Rent(encryptedSpan.Length);
        try
        {
            var span = tempBytes.AsSpan(0, encryptedSpan.Length);
            var count = dataEncryptionHelper.Decrypt(masterKey, ownerPeerId, encryptedSpan.Slice(4), span);
            return Encoding.UTF8.GetString(span.Slice(0, count));
        }
        catch (System.Security.Cryptography.AuthenticationTagMismatchException)
        {
            logger.LogWarning("Failed to decrypt message: ownerPeerId={OwnerPeerId}, messageId={MessageId}", ownerPeerId, messageId);
            return string.Empty;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tempBytes);
        }
    }

    private IMessageMedia? RevealPaidMediaIfPurchased(IMessageMedia? media, long selfUserId, IMessageReadModel readModel)
    {
        if (media is not TMessageMediaPaidMedia paidMedia)
            return media;

        try
        {
            var doc = mongoDatabase.GetCollection<PaidMediaDocument>(PaidMediaHelper.CollectionName)
                .Find(x => x.Id == PaidMediaHelper.MakeId(readModel.OwnerPeerId, readModel.MessageId))
                .FirstOrDefault();
            if (doc == null || !PaidMediaHelper.IsPurchasedBy(doc, selfUserId))
                return media;

            paidMedia.ExtendedMedia = PaidMediaHelper.ToRevealedExtendedMedia(doc.ExtendedMedia);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to reveal paid media: ownerPeerId={OwnerPeerId}, msgId={MsgId}", readModel.OwnerPeerId, readModel.MessageId);
        }

        return paidMedia;
    }

    private IMessageMedia? EnrichMediaDocuments(IMessageMedia? media)
    {
        if (media is not TMessageMediaDocument mediaDoc || mediaDoc.Document is not TDocument doc)
        {
            return media;
        }

        try
        {
            // Load document from MongoDB to get Attributes2 with stickerset info
            var docCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
            var filter = Builders<BsonDocument>.Filter.Eq("DocumentId", doc.Id);
            var docBson = docCol.Find(filter).FirstOrDefault();

            if (docBson != null && docBson.Contains("Attributes2") && docBson["Attributes2"] != BsonNull.Value)
            {
                // Deserialize Attributes2 - handle both short and full type names
                var attributes2Array = docBson["Attributes2"].AsBsonArray;
                var attributes2 = new List<IDocumentAttribute>();

                foreach (var attrBson in attributes2Array)
                {
                    var attrDoc = attrBson.AsBsonDocument;
                    var typeName = attrDoc["_t"].AsString;

                    // Handle both "TDocumentAttributeSticker" and "MyTelegram.Schema.TDocumentAttributeSticker"
                    if (typeName.EndsWith("TDocumentAttributeSticker"))
                    {
                        var stickerAttr = new TDocumentAttributeSticker
                        {
                            Alt = attrDoc.Contains("Alt") ? attrDoc["Alt"].AsString : "",
                            Mask = attrDoc.Contains("Mask") && attrDoc["Mask"].AsBoolean
                        };

                        if (attrDoc.Contains("Stickerset") && attrDoc["Stickerset"] != BsonNull.Value)
                        {
                            var stickersetDoc = attrDoc["Stickerset"].AsBsonDocument;
                            var stickersetType = stickersetDoc["_t"].AsString;

                            if (stickersetType.EndsWith("TInputStickerSetID"))
                            {
                                stickerAttr.Stickerset = new TInputStickerSetID
                                {
                                    Id = stickersetDoc["Id"].ToInt64(),
                                    AccessHash = stickersetDoc["AccessHash"].ToInt64()
                                };
                            }
                            else if (stickersetType.EndsWith("TInputStickerSetShortName"))
                            {
                                stickerAttr.Stickerset = new TInputStickerSetShortName
                                {
                                    ShortName = stickersetDoc["ShortName"].AsString
                                };
                            }
                            else
                            {
                                stickerAttr.Stickerset = new TInputStickerSetEmpty();
                            }
                        }
                        else
                        {
                            // CRITICAL: Always set Stickerset to prevent NullReferenceException
                            stickerAttr.Stickerset = new TInputStickerSetEmpty();
                        }

                        attributes2.Add(stickerAttr);
                    }
                    // Add other attribute types here if needed
                }

                if (attributes2.Count > 0)
                {
                    doc.Attributes = new TVector<IDocumentAttribute>(attributes2);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "EnrichMediaDocuments failed for doc {DocumentId}", doc.Id);
        }

        return media;
    }
}
