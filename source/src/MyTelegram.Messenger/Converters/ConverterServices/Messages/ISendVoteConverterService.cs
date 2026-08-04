namespace MyTelegram.Messenger.Converters.ConverterServices.Messages;

public interface ISendVoteConverterService
{
    IUpdates ToSelfUpdates(IPollReadModel pollReadModel,
        List<string> chosenOptions,
        int layer,
        long selfUserId = 0,
        Peer? peer = null,
        int? msgId = null,
        IReadOnlyCollection<long>? recentVoterPeerIds = null);

    IUpdates ToUpdates(IPollReadModel pollReadModel,
        List<string> chosenOptions,
        Peer? peer = null,
        int? msgId = null,
        IReadOnlyCollection<long>? recentVoterPeerIds = null);

    /// <summary>
    /// Builds an <c>updateMessagePollVote</c> announcing a single peer's vote. Only sent for
    /// non-anonymous (<c>public_voters</c>) polls.
    /// </summary>
    IUpdates ToPollVoteUpdates(IPollReadModel pollReadModel,
        Peer voterPeer,
        IReadOnlyCollection<string> options,
        int qts);
}
