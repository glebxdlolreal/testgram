using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Set the default peer that will be used to join a group call in a specific dialog.
/// Possible errors
/// Code Type Description
/// 400 JOIN_AS_PEER_INVALID The specified peer cannot be used to join a group call.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.saveDefaultGroupCallJoinAs"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SaveDefaultGroupCallJoinAsHandler(
    IPeerHelper peerHelper,
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestSaveDefaultGroupCallJoinAs, IBool>
{
    private readonly IMongoCollection<DefaultGroupCallJoinAsDocument> _defaultJoinAsCollection =
        mongoDatabase.GetCollection<DefaultGroupCallJoinAsDocument>("default_group_call_join_as");

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestSaveDefaultGroupCallJoinAs obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var joinAs = GroupCallStateHelper.NormalizeSelf(peerHelper.GetPeer(obj.JoinAs, input.UserId));
        if (peer == null || joinAs == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            return null!;
        }

        var document = new DefaultGroupCallJoinAsDocument
        {
            Id = $"{input.UserId}:{(int)peer.PeerType}:{peer.PeerId}",
            UserId = input.UserId,
            PeerId = peer.PeerId,
            PeerType = (int)peer.PeerType,
            JoinAsPeerId = joinAs.PeerId,
            JoinAsPeerType = (int)joinAs.PeerType,
            Date = GroupCallStateHelper.CurrentDate()
        };

        await _defaultJoinAsCollection.ReplaceOneAsync(
            saved => saved.Id == document.Id,
            document,
            new ReplaceOptions { IsUpsert = true });

        return new TBoolTrue();
    }
}
