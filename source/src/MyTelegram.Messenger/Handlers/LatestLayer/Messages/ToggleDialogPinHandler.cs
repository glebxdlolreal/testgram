using MyTelegram.Messenger.Services.Premium;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Pin/unpin a dialog
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 PEER_HISTORY_EMPTY You can't pin an empty chat with a user.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 PINNED_DIALOGS_TOO_MUCH Too many pinned dialogs.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.toggleDialogPin"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>An <c>inputDialogPeerFolder</c> pins the chat archive itself to the top of the main list. That flag is
/// what decides whether <c>messages.getDialogs</c> carries a <c>dialogFolder</c> row at all: the live service
/// sends none for an unpinned archive (measured), and Android builds its own row locally in that case.</para>
/// </remarks>
internal sealed class ToggleDialogPinHandler(
    ICommandBus commandBus,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IPremiumLimitResolver premiumLimitResolver) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestToggleDialogPin, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestToggleDialogPin obj)
    {
        switch (obj.Peer)
        {
            case TInputDialogPeer inputDialogPeer:
                var peer = peerHelper.GetPeer(inputDialogPeer.Peer, input.UserId);
                if (obj.Pinned)
                {
                    await EnsurePinnedLimitAsync(input.UserId, peer);
                }

                var command = new ToggleDialogPinnedCommand(DialogId.Create(input.UserId, peer), input.ToRequestInfo(), obj.Pinned);
                await commandBus.PublishAsync(command, CancellationToken.None);
                return null!;
            case TInputDialogPeerFolder inputDialogPeerFolder:
                if (inputDialogPeerFolder.FolderId != MyTelegramConsts.ArchiveFolderId)
                {
                    RpcErrors.RpcErrors400.FolderIdInvalid.ThrowRpcError();
                }

                var pinFolderCommand = new UpdateArchivePinnedCommand(
                    DialogFilterSettingsId.Create(input.UserId),
                    input.ToRequestInfo(),
                    input.UserId,
                    obj.Pinned);
                await commandBus.PublishAsync(pinFolderCommand, CancellationToken.None);
                return null!;
            default:
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                return null!;
        }
    }

    /// <summary>
    /// Refuses a pin past <c>dialogs_pinned_limit_*</c> (main list) or
    /// <c>dialogs_folder_pinned_limit_*</c> (archive), the limits tdlib reads as
    /// <c>pinned_chat_count_max</c> / <c>pinned_archived_chat_count_max</c>.
    /// See https://corefork.telegram.org/api/premium#doubled-limits
    /// </summary>
    private async Task EnsurePinnedLimitAsync(long userId, Peer peer)
    {
        var dialog = await queryProcessor.ProcessAsync(new GetDialogByIdQuery(DialogId.Create(userId, peer).Value));
        if (dialog?.Pinned == true)
        {
            return;
        }

        var folderId = dialog?.FolderId ?? 0;
        var limit = await premiumLimitResolver.GetLimitAsync(userId,
            folderId == MyTelegramConsts.ArchiveFolderId ? PremiumLimit.DialogsFolderPinned : PremiumLimit.DialogsPinned);
        var pinned = await queryProcessor.ProcessAsync(
            new GetDialogsQuery(userId, true, null, new OffsetInfo(), limit + 1, null, folderId));
        if (pinned.Count >= limit)
        {
            RpcErrors.RpcErrors400.PinnedDialogsTooMuch.ThrowRpcError();
        }
    }
}
