using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Get payment receipt
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getPaymentReceipt"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetPaymentReceiptHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetPaymentReceipt, MyTelegram.Schema.Payments.IPaymentReceipt>
{
    protected override async Task<MyTelegram.Schema.Payments.IPaymentReceipt> HandleCoreAsync(
        IRequestInput input, MyTelegram.Schema.Payments.RequestGetPaymentReceipt obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        // msg_id is the id of the payment service message the checkout generated, in the caller's own
        // id space — tdlib's get_payment_receipt passes exactly that message id through.
        var receipt = await ResolveReceiptAsync(input.UserId, obj.MsgId);
        if (receipt == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        // Only the two parties to the payment may read it back.
        if (receipt!.BuyerUserId != input.UserId && receipt.BotId != input.UserId)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var users = new TVector<IUser>();
        var bot = await queryProcessor.ProcessAsync(new GetUserByIdQuery(receipt.BotId));
        if (bot != null)
        {
            users.Add(userConverterService.ToUser(input, bot, layer: input.Layer));
        }

        // Paid by card: the ordinary receipt (clients open the card check for it, and the Stars
        // transaction screen for paymentReceiptStars).
        if (receipt.Currency != BotInvoiceHelper.StarsCurrency)
        {
            return new MyTelegram.Schema.Payments.TPaymentReceipt
            {
                Date = receipt.Date,
                BotId = receipt.BotId,
                // The bots that sell for money here are their own payment provider (see the payment form).
                ProviderId = receipt.BotId,
                Title = receipt.Title,
                Description = receipt.Description,
                Photo = PaymentReceiptHelper.ReadPhoto(receipt),
                Invoice = PaymentReceiptHelper.ReadInvoice(receipt),
                Currency = receipt.Currency,
                TotalAmount = receipt.TotalAmount,
                CredentialsTitle = receipt.CredentialsTitle ?? "Card",
                Users = users
            };
        }

        return new MyTelegram.Schema.Payments.TPaymentReceiptStars
        {
            Date = receipt.Date,
            BotId = receipt.BotId,
            Title = receipt.Title,
            Description = receipt.Description,
            Photo = PaymentReceiptHelper.ReadPhoto(receipt),
            Invoice = PaymentReceiptHelper.ReadInvoice(receipt),
            Currency = receipt.Currency,
            TotalAmount = receipt.TotalAmount,
            TransactionId = receipt.TransactionId,
            Users = users
        };
    }

    /// <summary>
    /// Finds the receipt behind the service message the caller quoted.
    /// </summary>
    /// <remarks>
    /// The record is keyed by the buyer's copy of the message, because the buyer is its sender. When
    /// the bot asks, it quotes its own inbox copy, whose read model carries <c>SenderPeerId</c> /
    /// <c>SenderMessageId</c> pointing back at the buyer's copy.
    /// </remarks>
    private async Task<PaymentReceiptDocument?> ResolveReceiptAsync(long callerUserId, int msgId)
    {
        var direct = await PaymentReceiptHelper.FindAsync(mongoDatabase, callerUserId, msgId);
        if (direct != null)
        {
            return direct;
        }

        var messageDoc = await mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel")
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("MessageId", msgId),
                Builders<BsonDocument>.Filter.Eq("OwnerPeerId", callerUserId)))
            .FirstOrDefaultAsync();

        if (messageDoc == null ||
            !messageDoc.TryGetValue("SenderPeerId", out var senderPeerId) ||
            !messageDoc.TryGetValue("SenderMessageId", out var senderMessageId) ||
            senderPeerId.IsBsonNull || senderMessageId.IsBsonNull)
        {
            return null;
        }

        return await PaymentReceiptHelper.FindAsync(
            mongoDatabase,
            senderPeerId.ToInt64(),
            senderMessageId.ToInt32());
    }
}