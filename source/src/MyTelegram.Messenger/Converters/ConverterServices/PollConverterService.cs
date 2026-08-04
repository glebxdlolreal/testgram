namespace MyTelegram.Messenger.Converters.ConverterServices;

public class PollConverterService(
    ILayeredService<IPollConverter> pollLayeredService,
    ILayeredService<IPollResultsConverter> pollResultsLayeredService) : IPollConverterService, ITransientDependency
{
    public IPoll ToPoll(IPollReadModel pollReadModel, int layer = 0, long selfUserId = 0)
    {
        return pollLayeredService.GetConverter(layer).ToPoll(pollReadModel, selfUserId);
    }

    public IPollResults ToPollResults(IPollReadModel pollReadModel,
        IList<string> chosenOptions,
        int layer = 0,
        IReadOnlyCollection<long>? recentVoterPeerIds = null,
        long selfUserId = 0)
    {
        return pollResultsLayeredService.GetConverter(layer)
            .ToPollResults(pollReadModel, chosenOptions, recentVoterPeerIds, selfUserId);
    }

    public IUpdates ToPollUpdates(IPollReadModel pollReadModel,
        IList<string> chosenOptions,
        int layer = 0,
        bool min = true,
        Peer? peer = null,
        int? msgId = null,
        IReadOnlyCollection<long>? recentVoterPeerIds = null,
        long selfUserId = 0,
        bool includePoll = false)
    {
        var pollResults = ToPollResults(pollReadModel, chosenOptions, layer, recentVoterPeerIds, selfUserId);
        pollResults.Min = min;

        var updateMessagePoll = new TUpdateMessagePoll
        {
            PollId = pollReadModel.PollId,
            Results = pollResults
        };

        // The poll object itself is only needed when its own fields changed (closed,
        // answers); for a plain tally refresh the results alone are enough.
        if (includePoll)
        {
            updateMessagePoll.Poll = ToPoll(pollReadModel, layer, selfUserId);
        }

        // peer and msg_id share a flag bit, so both are set or neither is. Clients can
        // match on poll_id alone, but the pair lets them locate the message directly.
        if (peer != null && msgId != null)
        {
            updateMessagePoll.Peer = peer.ToPeer();
            updateMessagePoll.MsgId = msgId;
        }

        return new TUpdateShort
        {
            Date = DateTime.UtcNow.ToTimestamp(),
            Update = updateMessagePoll
        };
    }
}
