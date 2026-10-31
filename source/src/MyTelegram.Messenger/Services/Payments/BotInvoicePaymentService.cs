using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.Messaging;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services.StarsSubscriptions;

namespace MyTelegram.Messenger.Services.Payments;

/// <summary>
/// Settles a bot invoice: pre-checkout, the Stars transfer with its affiliate split, the service
/// message both parties see, and the receipt behind it.
/// </summary>
/// <remarks>
/// Shared by <c>payments.sendPaymentForm</c> and <c>payments.sendStarsForm</c>. Which of the two a
/// client calls is not a choice the server makes: tdlib's <c>send_payment_form</c> sends
/// <c>sendStarsForm</c> whenever no card credentials are involved, which for a Telegram Stars invoice
/// is always, and <c>sendPaymentForm</c> otherwise. Both therefore have to end up here.
/// See https://corefork.telegram.org/api/payments
/// </remarks>
public interface IBotInvoicePaymentService
{
    /// <summary>True when this invoice is a bot invoice backed by a server side record.</summary>
    Task<bool> CanHandleAsync(IRequestInput input, IInputInvoice invoice);

    Task<Schema.Payments.IPaymentResult> PayAsync(
        IRequestInput input,
        IInputInvoice invoice,
        string? requestedInfoId,
        string? shippingOptionId);

    /// <summary>
    /// Records an invoice that was settled outside the Stars ledger (a card charge for a built-in bot's
    /// invoice): the <c>messageActionPaymentSent</c> receipt, <c>receipt_msg_id</c> on the invoice
    /// message and the stored receipt <c>payments.getPaymentReceipt</c> reads.
    /// </summary>
    /// <param name="credentialsTitle">How the card shows on the receipt ("Visa •••• 4242"), when known.</param>
    Task CompleteExternalChargeAsync(IRequestInput input, BotInvoiceDocument storedInvoice, string chargeId,
        string? credentialsTitle = null);
}

public class BotInvoicePaymentService(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IObjectMessageSender objectMessageSender,
    IMessageAppService messageAppService,
    ICommandBus commandBus,
    IIdGenerator idGenerator,
    IStarsSubscriptionService starsSubscriptionService,
    ILogger<BotInvoicePaymentService> logger) : IBotInvoicePaymentService, ITransientDependency
{
    /// <summary>"Telegram must receive an answer within 10 seconds after the pre-checkout query was sent."</summary>
    private static readonly TimeSpan PrecheckoutTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PrecheckoutPollInterval = TimeSpan.FromMilliseconds(500);

    public Task CompleteExternalChargeAsync(IRequestInput input, BotInvoiceDocument storedInvoice, string chargeId,
        string? credentialsTitle = null)
    {
        return CompleteAsync(input, storedInvoice, BotInvoiceHelper.ReadInvoice(storedInvoice), chargeId,
            requestedInfo: null, shippingOptionId: null, credentialsTitle);
    }

    public async Task<bool> CanHandleAsync(IRequestInput input, IInputInvoice invoice)
    {
        return await BotInvoiceHelper.ResolveAsync(mongoDatabase, peerHelper, input, invoice) != null;
    }

    public async Task<Schema.Payments.IPaymentResult> PayAsync(
        IRequestInput input,
        IInputInvoice invoice,
        string? requestedInfoId,
        string? shippingOptionId)
    {
        // The invoice the bot actually created — payload, provider and every invoice flag — lives in
        // the server side store; messageMediaInvoice only ever carried the display fields.
        var storedInvoice = await BotInvoiceHelper.ResolveAsync(mongoDatabase, peerHelper, input, invoice);
        if (storedInvoice == null)
        {
            RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
        }

        var botId = storedInvoice!.BotId;
        var totalAmount = storedInvoice.TotalAmount;
        var title = storedInvoice.Title;
        var payload = storedInvoice.Payload;
        var invoiceDetails = BotInvoiceHelper.ReadInvoice(storedInvoice);

        var botUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(botId));
        if (botUser == null || !botUser.Bot)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        if (storedInvoice.Currency != BotInvoiceHelper.StarsCurrency)
        {
            RpcErrors.RpcErrors400.CurrencyTotalAmountInvalid.ThrowRpcError();
        }

        // The order information the user validated earlier travels with the pre-checkout query and
        // ends up on the service message the bot receives.
        var requestedInfo = await ReadValidatedInfoAsync(input.UserId, requestedInfoId);
        if (invoiceDetails.ShippingAddressRequested && requestedInfo?.ShippingAddress == null)
        {
            RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
        }

        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
        if (balance < totalAmount)
        {
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
        }

        var (precheckoutSuccess, precheckoutError) = await SendPrecheckoutQueryAndWaitAsync(
            botId, input.UserId, payload, storedInvoice.Currency, totalAmount, requestedInfo, shippingOptionId);

        if (!precheckoutSuccess)
        {
            // The bot's own wording is shown to the user, so it is passed through verbatim.
            throw new RpcException(new RpcError(400, precheckoutError ?? "PRECHECKOUT_FAILED"));
        }

        var (commissionAmount, commissionPermille, affiliatePeerId, affiliatePeerType) =
            await ResolveAffiliateAsync(input.UserId, botId, totalAmount);

        // Debit atomically: TryDebitAsync only succeeds when the balance already covers the amount, so
        // two concurrent checkouts cannot both pass the earlier read-then-write check and mint stars.
        if (!await StarsBalanceHelper.TryDebitAsync(mongoDatabase, input.UserId, totalAmount))
        {
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
        }

        var botAmount = totalAmount - commissionAmount;
        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, botId, botAmount);

        await StarsBalanceHelper.AddTransactionAsync(
            mongoDatabase, input.UserId, -totalAmount, title: title, peerUserId: botId);

        // The bot's credit row is the charge: its id is what the bot quotes back to
        // payments.refundStarsCharge. See https://corefork.telegram.org/api/payments#6-refunds
        string chargeId;

        if (commissionAmount > 0 && affiliatePeerId > 0)
        {
            chargeId = await StarsBalanceHelper.AddTransactionAsync(
                mongoDatabase,
                botId,
                botAmount,
                title: title,
                peerUserId: input.UserId,
                starrefCommissionPermille: commissionPermille,
                starrefPeerUserId: affiliatePeerType == PeerType.User ? affiliatePeerId : null,
                starrefPeerChannelId: affiliatePeerType == PeerType.Channel ? affiliatePeerId : null,
                starrefAmount: commissionAmount);

            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, affiliatePeerId, commissionAmount);

            await StarsBalanceHelper.AddTransactionAsync(
                mongoDatabase, affiliatePeerId, commissionAmount,
                title: $"Commission from {title}", peerUserId: botId);

            var connectionsCol = mongoDatabase.GetCollection<BsonDocument>("connected_star_ref_bots");
            await connectionsCol.UpdateOneAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("bot_id", botId),
                    affiliatePeerType == PeerType.User
                        ? Builders<BsonDocument>.Filter.Eq("peer_user_id", affiliatePeerId)
                        : Builders<BsonDocument>.Filter.Eq("peer_channel_id", affiliatePeerId)),
                Builders<BsonDocument>.Update.Inc("participants", 1).Inc("revenue", commissionAmount));

            logger.LogInformation("Affiliate commission transferred: affiliate={Affiliate} amount={Amount}",
                affiliatePeerId, commissionAmount);
        }
        else
        {
            chargeId = await StarsBalanceHelper.AddTransactionAsync(
                mongoDatabase, botId, botAmount, title: title, peerUserId: input.UserId);
        }

        logger.LogInformation("Bot invoice paid: userId={UserId} botId={BotId} amount={Amount} commission={Commission}",
            input.UserId, botId, totalAmount, commissionAmount);

        await CompleteAsync(input, storedInvoice, invoiceDetails, chargeId, requestedInfo, shippingOptionId);

        return new Schema.Payments.TPaymentResult
        {
            Updates = new TUpdates
            {
                Updates = new TVector<IUpdate>(),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = DateTime.UtcNow.ToTimestamp(),
                Seq = 0
            }
        };
    }

    /// <summary>
    /// The buyer's own copy of the invoice message, or null when there is none to point at.
    /// </summary>
    /// <remarks>
    /// The stored record is keyed by the bot's copy; the two copies of a private chat message are
    /// linked by <c>BatchId</c>. Both the service message's <c>reply_to</c> and
    /// <c>receipt_msg_id</c> address messages inside the buyer's chat with the bot, so an invoice that
    /// was posted to a channel — or exported as a bare link, with no message at all — has nothing to
    /// link to and is skipped.
    /// </remarks>
    private async Task<IMessageReadModel?> ResolveBuyerInvoiceMessageAsync(long buyerUserId, BotInvoiceDocument storedInvoice)
    {
        if (storedInvoice.MsgId == 0)
        {
            return null;
        }

        var botCopy = await queryProcessor.ProcessAsync(new GetMessageByIdQuery(
            MessageId.Create(storedInvoice.OwnerPeerId, storedInvoice.MsgId).Value));

        if (botCopy == null || botCopy.ToPeerType != PeerType.User)
        {
            return null;
        }

        if (botCopy.OwnerPeerId == buyerUserId)
        {
            return botCopy;
        }

        if (botCopy.BatchId == Guid.Empty)
        {
            return null;
        }

        var buyerCopy = await queryProcessor.ProcessAsync(
            new GetMessageByBatchIdQuery(botCopy.BatchId, botCopy.OwnerPeerId));

        return buyerCopy?.OwnerPeerId == buyerUserId ? buyerCopy : null;
    }

    private async Task<(long Amount, int Permille, long PeerId, PeerType PeerType)> ResolveAffiliateAsync(
        long userId,
        long botId,
        long totalAmount)
    {
        var referral = await mongoDatabase.GetCollection<BsonDocument>("star_referrals")
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("user_id", userId),
                Builders<BsonDocument>.Filter.Eq("bot_id", botId),
                Builders<BsonDocument>.Filter.Eq("revoked", false)))
            .FirstOrDefaultAsync();

        if (referral == null)
        {
            return (0, 0, 0, PeerType.User);
        }

        var permille = referral["commission_permille"].AsInt32;
        var amount = totalAmount * permille / 1000;

        if (referral.Contains("peer_user_id") && !referral["peer_user_id"].IsBsonNull && referral["peer_user_id"].AsInt64 > 0)
        {
            return (amount, permille, referral["peer_user_id"].AsInt64, PeerType.User);
        }

        if (referral.Contains("peer_channel_id") && !referral["peer_channel_id"].IsBsonNull && referral["peer_channel_id"].AsInt64 > 0)
        {
            return (amount, permille, referral["peer_channel_id"].AsInt64, PeerType.Channel);
        }

        return (0, 0, 0, PeerType.User);
    }

    /// <summary>
    /// Reads back the order information the client validated with payments.validateRequestedInfo.
    /// </summary>
    /// <remarks>
    /// The record is scoped to the caller, so quoting somebody else's <c>requested_info_id</c> yields
    /// nothing rather than another user's address.
    /// </remarks>
    private async Task<IPaymentRequestedInfo?> ReadValidatedInfoAsync(long userId, string? requestedInfoId)
    {
        if (string.IsNullOrEmpty(requestedInfoId))
        {
            return null;
        }

        var validation = await mongoDatabase.GetCollection<BsonDocument>("payment-requested-info-validations")
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", requestedInfoId),
                Builders<BsonDocument>.Filter.Eq("user_id", userId)))
            .FirstOrDefaultAsync();

        if (validation == null ||
            !validation.TryGetValue("info", out var infoValue) ||
            !infoValue.IsBsonBinaryData)
        {
            return null;
        }

        var buffer = new ReadOnlyMemory<byte>(infoValue.AsBsonBinaryData.Bytes);
        return buffer.Read<IPaymentRequestedInfo>();
    }

    /// <summary>
    /// Asks the bot to confirm the order and waits for messages.setBotPrecheckoutResults.
    /// </summary>
    private async Task<(bool Success, string? Error)> SendPrecheckoutQueryAndWaitAsync(
        long botId,
        long userId,
        byte[] payload,
        string currency,
        long totalAmount,
        IPaymentRequestedInfo? requestedInfo,
        string? shippingOptionId)
    {
        var queryId = Random.Shared.NextInt64();
        var collection = mongoDatabase.GetCollection<BsonDocument>("pending_precheckout_queries");
        var queryFilter = Builders<BsonDocument>.Filter.Eq("query_id", queryId);

        await collection.InsertOneAsync(new BsonDocument
        {
            ["_id"] = $"precheckout-{queryId}",
            ["query_id"] = queryId,
            ["bot_id"] = botId,
            ["user_id"] = userId,
            ["payload"] = payload,
            ["currency"] = currency,
            ["total_amount"] = totalAmount,
            ["created_at"] = DateTime.UtcNow.ToTimestamp(),
            ["success"] = false,
            ["error"] = "",
            ["responded_at"] = 0
        });

        var update = new TUpdateBotPrecheckoutQuery
        {
            QueryId = queryId,
            UserId = userId,
            Payload = payload,
            Currency = currency,
            TotalAmount = totalAmount,
            Info = requestedInfo,
            ShippingOptionId = shippingOptionId
        };

        try
        {
            await objectMessageSender.PushMessageToPeerAsync(
                new Peer(PeerType.User, botId),
                new TUpdates
                {
                    Updates = new TVector<IUpdate> { update },
                    Users = new TVector<IUser>(),
                    Chats = new TVector<IChat>(),
                    Date = DateTime.UtcNow.ToTimestamp(),
                    Seq = 0
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send precheckout query to bot: queryId={QueryId} botId={BotId}", queryId, botId);
            await collection.DeleteOneAsync(queryFilter);
            return (false, "BOT_PRECHECKOUT_TIMEOUT");
        }

        var deadline = DateTime.UtcNow + PrecheckoutTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PrecheckoutPollInterval);

            var query = await collection.Find(queryFilter).FirstOrDefaultAsync();
            if (query == null || query["responded_at"].AsInt32 <= 0)
            {
                continue;
            }

            var success = query["success"].AsBoolean;
            var error = query.Contains("error") && !query["error"].IsBsonNull ? query["error"].AsString : null;

            await collection.DeleteOneAsync(queryFilter);

            logger.LogInformation("Bot precheckout response received: queryId={QueryId} success={Success}", queryId, success);

            return (success, error);
        }

        await collection.DeleteOneAsync(queryFilter);
        logger.LogWarning("Bot precheckout timeout: queryId={QueryId} botId={BotId}", queryId, botId);

        return (false, "BOT_PRECHECKOUT_TIMEOUT");
    }

    /// <summary>
    /// Emits the service message the checkout is required to generate and stores the receipt behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One message is created, in the buyer's chat with the bot. It is stored as
    /// <c>messageActionPaymentSentMe</c> — payload and charge included — and
    /// <c>MessageServiceMapper</c> downgrades it to <c>messageActionPaymentSent</c> for everyone who is
    /// not the bot, the same way it already handles Passport's <c>secureValuesSentMe</c>.
    /// </para>
    /// <para>
    /// The message <em>replies to the invoice</em>, as the API requires: tdlib builds
    /// <c>messagePaymentSuccessful.invoice_chat_id</c> / <c>invoice_message_id</c> out of the service
    /// message's <c>reply_to</c> (<c>MessageContent.cpp</c>, <c>messageActionPaymentSent</c> branch),
    /// so without it the client cannot jump from the receipt back to what was bought.
    /// </para>
    /// <para>
    /// Clients open the receipt from this very message (tdlib's <c>get_payment_receipt</c> passes the
    /// payment-successful message id straight to <c>payments.getPaymentReceipt</c>), so the receipt is
    /// keyed by it. The invoice message additionally gets <c>receipt_msg_id</c> pointing back here,
    /// which is what turns its <em>Pay</em> button into <em>Receipt</em>.
    /// </para>
    /// <para>See https://corefork.telegram.org/api/payments#5-checkout </para>
    /// </remarks>
    private async Task CompleteAsync(
        IRequestInput input,
        BotInvoiceDocument storedInvoice,
        IInvoice invoiceDetails,
        string chargeId,
        IPaymentRequestedInfo? requestedInfo,
        string? shippingOptionId,
        string? credentialsTitle = null)
    {
        // The buyer is the sender of the service message, so its id comes from the buyer's id space —
        // the same one payments.getPaymentReceipt will quote back.
        var receiptMessageId = (int)await idGenerator.NextIdAsync(IdType.MessageId, input.UserId);
        var buyerInvoiceMessage = await ResolveBuyerInvoiceMessageAsync(input.UserId, storedInvoice);

        var action = new TMessageActionPaymentSentMe
        {
            Currency = storedInvoice.Currency,
            TotalAmount = storedInvoice.TotalAmount,
            Payload = storedInvoice.Payload,
            Info = requestedInfo,
            ShippingOptionId = shippingOptionId,
            Charge = new TPaymentCharge { Id = chargeId, ProviderChargeId = chargeId },
            RecurringInit = invoiceDetails.Recurring
        };

        await messageAppService.SendMessageAsync([
            new SendMessageInput(
                // Its own request: a send saga is keyed by the request id, so a second message sent on the
                // caller's request would merge into the first one and be lost.
                input.ToRequestInfo() with { ReqMsgId = 0, RequestId = Guid.NewGuid() },
                input.UserId,
                new Peer(PeerType.User, storedInvoice.BotId),
                string.Empty,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: action,
                messageId: receiptMessageId,
                inputReplyTo: buyerInvoiceMessage == null
                    ? null
                    : new TInputReplyToMessage { ReplyToMsgId = buyerInvoiceMessage.MessageId })
        ]);

        if (buyerInvoiceMessage != null)
        {
            await InvoiceReceiptPublisher.PublishToAllCopiesAsync(
                commandBus,
                queryProcessor,
                buyerInvoiceMessage,
                input.ToRequestInfo() with { ReqMsgId = 0 },
                receiptMessageId);
        }

        await PaymentReceiptHelper.SaveAsync(mongoDatabase, new PaymentReceiptDocument
        {
            Id = PaymentReceiptHelper.MakeId(input.UserId, receiptMessageId),
            OwnerPeerId = input.UserId,
            MsgId = receiptMessageId,
            BotId = storedInvoice.BotId,
            BuyerUserId = input.UserId,
            Date = DateTime.UtcNow.ToTimestamp(),
            Title = storedInvoice.Title,
            Description = storedInvoice.Description,
            Photo = storedInvoice.Photo,
            Invoice = storedInvoice.Invoice,
            Currency = storedInvoice.Currency,
            TotalAmount = storedInvoice.TotalAmount,
            TransactionId = chargeId,
            Info = requestedInfo?.ToBytes(),
            ShippingOptionId = shippingOptionId,
            CredentialsTitle = string.IsNullOrEmpty(credentialsTitle) ? null : credentialsTitle,
            InvoiceSlug = storedInvoice.Slug
        });

        // A subscription invoice starts (or extends) a bot subscription the bot can later end with
        // payments.botCancelStarsSubscription, quoting this charge.
        // See https://corefork.telegram.org/api/subscriptions#bot-subscriptions
        if (invoiceDetails.SubscriptionPeriod is > 0 and var period)
        {
            await starsSubscriptionService.RecordBotSubscriptionAsync(
                input.UserId,
                storedInvoice.BotId,
                chargeId,
                period,
                storedInvoice.TotalAmount,
                storedInvoice.Title);
        }
    }
}
