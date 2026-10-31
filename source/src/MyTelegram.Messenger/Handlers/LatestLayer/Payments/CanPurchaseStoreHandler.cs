using MyTelegram.Messenger.Services.Premium;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Checks whether a purchase is possible. Must be called before in-store purchase, official apps only.
/// Possible errors
/// Code Type Description
/// 400 INPUT_PURPOSE_INVALID The specified payment purpose is invalid.
/// 406 PREMIUM_CURRENTLY_UNAVAILABLE You cannot currently purchase a Premium subscription.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.canPurchaseStore"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✔]
///
/// <para>Premium purposes are refused for an account Premium is off sale for
/// (<see cref="IPremiumPurchaseGate"/>), and a gift for one whose recipient is. Official clients ask
/// here before starting a store purchase, and tdlib reports a refusal as "Payment can't be completed".</para>
/// </remarks>
internal sealed class CanPurchaseStoreHandler(IPremiumPurchaseGate premiumPurchaseGate, IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestCanPurchaseStore, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestCanPurchaseStore obj)
    {
        switch (obj.Purpose)
        {
            case TInputStorePaymentPremiumSubscription:
            case TInputStorePaymentPremiumGiveaway:
                await premiumPurchaseGate.EnsureAllowedAsync(input.UserId);
                break;

            case TInputStorePaymentGiftPremium gift:
                await premiumPurchaseGate.EnsureAllowedAsync(input.UserId, ResolveUser(input, gift.UserId));
                break;

            case TInputStorePaymentPremiumGiftCode giftCode:
                await premiumPurchaseGate.EnsureAllowedAsync([input.UserId, .. giftCode.Users.Select(u => ResolveUser(input, u))]);
                break;
        }

        return new TBoolTrue();
    }

    private long ResolveUser(IRequestInput input, IInputUser user) =>
        user is TInputUserSelf ? input.UserId : peerHelper.GetPeer(user, input.UserId).PeerId;
}
