using System.Text;

namespace MyTelegram.Messenger.Converters.ConverterServices.Messages;

internal sealed class SendVoteConverterService(IPollConverterService pollConverterService) : ISendVoteConverterService, ITransientDependency
{
    public IUpdates ToSelfUpdates(IPollReadModel pollReadModel,
        List<string> chosenOptions,
        int layer,
        long selfUserId = 0,
        Peer? peer = null,
        int? msgId = null,
        IReadOnlyCollection<long>? recentVoterPeerIds = null)
    {
        var poll = pollConverterService.ToPoll(pollReadModel, layer, selfUserId);
        var pollResults = pollConverterService.ToPollResults(pollReadModel, chosenOptions, layer, recentVoterPeerIds,
            selfUserId);

        var updateMessagePoll = new TUpdateMessagePoll
        {
            Poll = poll,
            PollId = pollReadModel.PollId,
            Results = pollResults
        };

        if (peer != null && msgId != null)
        {
            updateMessagePoll.Peer = peer.ToPeer();
            updateMessagePoll.MsgId = msgId;
        }

        return new TUpdates
        {
            Updates = [updateMessagePoll],
            Chats = [],
            Users = [],
            Date = DateTime.UtcNow.ToTimestamp()
        };
    }

    public IUpdates ToUpdates(IPollReadModel pollReadModel,
        List<string> chosenOptions,
        Peer? peer = null,
        int? msgId = null,
        IReadOnlyCollection<long>? recentVoterPeerIds = null)
    {
        var pollResults = pollConverterService.ToPollResults(pollReadModel, chosenOptions,
            recentVoterPeerIds: recentVoterPeerIds);

        // min: this copy goes to everyone else, so it carries no per-user chosen state.
        pollResults.Min = true;

        var updateMessagePoll = new TUpdateMessagePoll
        {
            PollId = pollReadModel.PollId,
            Results = pollResults
        };

        if (peer != null && msgId != null)
        {
            updateMessagePoll.Peer = peer.ToPeer();
            updateMessagePoll.MsgId = msgId;
        }

        return new TUpdates
        {
            Updates = [updateMessagePoll],
            Chats = [],
            Users = [],
            Date = DateTime.UtcNow.ToTimestamp()
        };
    }

    public IUpdates ToPollVoteUpdates(IPollReadModel pollReadModel,
        Peer voterPeer,
        IReadOnlyCollection<string> options,
        int qts)
    {
        // positions mirrors options: for each cast option, its index among the poll's
        // answers, so clients can locate the answer without re-matching the raw bytes.
        var answerOptions = pollReadModel.Answers.Select(p => p.Option).ToList();

        var update = new TUpdateMessagePollVote
        {
            PollId = pollReadModel.PollId,
            Peer = voterPeer.ToPeer()!,
            Options = new TVector<ReadOnlyMemory<byte>>(options.Select(p => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(p))),
            Positions = new TVector<int>(options.Select(p => answerOptions.IndexOf(p)).Where(p => p >= 0)),
            Qts = qts
        };

        return new TUpdates
        {
            Updates = [update],
            Chats = [],
            Users = [],
            Date = DateTime.UtcNow.ToTimestamp()
        };
    }
}
