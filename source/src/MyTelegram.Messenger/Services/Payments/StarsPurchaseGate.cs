using MongoDB.Driver;
namespace MyTelegram.Messenger.Services.Payments;

/// <summary>
/// Per-account switch that takes Telegram Stars off sale for that account, the server-side half of
/// the <a href="https://corefork.telegram.org/api/config"><c>stars_purchase_blocked</c></a> app config
/// flag; the Stars counterpart of <see cref="Premium.IPremiumPurchaseGate"/>.
/// </summary>
/// <remarks>
/// <para>Switched by hand: <c>db["stars-purchase-blocks"].updateOne({_id: NumberLong(123)}, {$set: {Blocked: true}}, {upsert: true})</c>.</para>
/// <para>A blocked account can neither buy Stars for money (top-ups, Stars gifts, Stars giveaways,
/// store purchases) nor be the recipient of a Stars gift; those are refused with
/// <c>406 PAYMENT_UNSUPPORTED</c>. Stars it already has, or earns otherwise, are unaffected.</para>
/// </remarks>
public interface IStarsPurchaseGate
{
    Task<bool> IsBlockedAsync(long userId);

    /// <summary>Throws <c>406 PAYMENT_UNSUPPORTED</c> if any of the accounts is blocked.</summary>
    Task EnsureAllowedAsync(params long[] userIds);
}

/// <inheritdoc cref="IStarsPurchaseGate" />
public sealed class StarsPurchaseGate(IMongoDatabase mongoDatabase)
    : PurchaseBlockList(mongoDatabase, CollectionName), IStarsPurchaseGate, ISingletonDependency
{
    public const string CollectionName = "stars-purchase-blocks";

    protected override void ThrowBlocked() => RpcErrors.RpcErrors406.PaymentUnsupported.ThrowRpcError();
}
