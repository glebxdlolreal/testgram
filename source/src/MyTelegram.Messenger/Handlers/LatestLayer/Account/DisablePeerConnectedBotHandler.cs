using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Permanently disconnect a specific chat from all <a href="https://corefork.telegram.org/api/bots/connected-business-bots">business bots »</a> (equivalent to specifying it in <code>recipients.exclude_users</code> during initial configuration with <a href="https://corefork.telegram.org/method/account.updateConnectedBot">account.updateConnectedBot »</a>); to reconnect of a chat disconnected using this method the user must reconnect the entire bot by invoking <a href="https://corefork.telegram.org/method/account.updateConnectedBot">account.updateConnectedBot »</a>.
/// Possible errors
/// Code Type Description
/// 400 BOT_ALREADY_DISABLED The connected business bot was already disabled for the specified peer.
/// 400 BOT_NOT_CONNECTED_YET No business bot is connected to the currently logged in user.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.disablePeerConnectedBot"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DisablePeerConnectedBotHandler(
    IAccessHashHelper2 accessHashHelper,
    IPeerHelper peerHelper,
    IBusinessConnectionStore connectionStore,
    IBusinessBotNotifier notifier)
    : RpcResultObjectHandler<RequestDisablePeerConnectedBot, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestDisablePeerConnectedBot obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer.PeerType != PeerType.User || peer.PeerId == input.UserId)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var connection = await connectionStore.GetByUserAsync(input.UserId);
        if (connection == null)
        {
            RpcErrors.RpcErrors400.BotNotConnectedYet.ThrowRpcError();
        }

        if (connection!.ExcludeUserIds.Contains(peer.PeerId))
        {
            RpcErrors.RpcErrors400.BotAlreadyDisabled.ThrowRpcError();
        }

        // exclude_users wins over every other selection, whatever exclude_selected says.
        var updated = connection with
        {
            ExcludeUserIds = [.. connection.ExcludeUserIds, peer.PeerId],
            Recipients = connection.Recipients with
            {
                UserIds = connection.Recipients.UserIds.Where(p => p != peer.PeerId).ToList()
            },
            ConnectionId = connectionStore.NewConnectionId()
        };
        await connectionStore.SaveAsync(updated);
        await connectionStore.SetPausedAsync(input.UserId, peer.PeerId, false);
        await notifier.NotifyConnectionAsync(updated, false);

        return new TBoolTrue();
    }
}
