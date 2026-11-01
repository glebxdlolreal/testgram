namespace MyTelegram;

/// <summary>
///     The request info
/// </summary>
/// <param name="ReqMsgId">Request message id</param>
/// <param name="UserId">Request userId</param>
/// <param name="AuthKeyId">Request temp/perm auth key id</param>
/// <param name="PermAuthKeyId">Request permanent auth key id</param>
/// <param name="RequestId">Request id</param>
/// <param name="Layer">Request layer</param>
/// <param name="Date">Request date</param>
/// <param name="DeviceType">Client device type</param>
/// <param name="AddRequestIdToCache">Add the request id to cache(used by InvokeAfterMsgHandler)</param>
/// <param name="BusinessBotId">
/// Set when a connected business bot made this request on behalf of <paramref name="UserId"/> through
/// <c>invokeWithBusinessConnection</c>: the answer goes to the bot in the business form.
/// See https://corefork.telegram.org/api/bots/connected-business-bots
/// </param>
public record RequestInfo(
    string ConnectionId,
    long SessionId,
    long ReqMsgId,
    long UserId,
    long AccessHashKeyId,
    long AuthKeyId,
    long PermAuthKeyId,
    Guid RequestId,
    int Layer,
    long Date,
    DeviceType DeviceType = DeviceType.Desktop,
    bool AddRequestIdToCache = true,
    bool IsSubRequest = false,
    long? BusinessBotId = null
    ) : IRequestWithAccessHashKeyId
{
    public static RequestInfo Empty { get; } = new(string.Empty, 0, 0, 0, 0, 0, 0, Guid.Empty, 0, 0);

    /// <summary>Request layer</summary>
    public int Layer { get; set; } = Layer;
}