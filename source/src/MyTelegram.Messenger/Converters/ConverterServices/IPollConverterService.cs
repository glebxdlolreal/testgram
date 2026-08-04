namespace MyTelegram.Messenger.Converters.ConverterServices;

public interface IPollConverterService
{
    IPoll ToPoll(IPollReadModel pollReadModel, int layer = 0, long selfUserId = 0);

    IPollResults ToPollResults(IPollReadModel pollReadModel,
        IList<string> chosenOptions,
        int layer = 0,
        IReadOnlyCollection<long>? recentVoterPeerIds = null,
        long selfUserId = 0);

    /// <summary>
    /// Wraps the current poll results in an <c>updateMessagePoll</c>.
    /// </summary>
    /// <param name="min">
    /// True for broadcasts to other members, where per-user state (the chosen flag) is
    /// meaningless. Must be false when answering the requesting user, otherwise clients
    /// discard the chosen flags.
    /// </param>
    /// <param name="includePoll">
    /// Include the poll object itself, needed when a field on the poll changed (closed,
    /// answers) rather than just the tallies.
    /// </param>
    IUpdates ToPollUpdates(IPollReadModel pollReadModel,
        IList<string> chosenOptions,
        int layer = 0,
        bool min = true,
        Peer? peer = null,
        int? msgId = null,
        IReadOnlyCollection<long>? recentVoterPeerIds = null,
        long selfUserId = 0,
        bool includePoll = false);
}
