namespace MyTelegram.Messenger.Services.Phone;

/// <summary>Allocating an SFU connection for joinGroupCall and joinGroupCallPresentation.</summary>
internal static class GroupCallMediaAllocation
{
    public static string CallKey(long userId) => userId.ToString();

    public static string PresentationKey(long userId) => $"{userId}:presentation";

    public static async Task<GroupCallMediaJoinResult> AllocateAsync(
        IGroupCallMediaServer mediaServer,
        ILogger logger,
        long callId,
        string key,
        string? paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson))
        {
            RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
        }

        try
        {
            return await mediaServer.JoinAsync(callId, key, paramsJson!);
        }
        catch (GroupCallJoinParamsInvalidException ex)
        {
            logger.LogInformation("Group call {CallId}: {Key} sent join params the SFU cannot use: {Reason}",
                callId, key, ex.Message);
            RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogError(ex, "Group call {CallId}: the SFU did not allocate a connection for {Key}", callId, key);
        }

        // Telegram answers an internal failure with a bare 500 INTERNAL; the client retries the join.
        new RpcError(500, "INTERNAL").ThrowRpcError();
        return null!;
    }
}
