using MyTelegram.Services.Services.IdGenerator;

namespace MyTelegram.Messenger.Services.Impl;

public class IdGenerator(
    IHiLoValueGeneratorCache cache,
    IHiLoValueGeneratorFactory factory,
    IQueryProcessor queryProcessor,
    IHiLoStateBlockSizeHelper stateBlockSizeHelper,
    ILogger<IdGenerator> logger,
    MyTelegram.Messenger.Services.Pts.IChannelPtsAllocator? channelPtsAllocator = null,
    IMessageIdAllocator? messageIdAllocator = null)
    : IIdGenerator, ITransientDependency
{
    public async Task<int> NextIdAsync(IdType idType,
        long id,
        int step = 1,
        CancellationToken cancellationToken = default)
    {
        return (int)await NextLongIdAsync(idType, id, step, cancellationToken);
    }

    public async Task<long> NextLongIdAsync(IdType idType,
        long id = 0,
        int step = 1,
        CancellationToken cancellationToken = default)
    {
        if (idType == IdType.Pts && id >= MyTelegramConsts.ChannelInitId)
        {
            if (channelPtsAllocator == null)
                throw new InvalidOperationException("The durable channel Pts authority is not registered.");
            return await channelPtsAllocator.ReserveAsync(id, Guid.NewGuid().ToString("N"), step, cancellationToken);
        }

        // Message ids come from a counter every process shares; see IMessageIdAllocator.
        if (idType == IdType.MessageId && messageIdAllocator != null)
        {
            var messageId = await messageIdAllocator.NextAsync(id, step, async () => await GetMaxMessageIdAsync(id), cancellationToken);
            EnsureIdIsInRange(idType, messageId);
            return messageId;
        }

        var sw = Stopwatch.StartNew();

        HiLoValueGeneratorState? state = null;
        switch (idType)
        {
            case IdType.MessageId:
                if (!cache.Exists(idType, id))
                {
                    var maxMessageId = await GetMaxMessageIdAsync(id);
                    state = await GetStateAsync(idType, id, maxMessageId);
                }
                break;
            case IdType.UserId:
                if (!cache.Exists(idType, id))
                {
                    var maxUserId = await GetMaxUserIdAsync();
                    state = await GetStateAsync(idType, id, maxUserId);
                }
                break;
            case IdType.ChannelId:
                {
                    if (!cache.Exists(idType, id))
                    {
                        var maxChannelId = await GetMaxChannelIdAsync();
                        state = await GetStateAsync(idType, id, maxChannelId);
                    }
                }
                break;
        }

        state ??= cache.GetOrAdd(idType, id);

        var generator = factory.Create(state);
        var nextId = await generator.NextAsync(idType, id, cancellationToken);
        sw.Stop();

        if (sw.Elapsed.TotalMilliseconds > 100)
        {
            logger.LogWarning("[{Timespan}] Generate id too slow, idType: {IdType}, id: {Id}", sw.Elapsed, idType, id);
        }

        var generatedId = nextId + GetInitId(idType);
        EnsureIdIsInRange(idType, generatedId);

        return generatedId;
    }

    /// <summary>
    /// Returns the inclusive lower and exclusive upper bound of the id range that belongs to
    /// <paramref name="idType"/>, or <c>null</c> for id types that are not peer ids and therefore
    /// have no range of their own.
    /// </summary>
    private static (long Min, long Max)? GetIdRange(IdType idType)
    {
        return idType switch
        {
            IdType.UserId => (MyTelegramConsts.UserIdInitId, MyTelegramConsts.BotUserInitId),
            IdType.BotUserId => (MyTelegramConsts.BotUserInitId, MyTelegramConsts.ChatIdInitId),
            IdType.ChatId => (MyTelegramConsts.ChatIdInitId, MyTelegramConsts.ChannelInitId),
            IdType.ChannelId => (MyTelegramConsts.ChannelInitId, long.MaxValue),
            _ => null
        };
    }

    /// <summary>
    /// Guards against handing out an id that belongs to a different peer range — for example a
    /// regular user id that lands inside the bot range, which every <c>IsBotUser</c> check would
    /// then treat as a bot (and <c>messages.sendMessage</c> would reject with <c>USER_IS_BOT</c>).
    /// </summary>
    private static void EnsureIdIsInRange(IdType idType, long generatedId)
    {
        if (GetIdRange(idType) is not var (min, max))
        {
            return;
        }

        if (generatedId < min || generatedId >= max)
        {
            throw new InvalidOperationException(
                $"Generated {idType} {generatedId} is outside of its allowed range [{min}, {max}). " +
                "Refusing to hand out an id that belongs to another peer range.");
        }
    }

    private static long GetInitId(IdType idType)
    {
        return idType switch
        {
            IdType.ChannelId => MyTelegramConsts.ChannelInitId,
            IdType.UserId => MyTelegramConsts.UserIdInitId + 10000, // First 10000 for testing
            IdType.BotUserId => MyTelegramConsts.BotUserInitId,
            IdType.ChatId => MyTelegramConsts.ChatIdInitId,
            IdType.Pts => MyTelegramConsts.PtsInitId,
            IdType.FolderId => MyTelegramConsts.FolderInitId,
            _ => 0
        };
    }

    private async Task<long> GetMaxChannelIdAsync()
    {
        var id = await queryProcessor.ProcessAsync(new GetMaxChannelIdQuery());

        return ToRelativeId(IdType.ChannelId, id);
    }

    private async Task<long> GetMaxUserIdAsync()
    {
        var id = await queryProcessor.ProcessAsync(new GetMaxUserIdQuery());

        return ToRelativeId(IdType.UserId, id);
    }

    /// <summary>
    /// The HiLo state tracks the raw sequence value, while the ids stored in the read models are
    /// already offset by <see cref="GetInitId"/>. Seeding the state with a stored id as-is would
    /// add the offset a second time, so strip it back off first. Ids that fall outside the range of
    /// their own type are ignored — a bot id must never seed the regular-user sequence.
    /// </summary>
    private static long ToRelativeId(IdType idType, long storedMaxId)
    {
        if (GetIdRange(idType) is var (min, max) && (storedMaxId < min || storedMaxId >= max))
        {
            return 0;
        }

        var relativeId = storedMaxId - GetInitId(idType);

        return relativeId > 0 ? relativeId : 0;
    }

    /// <summary>
    /// The highest message id ever handed out for <paramref name="ownerPeerId"/>. The message read
    /// model alone is not enough: a deleted message leaves it, so after deleting the newest message and
    /// a restart its id would be handed out again and the next send would fail on the still existing
    /// aggregate. The pts read model keeps a high-water mark that deletions do not lower.
    /// </summary>
    private async Task<int> GetMaxMessageIdAsync(long ownerPeerId)
    {
        int? maxId = await queryProcessor.ProcessAsync(new GetMaxMessageIdByPeerIdQuery(ownerPeerId));
        var pts = await queryProcessor.ProcessAsync(new GetPtsByPeerIdQuery(ownerPeerId));

        return Math.Max(maxId ?? 0, pts?.MaxMessageId ?? 0);
    }

    private async Task<HiLoValueGeneratorState> GetStateAsync(IdType idType, long id, long oldMaxId)
    {
        if (oldMaxId > 0)
        {
            var blockSize = stateBlockSizeHelper.GetBlockSize(idType);
            var high = oldMaxId / blockSize;
            return await cache.GetOrAddAsync(idType, id, () => Task.FromResult(new HiLoValueGeneratorState(blockSize, oldMaxId, (high + 1) * blockSize + 1)));
        }

        return cache.GetOrAdd(idType, id);
    }
}