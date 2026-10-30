using global::EventFlow.Aggregates;
using global::EventFlow.ReadStores;
using Moq;
using MyTelegram.Domain.Aggregates.Messaging;
using MyTelegram.ReadModel.Impl;

namespace MyTelegram.Messenger.Tests.AntiSpam;

public sealed class AntiSpamReplyProjectionTests
{
    [Fact]
    public async Task Refresh_restores_missing_reply_and_does_not_alias_event_payload()
    {
        var model = new MessageReadModel();
        var reply = new MessageReply(100, 3, 42, 90, [new Peer(PeerType.User, 200)]);
        var payload = new AntiSpamReplyDeletionAppliedEvent("job", 3, 42, reply: reply);
        var domainEvent = new Mock<IDomainEvent<MessageAggregate, MessageId, AntiSpamReplyDeletionAppliedEvent>>();
        domainEvent.SetupGet(x => x.AggregateIdentity).Returns(MessageId.Create(100, 1));
        domainEvent.SetupGet(x => x.AggregateSequenceNumber).Returns(10);
        domainEvent.SetupGet(x => x.AggregateEvent).Returns(payload);
        await model.ApplyAsync(Mock.Of<IReadModelContext>(), domainEvent.Object, default);
        Assert.NotNull(model.Reply);
        Assert.Equal(3, model.Reply.Replies);
        Assert.Equal(90, model.Reply.MaxId);
        Assert.Equal(100, model.Reply.ChannelId);
        Assert.NotSame(reply, model.Reply);
        Assert.NotSame(reply.RecentRepliers, model.Reply.RecentRepliers);
        model.Reply.RecentRepliers!.Clear();
        model.Reply.Replies = 99;
        Assert.Single(reply.RecentRepliers!);
        Assert.Equal(3, reply.Replies);
    }
}
