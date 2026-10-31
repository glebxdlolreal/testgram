using MyTelegram.Messenger.Services.Payments;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Premium;

/// <summary>
/// Per-account switch that takes Telegram Premium off sale for that account, the server-side half of
/// the <a href="https://corefork.telegram.org/api/config#premium-purchase-blocked"><c>premium_purchase_blocked</c></a>
/// app config flag.
/// </summary>
/// <remarks>
/// <para>The switch is a document in <see cref="CollectionName"/>, edited by hand:
/// <c>db["premium-purchase-blocks"].updateOne({_id: NumberLong(123)}, {$set: {Blocked: true}}, {upsert: true})</c>.
/// It lives in its own collection rather than on the user read model because read models are only
/// ever written through aggregate events and are cached without expiry in every process; a hand edit
/// there would neither survive a rebuild nor be seen until a restart. Lookups here are cached for
/// <see cref="CacheDuration"/>, so an edit takes effect within that.</para>
/// <para>A blocked account can neither buy Premium nor receive it: gifts, gift codes, giveaways and
/// store purchases naming it are refused with <c>406 PREMIUM_CURRENTLY_UNAVAILABLE</c>, the one
/// "Premium unavailable" error the API documents (payments.canPurchaseStore). Clients hide the UI
/// themselves once app config says so; the server refuses regardless of what the client shows.</para>
/// </remarks>
public interface IPremiumPurchaseGate
{
    Task<bool> IsBlockedAsync(long userId);

    /// <summary>Throws <c>406 PREMIUM_CURRENTLY_UNAVAILABLE</c> if any of the accounts is blocked.</summary>
    Task EnsureAllowedAsync(params long[] userIds);
}

/// <inheritdoc cref="IPremiumPurchaseGate" />
public sealed class PremiumPurchaseGate(IMongoDatabase mongoDatabase)
    : PurchaseBlockList(mongoDatabase, CollectionName), IPremiumPurchaseGate, ISingletonDependency
{
    public const string CollectionName = "premium-purchase-blocks";

    /// <summary>The one "Premium unavailable" error the API documents (payments.canPurchaseStore).</summary>
    protected override void ThrowBlocked() => RpcErrors.RpcErrors406.PremiumCurrentlyUnavailable.ThrowRpcError();
}
