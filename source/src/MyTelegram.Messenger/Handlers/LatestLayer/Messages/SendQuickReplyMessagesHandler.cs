using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Send <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">quick reply messages »</a>.
/// Only private chats with users are supported. The answer is <c>updates</c> with an <c>updateMessageID</c>
/// and an <c>updateNewMessage</c> per sent message, which tdlib checks against the random ids it sent.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 RANDOM_ID_INVALID A provided random ID is invalid.
/// 400 SHORTCUT_INVALID The specified shortcut is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.sendQuickReplyMessages"/> </c></para>
/// </summary>
internal sealed class SendQuickReplyMessagesHandler(
    IPeerHelper peerHelper,
    IAccessHashHelper2 accessHashHelper,
    IUserAppService userAppService,
    IQuickReplyService quickReplyService,
    IMessageAppService messageAppService)
    : RpcResultObjectHandler<RequestSendQuickReplyMessages, IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestSendQuickReplyMessages obj)
    {
        var userId = input.UserId;
        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);
        var peer = peerHelper.GetPeer(obj.Peer, userId);
        if (peer.PeerType is not (PeerType.User or PeerType.Self))
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        await userAppService.CheckAccountPremiumStatusAsync(userId);

        var shortcuts = await quickReplyService.GetShortcutsAsync(userId);
        if (shortcuts.All(p => p.ShortcutId != obj.ShortcutId))
        {
            RpcErrors.RpcErrors400.ShortcutInvalid.ThrowRpcError();
        }

        var ids = obj.Id?.ToList() ?? [];
        var randomIds = obj.RandomId?.ToList() ?? [];
        if (ids.Count == 0 || ids.Count != randomIds.Count || randomIds.Contains(0) ||
            randomIds.Distinct().Count() != randomIds.Count)
        {
            RpcErrors.RpcErrors400.RandomIdInvalid.ThrowRpcError();
        }

        var stored = (await quickReplyService.GetMessagesAsync(userId, [obj.ShortcutId], ids))
            .ToDictionary(p => p.MessageId);
        if (ids.Any(id => !stored.ContainsKey(id)))
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        // Sent in the order given, which is the order the random ids belong to.
        var messages = ids.Select(id => stored[id]).ToList();
        var inputs = quickReplyService.BuildSendInputs(input.ToRequestInfo(), userId, peer, messages, randomIds);
        await messageAppService.SendMessageAsync(inputs);

        // Answered by the send pipeline once the messages exist.
        return null!;
    }
}
