namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get your own polls that have received votes you haven't read yet. Only meaningful for
/// non-anonymous (<c>public_voters</c>) polls, since anonymous votes are never attributed.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getUnreadPollVotes"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetUnreadPollVotesHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IMessageConverterService messageConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetUnreadPollVotes, MyTelegram.Schema.Messages.IMessages>
{
    protected override async Task<MyTelegram.Schema.Messages.IMessages> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetUnreadPollVotes obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;

        var readState = await queryProcessor.ProcessAsync(
            new GetUserConfigByKeyQuery(input.UserId, PollVoteReadState.GetKey(peer, obj.TopMsgId)));
        var readDate = PollVoteReadState.ParseReadDate(readState?.Value);

        var limit = obj.Limit > 0 && obj.Limit <= 100 ? obj.Limit : 20;

        // Two steps instead of a join: first this peer's own poll messages, then a single
        // batched vote lookup over their poll ids.
        var pollMessages = await queryProcessor.ProcessAsync(
            new GetPollMessagesQuery(
                ownerPeerId,
                input.UserId,
                obj.OffsetId,
                obj.AddOffset,
                limit,
                obj.MaxId,
                obj.MinId,
                obj.TopMsgId));

        if (pollMessages.Count == 0)
        {
            return EmptyMessages();
        }

        var pollIds = pollMessages
            .Where(p => p.PollId.HasValue)
            .Select(p => p.PollId!.Value)
            .Distinct()
            .ToList();

        if (pollIds.Count == 0)
        {
            return EmptyMessages();
        }

        var polls = await queryProcessor.ProcessAsync(new GetPollsQuery(pollIds));

        // Anonymous polls never surface unread votes: there is nobody to attribute them to.
        var publicPollIds = polls
            .Where(p => p.PublicVoters)
            .Select(p => p.PollId)
            .ToHashSet();
        if (publicPollIds.Count == 0)
        {
            return EmptyMessages();
        }

        var recentVotes = await queryProcessor.ProcessAsync(
            new GetPollVotesByPollIdsQuery([.. publicPollIds], readDate));

        // A vote by the poll's own author isn't news to them.
        var pollIdsWithUnreadVotes = recentVotes
            .Where(p => p.VoterPeerId != input.UserId)
            .Select(p => p.PollId)
            .ToHashSet();
        if (pollIdsWithUnreadVotes.Count == 0)
        {
            return EmptyMessages();
        }

        var unreadMessages = pollMessages
            .Where(p => p.PollId.HasValue && pollIdsWithUnreadVotes.Contains(p.PollId.Value))
            .ToList();

        var chosenOptions = await queryProcessor.ProcessAsync(
            new GetChosenVoteAnswersQuery([.. pollIdsWithUnreadVotes], input.UserId));

        var messages = messageConverterService.ToMessageList(
            input.UserId,
            unreadMessages,
            polls.Where(p => pollIdsWithUnreadVotes.Contains(p.PollId)).ToList(),
            chosenOptions,
            null,
            input.Layer);

        return new TMessages
        {
            Messages = new TVector<IMessage>(messages),
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Topics = new TVector<IForumTopic>()
        };
    }

    private static TMessages EmptyMessages()
    {
        return new TMessages
        {
            Messages = new TVector<IMessage>(),
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Topics = new TVector<IForumTopic>()
        };
    }
}
