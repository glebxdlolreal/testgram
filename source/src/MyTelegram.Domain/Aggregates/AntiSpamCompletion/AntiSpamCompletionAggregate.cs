namespace MyTelegram.Domain.Aggregates.AntiSpamCompletion;

[JsonConverter(typeof(SystemTextJsonSingleValueObjectConverter<AntiSpamCompletionId>))]
public sealed class AntiSpamCompletionId : global::EventFlow.ValueObjects.SingleValueObject<string>, IIdentity
{
    public AntiSpamCompletionId(string value) : base(value)
    {
        var parts = value?.Split('-');
        if (parts is not { Length: 4 } || parts[0] != "antispamcompletion" ||
            !long.TryParse(parts[1], out var channel) || channel < MyTelegramConsts.ChannelInitId ||
            !int.TryParse(parts[2], out var message) || message <= 0 ||
            !Guid.TryParseExact(parts[3], "N", out var request) ||
            value != $"antispamcompletion-{channel}-{message}-{request:N}")
            throw new ArgumentException("Invalid channel/message completion operation identity", nameof(value));
    }
}

/// <summary>Immutable, event-backed deletion update. Transport may repeat; pts never changes.</summary>
[EnableAutoGeneration]
public sealed class AntiSpamCompletionAggregate : AggregateRoot<AntiSpamCompletionAggregate, AntiSpamCompletionId>
{
    private readonly AntiSpamCompletionState _state = new();
    public AntiSpamCompletionAggregate(AntiSpamCompletionId id) : base(id) => Register(_state);
    public AntiSpamCompletionCreatedEvent? Completion => _state.Completion;

    /// <summary>Retries use a fresh live-history candidate, never the immutable creation candidate.</summary>
    public void RepairAntiSpamCompletionTop(int expectedTopMessageId, int newTopMessageId)
    {
        var completion = _state.Completion ?? throw new InvalidOperationException("Completion has not been created");
        if (expectedTopMessageId < 0 || newTopMessageId < 0 || newTopMessageId == completion.MessageId)
            throw new InvalidOperationException("Invalid anti-spam top repair");
        string operationId = completion.OperationId;
        long channelId = completion.ChannelId;
        Emit(new AntiSpamCompletionTopRepairedEvent(operationId, channelId, expectedTopMessageId, newTopMessageId));
    }

    public void PublishAntiSpamCompletion()
    {
        var e = _state.Completion ?? throw new InvalidOperationException("Completion has not been created");
        string operationId = e.OperationId;
        long channelId = e.ChannelId;
        int messageId = e.MessageId;
        int pts = e.Pts;
        long globalSeqNo = e.GlobalSeqNo;
        int date = e.Date;
        int newTopMessageId = e.NewTopMessageId;
        Emit(new AntiSpamCompletionPublishedEvent(operationId, channelId, messageId, pts, globalSeqNo, date, newTopMessageId));
    }

    public void CreateAntiSpamCompletion(string operationId, long channelId, int messageId, int pts, long globalSeqNo, int date, int newTopMessageId = 0)
    {
        if (_state.Completion is { } existing)
        {
            if (existing.OperationId != operationId || existing.ChannelId != channelId || existing.MessageId != messageId ||
                existing.Pts != pts || existing.GlobalSeqNo != globalSeqNo || existing.Date != date || existing.NewTopMessageId != newTopMessageId)
                throw new InvalidOperationException("Anti-spam completion identity cannot be changed");
            // Re-emitting the immutable value repairs a lost cross-process projection delivery.
            Emit(new AntiSpamCompletionCreatedEvent(operationId, channelId, messageId, pts, globalSeqNo, date, newTopMessageId));
            return;
        }
        var identityParts = Id.Value.Split('-');
        if (operationId != Id.Value || identityParts[1] != channelId.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            identityParts[2] != messageId.ToString(System.Globalization.CultureInfo.InvariantCulture) || pts <= 0 || globalSeqNo <= 0)
            throw new InvalidOperationException("Invalid anti-spam completion identity");
        Emit(new AntiSpamCompletionCreatedEvent(operationId, channelId, messageId, pts, globalSeqNo, date, newTopMessageId));
    }
}

public sealed class AntiSpamCompletionState : AggregateState<AntiSpamCompletionAggregate, AntiSpamCompletionId, AntiSpamCompletionState>,
    IApply<AntiSpamCompletionCreatedEvent>, IApply<AntiSpamCompletionPublishedEvent>, IApply<AntiSpamCompletionTopRepairedEvent>
{
    public AntiSpamCompletionCreatedEvent? Completion { get; private set; }
    public void Apply(AntiSpamCompletionTopRepairedEvent aggregateEvent) { }
    public void Apply(AntiSpamCompletionPublishedEvent aggregateEvent) { }
    public void Apply(AntiSpamCompletionCreatedEvent aggregateEvent) => Completion = aggregateEvent;
}

