using MyTelegram.Messenger.Services.Boosts;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Premium;

/// <summary>
/// Delivers a paid-for Telegram Premium gift: the recipient's term, the boost slots it brings and the
/// <c>messageActionGiftPremium</c> service message in the private chat.
/// </summary>
/// <remarks>
/// See https://corefork.telegram.org/api/premium#gifting-telegram-premium : the recipient
/// automatically receives the service message, the gifter gets <c>boosts_per_sent_gift</c> boost
/// slots and the recipient one.
/// </remarks>
public interface IPremiumGiftService
{
    Task<PremiumGrantResult> DeliverAsync(IRequestInput input, long recipientUserId, int months, string currency, long amount,
        ITextWithEntities? message, string source);
}

/// <inheritdoc />
public sealed class PremiumGiftService(
    IMongoDatabase mongoDatabase,
    IPremiumSubscriptionService subscriptionService,
    IMessageAppService messageAppService,
    IAppConfigHelper appConfigHelper,
    IRandomHelper randomHelper) : IPremiumGiftService, ITransientDependency
{
    public const int BoostsPerSentGiftFallback = 3;

    public async Task<PremiumGrantResult> DeliverAsync(IRequestInput input, long recipientUserId, int months, string currency,
        long amount, ITextWithEntities? message, string source)
    {
        var days = PremiumDuration.DaysFromMonths(months);
        var result = await subscriptionService.GrantAsync(recipientUserId, days, source, input.UserId, gift: recipientUserId != input.UserId);

        if (recipientUserId == input.UserId)
        {
            return result;
        }

        var boostsPerGift = appConfigHelper.GetInt32Value("boosts_per_sent_gift", BoostsPerSentGiftFallback);
        await BoostSlotHelper.GrantAsync(mongoDatabase, input.UserId, boostsPerGift, result.ExpiresAt, gift: true);

        var action = new TMessageActionGiftPremium
        {
            Currency = currency,
            Amount = amount,
            Days = days,
            Message = message
        };

        await messageAppService.SendMessageAsync([new SendMessageInput(
            // Its own request: a send saga is keyed by the request id, so a second message sent on the
            // caller's request would merge into the first one and be lost.
            input.ToRequestInfo() with { ReqMsgId = 0, RequestId = Guid.NewGuid() },
            input.UserId,
            new Peer(PeerType.User, recipientUserId),
            string.Empty,
            randomHelper.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action)]);

        return result;
    }
}
