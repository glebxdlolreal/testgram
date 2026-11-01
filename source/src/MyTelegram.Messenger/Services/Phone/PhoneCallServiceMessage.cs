using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Phone;

/// <summary>
/// The one <c>messageActionPhoneCall</c> a 1:1 call leaves in the chat history.
/// </summary>
/// <remarks>
/// Telegram writes it once, when the call ends, always from the caller to the callee (outgoing for the
/// caller), carrying the final reason and duration — whichever side hung up, declined or let it ring out
/// (observed on the production servers, 2026-11-01). Nothing is written when the call starts.
/// </remarks>
public static class PhoneCallServiceMessage
{
    /// <param name="requestInfo">The request that ended the call, when the caller sent it; otherwise null
    /// (the callee hung up, or the server expired the call), and the message is sent on the caller's behalf
    /// to all of the caller's sessions.</param>
    public static Task SendAsync(
        IMessageAppService messageAppService,
        CallSessionDocument session,
        IPhoneCallDiscardReason? reason,
        int? duration,
        bool video,
        RequestInfo? requestInfo,
        int layer)
    {
        var senderRequest = requestInfo != null && requestInfo.UserId == session.CallerId
            ? requestInfo with { ReqMsgId = 0 }
            : new RequestInfo(
                ConnectionId: "phone-call",
                SessionId: 0,
                ReqMsgId: 0,
                UserId: session.CallerId,
                AccessHashKeyId: 0,
                AuthKeyId: 0,
                PermAuthKeyId: 0,
                RequestId: Guid.NewGuid(),
                Layer: layer,
                Date: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var sendInput = new SendMessageInput(
            senderRequest,
            session.CallerId,
            new Peer(PeerType.User, session.CalleeId),
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.PhoneCall,
            messageAction: new TMessageActionPhoneCall
            {
                CallId = session.CallId,
                Reason = reason,
                Duration = duration,
                Video = video
            });

        return messageAppService.SendMessageAsync([sendInput]);
    }
}
