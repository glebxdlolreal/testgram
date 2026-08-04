using System.Text;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get poll results for non-anonymous polls
/// Possible errors
/// Code Type Description
/// 403 BROADCAST_FORBIDDEN Channel poll voters and reactions cannot be fetched to prevent deanonymization.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 403 POLL_VOTE_REQUIRED Cast a vote in the poll before calling this method.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getPollVotes"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetPollVotesHandler(IQueryProcessor queryProcessor, IChatConverterService chatConverterService, IUserConverterService userConverterService, IChannelAppService channelAppService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetPollVotes, MyTelegram.Schema.Messages.IVotesList>
{
    protected override async Task<MyTelegram.Schema.Messages.IVotesList> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetPollVotes obj)
    {
        var peer = obj.Peer.ToPeer();
        var ownerPeerId = peer.PeerId;
        if (peer.PeerType == PeerType.Channel)
        {
            var channelReadModel = await channelAppService.GetAsync(peer.PeerId);
            if (channelReadModel.Broadcast)
            {
                RpcErrors.RpcErrors403.BroadcastForbidden.ThrowRpcError();
            }
        }

        var messageReadModel = await queryProcessor.ProcessAsync(new GetMessageByPeerIdAndMessageIdQuery(ownerPeerId, obj.Id));
        if (messageReadModel == null || messageReadModel.PollId == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var pollId = messageReadModel!.PollId!.Value;
        var pollReadModel = await queryProcessor.ProcessAsync(new GetPollQuery(pollId));
        if (pollReadModel == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        // You have to participate before you get to see who voted for what. The poll's own
        // creator is exempt, since they need the breakdown regardless.
        if (pollReadModel!.CreatorUserId != input.UserId)
        {
            var ownVotes = await queryProcessor.ProcessAsync(new GetPollAnswerVotersQuery(pollId, input.UserId));
            if (ownVotes.Count == 0)
            {
                RpcErrors.RpcErrors403.PollVoteRequired.ThrowRpcError();
            }
        }

        var limit = obj.Limit;
        if (limit <= 0 || limit > 500)
        {
            limit = 100;
        }

        int.TryParse(obj.Offset, out var offset);
        var pollVoterReadModels = await queryProcessor.ProcessAsync(new GetPollVotesQuery(pollId, obj.Option, offset, limit));

        // Offsets count vote documents, not voters: with multiple_choice one voter can own
        // several, so paging by voter would need a full scan to stay stable.
        string? nextOffset = null;
        if (pollVoterReadModels.Count == limit)
        {
            nextOffset = (offset + pollVoterReadModels.Count).ToString();
        }

        var totalCount = await queryProcessor.ProcessAsync(new GetPollVotesCountQuery(pollId, obj.Option));

        var result = new TVotesList
        {
            // Total across the whole poll, not just this page — clients render it as
            // "N votes" next to the option.
            Count = (int)totalCount,
            NextOffset = nextOffset,
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Votes = []
        };

        // One entry per voter: several options collapse into messagePeerVoteMultiple.
        foreach (var group in pollVoterReadModels.GroupBy(p => p.VoterPeerId))
        {
            var votes = group.ToList();
            var date = votes.Max(p => p.Date);
            if (date == 0)
            {
                date = DateTime.UtcNow.ToTimestamp();
            }

            var voterPeer = new TPeerUser { UserId = group.Key };

            result.Votes.Add(votes.Count == 1
                ? new TMessagePeerVote
                {
                    Date = date,
                    Option = votes[0].Option,
                    Peer = voterPeer
                }
                : new TMessagePeerVoteMultiple
                {
                    Date = date,
                    Options = new TVector<ReadOnlyMemory<byte>>(
                        votes.Select(p => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(p.Option))),
                    Peer = voterPeer
                });
        }

        if (peer.PeerType == PeerType.Channel)
        {
            var channel = await chatConverterService.GetChannelAsync(input, peer.PeerId, true, null, layer: input.Layer);
            result.Chats.Add(channel);
        }

        var userIds = pollVoterReadModels.Select(p => p.VoterPeerId).Distinct().ToList();
        var users = await userConverterService.GetUserListAsync(input, userIds, false, false, input.Layer);
        result.Users.AddRange(users);
        return result;
    }
}
