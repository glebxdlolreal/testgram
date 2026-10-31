using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
namespace MyTelegram.Messenger.Services.Payments;

/// <summary>
/// Settles Stars bought for money: credits the buyer, or the friend they are a gift for, who then gets
/// the <c>messageActionGiftStars</c> service message from the buyer.
/// </summary>
public static class StarsPurchaseHelper
{
    public static async Task CreditAsync(IMongoDatabase mongoDatabase, IMessageAppService messageAppService, IRequestInput input,
        long recipientUserId, long stars, string currency, long amount, string transactionId)
    {
        var isGift = recipientUserId > 0 && recipientUserId != input.UserId;
        var creditUserId = isGift ? recipientUserId : input.UserId;

        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, creditUserId, stars);

        if (!isGift)
        {
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, creditUserId, stars,
                title: $"Stripe top-up: {stars} stars");
            return;
        }

        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -stars,
            title: $"Gift {stars} Stars", peerUserId: recipientUserId);
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, creditUserId, stars,
            gift: true, title: $"Gift {stars} Stars", peerUserId: input.UserId);

        await messageAppService.SendMessageAsync([new SendMessageInput(
            // Its own request: a send saga is keyed by the request id, so a second message sent on the
            // caller's request would merge into the first one and be lost.
            input.ToRequestInfo() with { ReqMsgId = 0, RequestId = Guid.NewGuid() },
            input.UserId,
            new Peer(PeerType.User, recipientUserId),
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: new TMessageActionGiftStars
            {
                Currency = currency,
                Amount = amount,
                Stars = stars,
                TransactionId = transactionId,
            })]);
    }
}
