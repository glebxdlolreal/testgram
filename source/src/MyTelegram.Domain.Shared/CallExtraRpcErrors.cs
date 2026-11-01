namespace MyTelegram;

/// <summary>
/// Hand-written RPC errors for <a href="https://corefork.telegram.org/api/end-to-end/voice-calls">calls »</a>
/// that are not present in the generated <see cref="RpcErrors"/> (<c>RpcErrors.g.cs</c>).
/// Do not add these to the generated file; it is regenerated and would lose manual edits.
/// </summary>
public static class CallExtraRpcErrors
{
    /// <summary>
    /// The <c>g_b</c> half of the call's Diffie-Hellman exchange is malformed. This is what Telegram's servers
    /// answer to a <c>g_b</c> of the wrong length (observed 2026-11-01).
    /// <code>
    /// phone.acceptCall
    /// </code>
    /// </summary>
    public static readonly RpcError DhGBInvalid = new(400, "DH_G_B_INVALID");
}
