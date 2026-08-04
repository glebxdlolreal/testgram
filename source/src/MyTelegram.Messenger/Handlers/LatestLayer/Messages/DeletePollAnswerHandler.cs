using System.Text;
using MyTelegram.Domain.Aggregates.Poll;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Remove an answer from an <a href="https://corefork.telegram.org/api/poll">open poll »</a>.
/// Only the member who contributed the answer, the poll's creator, or a channel admin with
/// edit rights may remove it.
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 MESSAGE_POLL_CLOSED Poll closed.
/// 400 OPTION_INVALID Invalid option selected.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 POLL_ANSWER_INVALID The specified poll answer is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.deletePollAnswer"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DeletePollAnswerHandler(
    IQueryProcessor queryProcessor,
    ICommandBus commandBus,
    IPeerHelper peerHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IMessageAppService messageAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestDeletePollAnswer, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestDeletePollAnswer obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        var pollId = await queryProcessor.ProcessAsync(new GetPollIdByMessageIdQuery(peer.PeerId, obj.MsgId));
        if (pollId == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var pollReadModel = await queryProcessor.ProcessAsync(new GetPollQuery(pollId!.Value));
        if (pollReadModel == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var option = Encoding.UTF8.GetString(obj.Option.Span);
        var answer = pollReadModel!.Answers.FirstOrDefault(p => p.Option == option);
        if (answer == null)
        {
            RpcErrors.RpcErrors400.OptionInvalid.ThrowRpcError();
        }

        // A channel admin who can edit messages may remove anyone's answer. For everyone
        // else the aggregate decides (contributor or poll creator only).
        var requestedByPeerId = input.UserId;
        if (peer.PeerType == PeerType.Channel
            && answer!.AddedByPeerId != input.UserId
            && pollReadModel.CreatorUserId != input.UserId)
        {
            await channelAdminRightsChecker.CheckAdminRightAsync(peer.PeerId, input.UserId, p => p.EditMessages);

            // Authorized as an admin: present the request as the poll creator so the
            // aggregate's contributor check passes.
            requestedByPeerId = pollReadModel.CreatorUserId ?? input.UserId;
        }

        await commandBus.PublishAsync(new DeleteAnswerCommand(
            PollId.Create(pollReadModel.PollId),
            input.ToRequestInfo(),
            requestedByPeerId,
            option));

        var action = new TMessageActionPollDeleteAnswer
        {
            Answer = new TPollAnswer
            {
                Option = answer!.Option,
                Text = new TTextWithEntities
                {
                    Text = answer.Text,
                    Entities = answer.Entities.ToTObject<TVector<IMessageEntity>>() ?? []
                },
                AddedBy = answer.AddedByPeerId == null
                    ? null
                    : new TPeerUser { UserId = answer.AddedByPeerId.Value },
                Date = answer.AddedByPeerId == null ? null : answer.Date ?? 0
            }
        };

        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            peer,
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action
        );
        await messageAppService.SendMessageAsync([sendInput]);

        return null!;
    }
}
