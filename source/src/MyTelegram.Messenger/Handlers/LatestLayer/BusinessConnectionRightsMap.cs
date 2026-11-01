using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Account;
using MyTelegram.Schema.Messages;
using MyTelegram.Schema.Payments;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers;

/// <summary>
/// What a method wrapped in <c>invokeWithBusinessConnection</c> needs.
/// </summary>
/// <param name="IsAllowed">Whether the connection's rights cover this call.</param>
/// <param name="ChatPeer">The private chat the call acts in, checked against the connection's recipients.</param>
/// <param name="NeedsRecentIncomingMessage">
/// Sending is only possible into chats the peer wrote to within the last 24 hours ("can reply").
/// </param>
/// <param name="SelfPeer">A peer the method names that has to be the business account itself.</param>
internal sealed record BusinessCallRule(
    Func<BusinessBotRightsItem, bool> IsAllowed,
    IInputPeer? ChatPeer = null,
    bool NeedsRecentIncomingMessage = false,
    IInputPeer? SelfPeer = null);

/// <summary>
/// The closed set of methods a <a href="https://corefork.telegram.org/api/bots/connected-business-bots">connected
/// business bot</a> may wrap, each mapped to the <c>businessBotRights</c> it needs. The inner request runs as the
/// account, so everything outside this table fails with <c>BUSINESS_CONNECTION_NOT_ALLOWED</c>.
/// The list is the documented one plus <c>account.updateUsername</c>, which tdlib wraps for
/// <c>setBusinessAccountUsername</c>.
/// </summary>
internal static class BusinessConnectionRightsMap
{
    public static BusinessCallRule? GetRule(IObject query)
    {
        return query switch
        {
            RequestSendMessage r => new(p => p.Reply, r.Peer, true),
            RequestSendMedia r => new(p => p.Reply, r.Peer, true),
            RequestSendMultiMedia r => new(p => p.Reply, r.Peer, true),
            RequestEditMessage r => new(p => p.Reply, r.Peer),
            RequestSetTyping r => new(p => p.Reply, r.Peer),
            RequestUpdatePinnedMessage r => new(p => p.Reply, r.Peer),
            MyTelegram.Schema.Messages.RequestReadHistory r => new(p => p.ReadMessages, r.Peer),
            // Which of the two rights applies depends on each message; the handler checks that.
            MyTelegram.Schema.Messages.RequestDeleteMessages => new(p => p.DeleteSentMessages || p.DeleteReceivedMessages),
            RequestUpdateProfile r => new(p =>
                (r.FirstName == null && r.LastName == null || p.EditName) && (r.About == null || p.EditBio)),
            MyTelegram.Schema.Account.RequestUpdateUsername => new(p => p.EditUsername),
            RequestSetGlobalPrivacySettings => new(p => p.ChangeGiftSettings),
            RequestGetSavedStarGifts r => new(p => p.ViewGifts, SelfPeer: r.Peer),
            RequestGetStarsStatus r => new(p => p.ViewGifts, SelfPeer: r.Peer),
            RequestConvertStarGift => new(p => p.SellGifts),
            RequestTransferStarGift => new(p => p.TransferAndUpgradeGifts),
            RequestUpgradeStarGift => new(p => p.TransferAndUpgradeGifts),
            RequestGetPaymentForm => new(p => p.TransferAndUpgradeGifts || p.TransferStars),
            RequestSendStarsForm => new(p => p.TransferAndUpgradeGifts || p.TransferStars),
            RequestExportInvoice => new(p => p.TransferStars),
            RequestDeleteStories r => new(p => p.ManageStories, SelfPeer: r.Peer),
            _ => null
        };
    }
}
