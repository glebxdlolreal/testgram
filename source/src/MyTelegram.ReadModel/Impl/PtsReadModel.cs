using MyTelegram.Domain.Aggregates.AntiSpamCompletion;

namespace MyTelegram.ReadModel.Impl;

public class PtsReadModel : IPtsReadModel,
    IAmReadModelFor<AntiSpamCompletionAggregate, AntiSpamCompletionId, AntiSpamCompletionPublishedEvent>,
    IAmReadModelFor<PtsAggregate, PtsId, PtsUpdatedEvent>,
    IAmReadModelFor<PtsAggregate, PtsId, QtsUpdatedEvent>,
    IAmReadModelFor<PtsAggregate, PtsId, PtsGlobalSeqNoUpdatedEvent>
{
    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<AntiSpamCompletionAggregate, AntiSpamCompletionId, AntiSpamCompletionPublishedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var e = domainEvent.AggregateEvent;
        Id = PtsId.Create(e.ChannelId).Value;
        PeerId = e.ChannelId;
        if (e.Pts > Pts)
        {
            Pts = e.Pts;
            Date = e.Date;
            Seq++;
        }
        GlobalSeqNo = Math.Max(GlobalSeqNo, e.GlobalSeqNo);
        return Task.CompletedTask;
    }

    public virtual int Date { get; private set; }
    public virtual long GlobalSeqNo { get; private set; }
    public virtual string Id { get; private set; } = null!;
    public virtual long PeerId { get; private set; }
    public virtual int Pts { get; private set; }
    public virtual int Qts { get; private set; }

    /// <summary>
    /// Counts the update batches applied to this box. Derived from the event stream rather than
    /// carried on the events themselves, so it stays deterministic across a read-model replay.
    /// </summary>
    public virtual int Seq { get; private set; }
    public virtual int UnreadCount { get; private set; }
    public virtual long? Version { get; set; }

    public virtual int MaxMessageId { get; set; }

    public Task ApplyAsync(IReadModelContext context,
        IDomainEvent<PtsAggregate, PtsId, PtsGlobalSeqNoUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        Id = PtsId.Create(domainEvent.AggregateEvent.PeerId).Value;
        GlobalSeqNo = domainEvent.AggregateEvent.GlobalSeqNo;

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<PtsAggregate, PtsId, PtsUpdatedEvent> domainEvent, CancellationToken cancellationToken)
    {
        if (Pts >= domainEvent.AggregateEvent.NewPts)
        {
            return Task.CompletedTask;
        }

        Id = PtsId.Create(domainEvent.AggregateEvent.PeerId).Value;
        PeerId = domainEvent.AggregateEvent.PeerId;
        Pts = domainEvent.AggregateEvent.NewPts;
        Date = domainEvent.AggregateEvent.Date;
        Seq++;

        UnreadCount += domainEvent.AggregateEvent.ChangedUnreadCount;
        if (domainEvent.AggregateEvent.MessageId.HasValue && domainEvent.AggregateEvent.MessageId > MaxMessageId)
        {
            MaxMessageId = domainEvent.AggregateEvent.MessageId.Value;
        }

        return Task.CompletedTask;
    }

    public Task ApplyAsync(IReadModelContext context, IDomainEvent<PtsAggregate, PtsId, QtsUpdatedEvent> domainEvent, CancellationToken cancellationToken)
    {
        if (Qts >= domainEvent.AggregateEvent.NewQts)
        {
            return Task.CompletedTask;
        }

        Id = PtsId.Create(domainEvent.AggregateEvent.PeerId).Value;
        PeerId = domainEvent.AggregateEvent.PeerId;
        Qts = domainEvent.AggregateEvent.NewQts;
        Date = domainEvent.AggregateEvent.Date;
        Seq++;


        return Task.CompletedTask;
    }
}