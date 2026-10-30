namespace MyTelegram.Domain.Sagas;

[JsonConverter(typeof(SystemTextJsonSingleValueObjectConverter<DeleteReplyMessagesSagaId>))]
public class DeleteReplyMessagesSagaId(string value) : Identity<DeleteReplyMessagesSagaId>(value), ISagaId;

public class DeleteReplyMessagesSagaLocator : DefaultSagaLocator<DeleteReplyMessagesSaga, DeleteReplyMessagesSagaId>
{
    protected override DeleteReplyMessagesSagaId CreateSagaId(string requestId)
    {
        return new DeleteReplyMessagesSagaId(requestId);
    }
}

public class DeleteReplyMessagesSagaState : AggregateState<DeleteReplyMessagesSaga, DeleteReplyMessagesSagaId,
    DeleteReplyMessagesSagaState>,
    IApply<DeleteReplyMessagesSagaStartedSagaEvent>,
    IApply<DeleteReplyMessagePtsIncrementedSagaEvent>,
    IApply<DeleteReplyMessageAcknowledgedSagaEvent>,
    IApply<DeleteReplyMessagesCompletedSagaEvent>
{
    public long ChannelId { get; private set; }
    public int Pts { get; set; }
    public List<int> MessageIds { get; set; } = new();
    public int TotalCount { get; private set; }
    public int DeletedCount { get; private set; }
    public HashSet<int> AcknowledgedMessageIds { get; } = new();
    public List<int> ActuallyDeletedMessageIds { get; } = new();
    public int NewTopMessageId { get; private set; }
    public void Apply(DeleteReplyMessagesSagaStartedSagaEvent aggregateEvent)
    {
        ChannelId = aggregateEvent.ChannelId;
        MessageIds = aggregateEvent.MessageIds.Distinct().ToList();
        TotalCount = MessageIds.Count;
        NewTopMessageId = aggregateEvent.NewTopMessageId;
    }

    public void Apply(DeleteReplyMessagePtsIncrementedSagaEvent aggregateEvent)
    {
        if (Pts < aggregateEvent.Pts)
        {
            Pts = aggregateEvent.Pts;
        }
        DeletedCount++;
    }

    public void Apply(DeleteReplyMessageAcknowledgedSagaEvent aggregateEvent)
    {
        if (AcknowledgedMessageIds.Add(aggregateEvent.MessageId) && aggregateEvent.WasDeleted)
            ActuallyDeletedMessageIds.Add(aggregateEvent.MessageId);
    }

    public void Apply(DeleteReplyMessagesCompletedSagaEvent aggregateEvent)
    {

    }
}

public class DeleteReplyMessagesSaga : MyInMemoryAggregateSaga<DeleteReplyMessagesSaga, DeleteReplyMessagesSagaId,
        DeleteReplyMessagesSagaLocator>,
    ISagaIsStartedBy<TempAggregate, TempId, DeleteReplyMessagesStartedEvent>,
    ISagaHandles<MessageAggregate, MessageId, ChannelMessageDeletedEvent>,
    ISagaHandles<MessageAggregate, MessageId, ChannelMessageDeletionAcknowledgedEvent>
{
    private readonly IIdGenerator _idGenerator;
    private readonly DeleteReplyMessagesSagaState _state = new();

    public DeleteReplyMessagesSaga(DeleteReplyMessagesSagaId id, IEventStore eventStore, IIdGenerator idGenerator) : base(id, eventStore)
    {
        _idGenerator = idGenerator;
        Register(_state);
    }

    public Task HandleAsync(IDomainEvent<TempAggregate, TempId, DeleteReplyMessagesStartedEvent> domainEvent, ISagaContext sagaContext, CancellationToken cancellationToken)
    {
        Emit(new DeleteReplyMessagesSagaStartedSagaEvent(domainEvent.AggregateEvent.RequestInfo,
            domainEvent.AggregateEvent.ChannelId,
            domainEvent.AggregateEvent.MessageIds,
            domainEvent.AggregateEvent.NewTopMessageId
        ));

        foreach (var messageId in domainEvent.AggregateEvent.MessageIds)
        {
            var command = new DeleteChannelMessageCommand(MessageId.Create(domainEvent.AggregateEvent.ChannelId, messageId),
                domainEvent.AggregateEvent.RequestInfo);
            Publish(command);
        }

        return Task.CompletedTask;
    }

    public async Task HandleAsync(IDomainEvent<MessageAggregate, MessageId, ChannelMessageDeletedEvent> domainEvent, ISagaContext sagaContext, CancellationToken cancellationToken)
    {
        // Native moderation has its own durable completion; never allocate a second pts here.
        if (domainEvent.AggregateEvent.RequestInfo.UserId == MyTelegramConsts.AntiSpamBotUserId)
            return;
        var messageId = domainEvent.AggregateEvent.MessageId;
        if (!_state.MessageIds.Contains(messageId) || _state.AcknowledgedMessageIds.Contains(messageId))
            return;
        await IncrementPtsAsync(domainEvent.AggregateEvent.ChannelId);
        Emit(new DeleteReplyMessageAcknowledgedSagaEvent(messageId, true));
        HandleDeleteCompleted();
    }

    public Task HandleAsync(IDomainEvent<MessageAggregate, MessageId, ChannelMessageDeletionAcknowledgedEvent> domainEvent, ISagaContext sagaContext, CancellationToken cancellationToken)
    {
        var messageId = domainEvent.AggregateEvent.MessageId;
        if (_state.MessageIds.Contains(messageId) && !_state.AcknowledgedMessageIds.Contains(messageId))
        {
            Emit(new DeleteReplyMessageAcknowledgedSagaEvent(messageId, false));
            HandleDeleteCompleted();
        }
        return Task.CompletedTask;
    }

    private void HandleDeleteCompleted()
    {
        if (_state.TotalCount == _state.AcknowledgedMessageIds.Count)
        {
            if (_state.ActuallyDeletedMessageIds.Count > 0)
                Emit(new DeleteReplyMessagesCompletedSagaEvent(_state.ChannelId, _state.Pts, _state.ActuallyDeletedMessageIds.Count, _state.ActuallyDeletedMessageIds, _state.NewTopMessageId));

            CompleteAsync();
        }
    }

    private async Task IncrementPtsAsync(long peerId)
    {
        var pts = await _idGenerator.NextIdAsync(IdType.Pts, peerId);
        Emit(new DeleteReplyMessagePtsIncrementedSagaEvent(peerId, pts));
    }

}

public class DeleteReplyMessageAcknowledgedSagaEvent(int messageId, bool wasDeleted)
    : AggregateEvent<DeleteReplyMessagesSaga, DeleteReplyMessagesSagaId>
{
    public int MessageId { get; } = messageId;
    public bool WasDeleted { get; } = wasDeleted;
}

public class DeleteChannelMessageAcknowledgedSagaEvent(int messageId, bool wasDeleted)
    : AggregateEvent<DeleteChannelMessagesSaga, DeleteChannelMessagesSagaId>
{
    public int MessageId { get; } = messageId;
    public bool WasDeleted { get; } = wasDeleted;
}

public class DeleteReplyMessagesCompletedSagaEvent(long channelId, int pts, int ptsCount,
        List<int> messageIds, int newTopMessageId)
    : AggregateEvent<DeleteReplyMessagesSaga, DeleteReplyMessagesSagaId>
{
    public long ChannelId { get; } = channelId;
    public int Pts { get; } = pts;
    public int PtsCount { get; } = ptsCount;
    public List<int> MessageIds { get; } = messageIds;
    public int NewTopMessageId { get; } = newTopMessageId;
}

public class DeleteReplyMessagePtsIncrementedSagaEvent(long channelId, int pts) : AggregateEvent<DeleteReplyMessagesSaga, DeleteReplyMessagesSagaId>
{
    public long ChannelId { get; } = channelId;
    public int Pts { get; } = pts;
}

public class
    DeleteReplyMessagesSagaStartedSagaEvent(RequestInfo requestInfo, long channelId, List<int> messageIds, int newTopMessageId)
    : AggregateEvent<DeleteReplyMessagesSaga, DeleteReplyMessagesSagaId>
{
    public RequestInfo RequestInfo { get; } = requestInfo;
    public long ChannelId { get; } = channelId;
    public List<int> MessageIds { get; } = messageIds;
    public int NewTopMessageId { get; } = newTopMessageId;
}


[JsonConverter(typeof(SystemTextJsonSingleValueObjectConverter<DeleteChannelMessagesSagaId>))]
public class DeleteChannelMessagesSagaId(string value) : Identity<DeleteChannelMessagesSagaId>(value), ISagaId;

public class
    DeleteChannelMessagesSagaLocator : DefaultSagaLocator<DeleteChannelMessagesSaga, DeleteChannelMessagesSagaId>
{
    protected override DeleteChannelMessagesSagaId CreateSagaId(string requestId)
    {
        return new DeleteChannelMessagesSagaId(requestId);
    }
}

public class DeleteChannelMessagesSagaState : AggregateState<DeleteChannelMessagesSaga, DeleteChannelMessagesSagaId,
    DeleteChannelMessagesSagaState>,
    IApply<DeleteChannelMessagesSagaStartedSagaEvent>,
    IApply<DeleteChannelMessagePtsIncrementedSagaEvent>,
    IApply<DeleteChannelMessageAcknowledgedSagaEvent>,
    IApply<DeleteChannelMessagesAlreadyDeletedSagaEvent>,
    IApply<DeleteChannelMessagesCompletedSagaEvent>,
    IApply<DeleteChannelHistoryCompletedSagaEvent>
{
    public RequestInfo RequestInfo { get; private set; } = null!;
    public long ChannelId { get; private set; }
    public int Pts { get; set; }
    public List<int> MessageIds { get; set; } = new();
    public int TotalCount { get; private set; }
    public int DeletedCount { get; private set; }
    public HashSet<int> AcknowledgedMessageIds { get; } = new();
    public List<int> ActuallyDeletedMessageIds { get; } = new();
    public bool IsDeleteHistory { get; private set; }

    public long? DiscussionGroupChannelId { get; private set; }
    public List<int>? RepliesMessageIds { get; private set; }

    public int NewTopMessageId { get; private set; }
    public int? NewTopMessageIdForDiscussionGroup { get; private set; }

    public IReadOnlyList<MyTelegram.Schema.IMessage>? MessagesToLog { get; private set; }

    public void Apply(DeleteChannelMessagesSagaStartedSagaEvent aggregateEvent)
    {
        RequestInfo = aggregateEvent.RequestInfo;
        ChannelId = aggregateEvent.ChannelId;
        MessageIds = aggregateEvent.MessageIds.Distinct().ToList();
        TotalCount = MessageIds.Count;
        IsDeleteHistory = aggregateEvent.IsDeleteHistory;
        DiscussionGroupChannelId = aggregateEvent.DiscussionGroupChannelId;
        RepliesMessageIds = aggregateEvent.RepliesMessageIds?.ToList() ?? null;
        NewTopMessageId = aggregateEvent.NewTopMessageId;
        NewTopMessageIdForDiscussionGroup = aggregateEvent.NewTopMessageIdForDiscussionGroup;
        MessagesToLog = aggregateEvent.MessagesToLog;
    }

    public void Apply(DeleteChannelMessagePtsIncrementedSagaEvent aggregateEvent)
    {
        DeletedCount++;
        if (Pts < aggregateEvent.Pts)
        {
            Pts = aggregateEvent.Pts;
        }
    }

    public void Apply(DeleteChannelMessageAcknowledgedSagaEvent aggregateEvent)
    {
        if (AcknowledgedMessageIds.Add(aggregateEvent.MessageId) && aggregateEvent.WasDeleted)
            ActuallyDeletedMessageIds.Add(aggregateEvent.MessageId);
    }

    public void Apply(DeleteChannelMessagesAlreadyDeletedSagaEvent aggregateEvent)
    {
    }

    public void Apply(DeleteChannelMessagesCompletedSagaEvent aggregateEvent)
    {
        //throw new NotImplementedException();
    }

    public void Apply(DeleteChannelHistoryCompletedSagaEvent aggregateEvent)
    {
        //throw new NotImplementedException();
    }
}

public class DeleteChannelMessagesSaga : MyInMemoryAggregateSaga<DeleteChannelMessagesSaga, DeleteChannelMessagesSagaId,
    DeleteChannelMessagesSagaLocator>,
    ISagaIsStartedBy<TempAggregate, TempId, DeleteChannelMessagesStartedEvent>,
    ISagaIsStartedBy<TempAggregate, TempId, DeleteParticipantHistoryStartedEvent>,
    ISagaHandles<MessageAggregate, MessageId, ChannelMessageDeletedEvent>,
    ISagaHandles<MessageAggregate, MessageId, ChannelMessageDeletionAcknowledgedEvent>
{
    private readonly IIdGenerator _idGenerator;
    private readonly DeleteChannelMessagesSagaState _state = new();

    public DeleteChannelMessagesSaga(DeleteChannelMessagesSagaId id, IEventStore eventStore, IIdGenerator idGenerator) : base(id, eventStore)
    {
        _idGenerator = idGenerator;
        Register(_state);
    }

    public Task HandleAsync(IDomainEvent<TempAggregate, TempId, DeleteChannelMessagesStartedEvent> domainEvent, ISagaContext sagaContext, CancellationToken cancellationToken)
    {
        Emit(new DeleteChannelMessagesSagaStartedSagaEvent(domainEvent.AggregateEvent.RequestInfo,
            domainEvent.AggregateEvent.ChannelId,
            domainEvent.AggregateEvent.MessageIds,
            domainEvent.AggregateEvent.NewTopMessageId,
            domainEvent.AggregateEvent.NewTopMessageIdForDiscussionGroup,
            false,
            domainEvent.AggregateEvent.DiscussionGroupChannelId,
            domainEvent.AggregateEvent.RepliesMessageIds,
            domainEvent.AggregateEvent.MessagesToLog
            ));
        foreach (var messageId in domainEvent.AggregateEvent.MessageIds)
        {
            var command = new DeleteChannelMessageCommand(MessageId.Create(domainEvent.AggregateEvent.ChannelId, messageId),
                domainEvent.AggregateEvent.RequestInfo,
                domainEvent.AggregateEvent.ExpectedAggregateVersion);
            Publish(command);
        }

        return Task.CompletedTask;
    }

    public async Task HandleAsync(IDomainEvent<MessageAggregate, MessageId, ChannelMessageDeletedEvent> domainEvent, ISagaContext sagaContext, CancellationToken cancellationToken)
    {
        // Native moderation has its own durable completion; never allocate a second pts here.
        if (domainEvent.AggregateEvent.RequestInfo.UserId == MyTelegramConsts.AntiSpamBotUserId)
            return;
        var messageId = domainEvent.AggregateEvent.MessageId;
        if (!_state.MessageIds.Contains(messageId) || _state.AcknowledgedMessageIds.Contains(messageId))
            return;
        await IncrementPtsAsync(domainEvent.AggregateEvent.ChannelId);
        Emit(new DeleteChannelMessageAcknowledgedSagaEvent(messageId, true));
        if (domainEvent.AggregateEvent.IsForwardFromChannelPost &&
            domainEvent.AggregateEvent.PostChannelId.HasValue &&
            domainEvent.AggregateEvent.PostMessageId.HasValue
            )
        {
            await HandleUpdateChannelPostMessageReplyAsync(domainEvent.AggregateEvent.PostChannelId.Value, domainEvent.AggregateEvent.PostMessageId.Value);
        }
        else if (domainEvent.AggregateEvent.ThreadRootMessageId > 0)
        {
            await HandleDecrementThreadReplyCountAsync(domainEvent.AggregateEvent.ChannelId,
                domainEvent.AggregateEvent.ThreadRootMessageId!.Value);
        }

        HandleDeleteCompleted();
    }

    public Task HandleAsync(IDomainEvent<MessageAggregate, MessageId, ChannelMessageDeletionAcknowledgedEvent> domainEvent, ISagaContext sagaContext, CancellationToken cancellationToken)
    {
        var messageId = domainEvent.AggregateEvent.MessageId;
        if (_state.MessageIds.Contains(messageId) && !_state.AcknowledgedMessageIds.Contains(messageId))
        {
            Emit(new DeleteChannelMessageAcknowledgedSagaEvent(messageId, false));
            HandleDeleteCompleted();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// A deleted comment must no longer be counted in its thread root's <c>messageReplies.replies</c>.
    /// </summary>
    private async Task HandleDecrementThreadReplyCountAsync(long channelId, int threadRootMessageId)
    {
        var pts = await _idGenerator.NextIdAsync(IdType.Pts, channelId);
        var command = new DecrementMessageReplyCommand(MessageId.Create(channelId, threadRootMessageId), pts);
        Publish(command);
    }

    private async Task HandleUpdateChannelPostMessageReplyAsync(long postChannelId, int postChannelMessageId)
    {
        var pts = await _idGenerator.NextIdAsync(IdType.Pts, postChannelId);

        var command = new UpdateMessageRelyCommand(MessageId.Create(postChannelId, postChannelMessageId), pts);
        Publish(command);
    }

    private void HandleDeleteCompleted()
    {
        if (_state.TotalCount == _state.AcknowledgedMessageIds.Count)
        {
            if (_state.ActuallyDeletedMessageIds.Count == 0)
            {
                Emit(new DeleteChannelMessagesAlreadyDeletedSagaEvent(_state.RequestInfo, _state.ChannelId, _state.IsDeleteHistory));
            }
            else if (_state.IsDeleteHistory)
            {
                Emit(new DeleteChannelHistoryCompletedSagaEvent(_state.RequestInfo, _state.ChannelId, _state.Pts, _state.ActuallyDeletedMessageIds.Count, _state.ActuallyDeletedMessageIds, _state.NewTopMessageId));
            }
            else
            {
                Emit(new DeleteChannelMessagesCompletedSagaEvent(_state.RequestInfo, _state.ChannelId, _state.Pts, _state.ActuallyDeletedMessageIds.Count, _state.ActuallyDeletedMessageIds, _state.NewTopMessageId));
                if (_state.ActuallyDeletedMessageIds.Count > 0)
                    HandleDeleteReplyMessages();
            }

            CompleteAsync();
        }
    }

    private void HandleDeleteReplyMessages()
    {
        if (_state is { DiscussionGroupChannelId: not null, RepliesMessageIds.Count: > 0 })
        {
            var command = new StartDeleteReplyMessagesCommand(TempId.New,
                _state.RequestInfo with { RequestId = Guid.NewGuid() }, _state.DiscussionGroupChannelId.Value,
                _state.RepliesMessageIds,
                _state.NewTopMessageIdForDiscussionGroup ?? 0
                );
            Publish(command);
        }
    }

    private async Task IncrementPtsAsync(long peerId)
    {
        var pts = await _idGenerator.NextIdAsync(IdType.Pts, peerId);
        Emit(new DeleteChannelMessagePtsIncrementedSagaEvent(peerId, pts));
    }

    public Task HandleAsync(IDomainEvent<TempAggregate, TempId, DeleteParticipantHistoryStartedEvent> domainEvent, ISagaContext sagaContext, CancellationToken cancellationToken)
    {
        Emit(new DeleteChannelMessagesSagaStartedSagaEvent(domainEvent.AggregateEvent.RequestInfo,
            domainEvent.AggregateEvent.ChannelId,
            domainEvent.AggregateEvent.MessageIds,
            domainEvent.AggregateEvent.NewTopMessageId,
            null,
            true, null, null, null));
        foreach (var messageId in domainEvent.AggregateEvent.MessageIds)
        {
            var command = new DeleteChannelMessageCommand(MessageId.Create(domainEvent.AggregateEvent.ChannelId, messageId),
                domainEvent.AggregateEvent.RequestInfo);
            Publish(command);
        }

        return Task.CompletedTask;
    }
}

public class DeleteChannelMessagesAlreadyDeletedSagaEvent(RequestInfo requestInfo, long channelId, bool isDeleteHistory)
    : RequestAggregateEvent2<DeleteChannelMessagesSaga, DeleteChannelMessagesSagaId>(requestInfo)
{
    public long ChannelId { get; } = channelId;
    public bool IsDeleteHistory { get; } = isDeleteHistory;
}

public class DeleteChannelMessagesCompletedSagaEvent(RequestInfo requestInfo, long channelId, int pts, int ptsCount,
        List<int> messageIds, int newTopMessageId)
    : RequestAggregateEvent2<DeleteChannelMessagesSaga, DeleteChannelMessagesSagaId>(requestInfo)
{
    public long ChannelId { get; } = channelId;
    public int Pts { get; } = pts;
    public int PtsCount { get; } = ptsCount;
    public List<int> MessageIds { get; } = messageIds;
    public int NewTopMessageId { get; } = newTopMessageId;
}

public class DeleteChannelHistoryCompletedSagaEvent(RequestInfo requestInfo, long channelId, int pts, int ptsCount,
        List<int> messageIds,int newTopMessageId)
    : RequestAggregateEvent2<DeleteChannelMessagesSaga, DeleteChannelMessagesSagaId>(requestInfo)
{
    public long ChannelId { get; } = channelId;
    public int Pts { get; } = pts;
    public int PtsCount { get; } = ptsCount;
    public List<int> MessageIds { get; } = messageIds;
    public int NewTopMessageId { get; } = newTopMessageId;
}

public class DeleteChannelMessagePtsIncrementedSagaEvent(long channelId, int pts) : AggregateEvent<DeleteChannelMessagesSaga, DeleteChannelMessagesSagaId>
{
    public long ChannelId { get; } = channelId;
    public int Pts { get; } = pts;
}

public class
    DeleteChannelMessagesSagaStartedSagaEvent(RequestInfo requestInfo, long channelId, List<int> messageIds,
        int newTopMessageId,
        int? newTopMessageIdForDiscussionGroup,
        bool isDeleteHistory, long? discussionGroupChannelId, IReadOnlyCollection<int>? repliesMessageIds,
        IReadOnlyList<MyTelegram.Schema.IMessage>? messagesToLog)
    : AggregateEvent<DeleteChannelMessagesSaga, DeleteChannelMessagesSagaId>
{
    public RequestInfo RequestInfo { get; } = requestInfo;
    public long ChannelId { get; } = channelId;
    public List<int> MessageIds { get; } = messageIds;
    public int NewTopMessageId { get; } = newTopMessageId;
    public int? NewTopMessageIdForDiscussionGroup { get; } = newTopMessageIdForDiscussionGroup;
    public bool IsDeleteHistory { get; } = isDeleteHistory;
    public long? DiscussionGroupChannelId { get; } = discussionGroupChannelId;
    public IReadOnlyCollection<int>? RepliesMessageIds { get; } = repliesMessageIds;
    public IReadOnlyList<MyTelegram.Schema.IMessage>? MessagesToLog { get; } = messagesToLog;
}
