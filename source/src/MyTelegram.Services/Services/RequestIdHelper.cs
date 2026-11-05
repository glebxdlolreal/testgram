namespace MyTelegram.Services.Services;

/// <summary>
/// The RequestId a request is processed under.
/// <para>
/// The gateway cannot decrypt a frame, so it stamps one RequestId per encrypted frame; the session-server
/// unpacks a msg_container and hands that same id to every message inside it. Sagas are keyed by RequestId
/// (<c>{saga}-{RequestId}</c>), so several sendMessage calls a client packed into one container merged into
/// a single send saga: one message was created and the other calls never got an answer. Replies are matched
/// by req_msg_id, not by RequestId, so each message gets its own id derived from the frame's id and its
/// msg_id: unique inside the frame and the same every time that message is processed.
/// </para>
/// </summary>
public static class RequestIdHelper
{
    public static Guid ForMessage(Guid frameRequestId, long reqMsgId)
    {
        if (reqMsgId == 0)
        {
            return frameRequestId;
        }

        Span<byte> bytes = stackalloc byte[16];
        frameRequestId.TryWriteBytes(bytes);
        var low = BitConverter.ToInt64(bytes[8..]) ^ reqMsgId;
        BitConverter.TryWriteBytes(bytes[8..], low);

        return new Guid(bytes);
    }
}
