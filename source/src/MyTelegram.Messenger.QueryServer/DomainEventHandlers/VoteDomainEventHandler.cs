using MyTelegram.Messenger.Services.Caching;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

public class VoteDomainEventHandler(
    IObjectMessageSender objectMessageSender,
    ICommandBus commandBus,
    IIdGenerator idGenerator,
    IAckCacheService ackCacheService,
    IQueryProcessor queryProcessor,
    ISendVoteConverterService sendVoteConverterService,
    IPtsHelper ptsHelper
    )
    :
        DomainEventHandlerBase(objectMessageSender, commandBus, idGenerator, ackCacheService),
        ISubscribeSynchronousTo<VoteSaga, VoteSagaId, VoteSagaCompletedSagaEvent>
{
    public async Task HandleAsync(IDomainEvent<VoteSaga, VoteSagaId, VoteSagaCompletedSagaEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var pollReadModel = await queryProcessor
            .ProcessAsync(new GetPollQuery(domainEvent.AggregateEvent.PollId), cancellationToken);
        if (pollReadModel == null)
        {
            return;
        }

        var toPeer = domainEvent.AggregateEvent.ToPeer;
        var chosenOptions = domainEvent.AggregateEvent.ChosenOptions.ToList();

        // Locating the message lets the update carry peer/msg_id instead of poll_id alone.
        var msgId = await queryProcessor.ProcessAsync(
            new GetMessageIdByPollIdQuery(toPeer.PeerId, pollReadModel.PollId), cancellationToken);

        var recentVoterPeerIds = await GetRecentVoterPeerIdsAsync(pollReadModel, cancellationToken);

        var selfUpdates = sendVoteConverterService.ToSelfUpdates(pollReadModel,
            chosenOptions,
            domainEvent.AggregateEvent.RequestInfo.Layer,
            domainEvent.AggregateEvent.RequestInfo.UserId,
            toPeer,
            msgId,
            recentVoterPeerIds);
        await SendRpcMessageToClientAsync(domainEvent.AggregateEvent.RequestInfo, selfUpdates);

        await PushMessageToPeerAsync(new Peer(PeerType.User, domainEvent.AggregateEvent.RequestInfo.UserId),
            selfUpdates,
            domainEvent.AggregateEvent.RequestInfo.AuthKeyId);

        var updatesForMember = sendVoteConverterService.ToUpdates(pollReadModel, [], toPeer, msgId,
            recentVoterPeerIds);
        await PushMessageToPeerAsync(toPeer,
            updatesForMember,
            excludeAuthKeyId: domainEvent.AggregateEvent.RequestInfo.AuthKeyId);

        await SendPollVoteUpdateAsync(pollReadModel, domainEvent.AggregateEvent, cancellationToken);

        await BumpUnreadPollVotesAsync(pollReadModel, domainEvent.AggregateEvent, toPeer, msgId);
    }

    /// <summary>
    /// Raises the poll author's <c>unread_poll_votes_count</c> for this dialog. Anonymous polls
    /// are skipped: their votes are never shown individually, so there is nothing to read.
    /// </summary>
    private async Task BumpUnreadPollVotesAsync(
        IPollReadModel pollReadModel,
        VoteSagaCompletedSagaEvent aggregateEvent,
        Peer toPeer,
        int? msgId)
    {
        if (!pollReadModel.PublicVoters || pollReadModel.CreatorUserId == null || msgId == null)
        {
            return;
        }

        // A retraction removes a vote, and the author's own vote isn't news to them.
        if (aggregateEvent.ChosenOptions.Count == 0
            || pollReadModel.CreatorUserId == aggregateEvent.VoterPeerId)
        {
            return;
        }

        await commandBus.PublishAsync(new CreatePollVoteCommand(
            DialogId.Create(pollReadModel.CreatorUserId.Value, toPeer),
            msgId.Value));
    }

    /// <summary>
    /// Announces an individual vote via <c>updateMessagePollVote</c>. Only non-anonymous polls
    /// qualify — for an anonymous poll this update would expose who voted for what.
    /// </summary>
    private async Task SendPollVoteUpdateAsync(
        IPollReadModel pollReadModel,
        VoteSagaCompletedSagaEvent aggregateEvent,
        CancellationToken cancellationToken)
    {
        if (!pollReadModel.PublicVoters || pollReadModel.CreatorUserId == null)
        {
            return;
        }

        // A retraction carries no options; there is nothing to announce.
        if (aggregateEvent.ChosenOptions.Count == 0)
        {
            return;
        }

        var creatorUserId = pollReadModel.CreatorUserId.Value;

        // The voter's own client already got the full results above.
        if (creatorUserId == aggregateEvent.VoterPeerId)
        {
            return;
        }

        // qts sequenced so the recipient can recover missed votes via updates.getDifference.
        var currentQts = (await ptsHelper.GetPtsForUserAsync(creatorUserId)).Qts;
        var qts = await ptsHelper.IncrementQtsAsync(creatorUserId, currentQts);

        var voteUpdates = sendVoteConverterService.ToPollVoteUpdates(
            pollReadModel,
            new Peer(PeerType.User, aggregateEvent.VoterPeerId),
            aggregateEvent.ChosenOptions,
            qts);

        await objectMessageSender.PushMessageToPeerAsync(
            new Peer(PeerType.User, creatorUserId), voteUpdates, qts: qts);
    }

    private async Task<IReadOnlyCollection<long>?> GetRecentVoterPeerIdsAsync(
        IPollReadModel pollReadModel,
        CancellationToken cancellationToken)
    {
        if (!pollReadModel.PublicVoters)
        {
            return null;
        }

        var recentVoters = await queryProcessor.ProcessAsync(
            new GetRecentPollVotersQuery(pollReadModel.PollId, MyTelegramConsts.MaxPollRecentVoters),
            cancellationToken);

        return recentVoters.Select(p => p.VoterPeerId).Distinct().ToList();
    }
}
