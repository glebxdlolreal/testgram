using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Pause or unpause a specific chat, temporarily disconnecting it from all <a href="https://corefork.telegram.org/api/bots/connected-business-bots">business bots »</a>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.toggleConnectedBotPaused"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleConnectedBotPausedHandler(
    IAccessHashHelper2 accessHashHelper,
    IPeerHelper peerHelper,
    IBusinessConnectionStore connectionStore)
    : RpcResultObjectHandler<RequestToggleConnectedBotPaused, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestToggleConnectedBotPaused obj)
    {
        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer.PeerType != PeerType.User || peer.PeerId == input.UserId)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        if (await connectionStore.GetByUserAsync(input.UserId) == null)
        {
            RpcErrors.RpcErrors400.BotNotConnectedYet.ThrowRpcError();
        }

        await connectionStore.SetPausedAsync(input.UserId, peer.PeerId, obj.Paused);
        return new TBoolTrue();
    }
}
