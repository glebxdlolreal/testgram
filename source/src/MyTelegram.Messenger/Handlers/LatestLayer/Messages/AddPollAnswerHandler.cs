using MyTelegram.Domain.Aggregates.Poll;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Append a new answer to an <a href="https://corefork.telegram.org/api/poll">open poll »</a>,
/// i.e. one created with <c>open_answers</c>, which lets members contribute their own options.
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 MESSAGE_POLL_CLOSED Poll closed.
/// 400 OPTIONS_TOO_MUCH Too many options provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 POLL_ANSWER_INVALID The specified poll answer is invalid.
/// 400 POLL_OPTION_DUPLICATE Duplicate poll options provided.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.addPollAnswer"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class AddPollAnswerHandler(
    IQueryProcessor queryProcessor,
    ICommandBus commandBus,
    IPeerHelper peerHelper,
    IMessageAppService messageAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestAddPollAnswer, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestAddPollAnswer obj)
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

        var text = obj.Answer.Text;
        if (text == null || string.IsNullOrWhiteSpace(text.Text))
        {
            RpcErrors.RpcErrors400.PollAnswerInvalid.ThrowRpcError();
        }

        // Option ids are server-assigned and never reused, so a removed answer's id can't
        // collide with a later one. Existing ids are numeric strings from creation time.
        var nextOptionId = pollReadModel!.Answers
            .Select(p => int.TryParse(p.Option, out var value) ? value : -1)
            .DefaultIfEmpty(-1)
            .Max() + 1;

        var answer = new PollAnswer(
            text!.Text,
            nextOptionId.ToString(),
            text.Entities.ToBytes(),
            input.UserId,
            CurrentDate);

        // The aggregate enforces open_answers, the closed state, the option cap and duplicates.
        await commandBus.PublishAsync(new AddAnswerCommand(
            PollId.Create(pollReadModel.PollId),
            input.ToRequestInfo(),
            input.UserId,
            answer,
            CurrentDate));

        // Service message announcing the contribution, mirroring how todo list appends work.
        var action = new TMessageActionPollAppendAnswer
        {
            Answer = new TPollAnswer
            {
                Option = answer.Option,
                Text = new TTextWithEntities
                {
                    Text = answer.Text,
                    Entities = text.Entities ?? []
                },
                AddedBy = new TPeerUser { UserId = input.UserId },
                Date = CurrentDate
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

        // The refreshed poll reaches clients via updateMessagePoll pushed from the
        // PollAnswerAdded domain event handler.
        return null!;
    }
}
