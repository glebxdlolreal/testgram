using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;
using MyTelegram.Schema;
using MyTelegram.Messenger.Services.PaidMedia;
using MyTelegram.Messenger.Services.Payments;
using MyTelegram.Messenger.Options;
using MyTelegram.Messenger.Services.Premium;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetPaymentFormHandler(
    IMongoDatabase mongoDatabase,
    IOptions<MyTelegramMessengerServerOptions> options,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IUserConverterService userConverterService,
    ILogger<GetPaymentFormHandler> logger,
    IOptions<PremiumOptions> premiumOptions,
    IPremiumPurchaseGate premiumPurchaseGate,
    IPremiumCardCheckout premiumCardCheckout,
    IPremiumBotService premiumBotService,
    IStarsPurchaseGate starsPurchaseGate)
    : RpcResultObjectHandler<RequestGetPaymentForm, IPaymentForm>
{
    protected override async Task<IPaymentForm> HandleCoreAsync(IRequestInput input, RequestGetPaymentForm obj)
    {
        logger.LogInformation("[GetPaymentForm] Invoice type: {Type}", obj.Invoice?.GetType().Name);

        if (obj.Invoice is TInputInvoiceStars starsInvoice)
        {
            logger.LogInformation("[GetPaymentForm] Stars invoice, Purpose type: {Type}", starsInvoice.Purpose?.GetType().Name);
            return await HandleStarsTopupAsync(input, starsInvoice);
        }

        if (obj.Invoice is TInputInvoiceChatInviteSubscription subscriptionInvoice)
        {
            return await HandleChatInviteSubscriptionAsync(input, subscriptionInvoice);
        }

        if (obj.Invoice is TInputInvoiceStarGift starGiftInvoice)
        {
            var collection = mongoDatabase.GetCollection<StarGiftDocument>("star-gifts");
            var gift = await collection.Find(d => d.GiftId == starGiftInvoice.GiftId).FirstOrDefaultAsync();
            if (gift == null)
                RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            return new TPaymentFormStarGift
            {
                FormId = Random.Shared.NextInt64(),
                Invoice = new TInvoice
                {
                    Currency = "XTR",
                    Prices = [new TLabeledPrice { Label = "Gift", Amount = gift.Stars }],
                },
            };
        }

        if (obj.Invoice is TInputInvoiceStarGiftUpgrade upgradeInvoice)
        {
            var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
            SavedStarGiftDocument? saved = upgradeInvoice.Stargift switch
            {
                TInputSavedStarGiftUser u => await savedCol.Find(
                    Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, input.UserId)
                    & Builders<SavedStarGiftDocument>.Filter.Eq(d => d.IsUnique, false)
                    & Builders<SavedStarGiftDocument>.Filter.Ne(d => d.UpgradeStars, null)
                    & SavedStarGiftMsgIdHelper.MatchMsgId(u.MsgId)).FirstOrDefaultAsync(),
                TInputSavedStarGiftChat c => await savedCol.Find(d =>
                    d.OwnerChannelId == ((c.Peer as TInputPeerChannel) == null ? 0 : ((TInputPeerChannel)c.Peer).ChannelId) &&
                    d.RandomId == c.SavedId &&
                    !d.IsUnique &&
                    d.UpgradeStars.HasValue).FirstOrDefaultAsync(),
                _ => null
            };
            if (saved == null || saved.IsUnique || !saved.UpgradeStars.HasValue)
                RpcErrors.RpcErrors400.StargiftUpgradeUnavailable.ThrowRpcError();

            return new TPaymentFormStarGift
            {
                FormId = Random.Shared.NextInt64(),
                Invoice = new TInvoice
                {
                    Currency = "XTR",
                    Prices = [new TLabeledPrice { Label = "Upgrade", Amount = saved!.UpgradeStars!.Value }],
                },
            };
        }

        if (obj.Invoice is TInputInvoiceStarGiftPrepaidUpgrade prepaidForm)
        {
            var savedCol2 = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
            var peer = peerHelper.GetPeer(prepaidForm.Peer, input.UserId)!;
            var saved2 = await savedCol2.Find(d =>
                d.OwnerUserId == peer.PeerId &&
                d.PrepaidUpgradeHash == prepaidForm.Hash &&
                !d.IsUnique).FirstOrDefaultAsync();
            if (saved2 == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            return new TPaymentFormStarGift
            {
                FormId = Random.Shared.NextInt64(),
                Invoice = new TInvoice
                {
                    Currency = "XTR",
                    Prices = [new TLabeledPrice { Label = "Upgrade", Amount = 0 }],
                },
            };
        }

        if (obj.Invoice is TInputInvoiceStarGiftDropOriginalDetails)
        {
            return new TPaymentFormStarGift
            {
                FormId = Random.Shared.NextInt64(),
                Invoice = new TInvoice
                {
                    Currency = "XTR",
                    Prices = [new TLabeledPrice { Label = "Remove original details", Amount = 25 }],
                },
            };
        }

        if (obj.Invoice is TInputInvoiceStarGiftAuctionBid auctionBid)
        {
            var col = mongoDatabase.GetCollection<AuctionDocument>("star-gift-auctions");
            var auction = await col.Find(x => x.GiftId == auctionBid.GiftId && !x.Finished).FirstOrDefaultAsync();
            if (auction == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
            if (auctionBid.BidAmount < auction!.MinBidAmount) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            return new TPaymentFormStarGift
            {
                FormId = Random.Shared.NextInt64(),
                Invoice = new TInvoice
                {
                    Currency = "XTR",
                    Prices = [new TLabeledPrice { Label = "Auction bid", Amount = auctionBid.BidAmount }],
                },
            };
        }

        if (obj.Invoice is TInputInvoiceStarGiftResale resaleForm)
        {
            var uniqueDoc = await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
                .Find(d => d.Slug == resaleForm.Slug).FirstOrDefaultAsync();
            if (uniqueDoc == null) RpcErrors.RpcErrors400.StargiftSlugInvalid.ThrowRpcError();

            // Layer 206: resaleForm.Ton flag selects the currency. When the gift is
            // flagged resale_ton_only we ignore the Stars branch entirely.
            var useTon = resaleForm.Ton || uniqueDoc!.ResaleTonOnly;
            if (useTon)
            {
                if (uniqueDoc!.ResellTon <= 0) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
                return new TPaymentFormStarGift
                {
                    FormId = Random.Shared.NextInt64(),
                    Invoice = new TInvoice
                    {
                        Currency = "TON",
                        Prices = [new TLabeledPrice { Label = uniqueDoc.Title, Amount = uniqueDoc.ResellTon }],
                    },
                };
            }

            if (uniqueDoc!.ResellStars <= 0) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
            return new TPaymentFormStarGift
            {
                FormId = Random.Shared.NextInt64(),
                Invoice = new TInvoice
                {
                    Currency = "XTR",
                    Prices = [new TLabeledPrice { Label = uniqueDoc.Title, Amount = uniqueDoc.ResellStars }],
                },
            };
        }

        if (obj.Invoice is TInputInvoicePremiumGiftCode giftCodeInvoice)
        {
            // Premium gift code purchase - use Stripe payment
            return await HandlePremiumGiftCodeAsync(input, giftCodeInvoice);
        }

        if (obj.Invoice is TInputInvoicePremiumGiftStars giftStarsInvoice)
        {
            return await HandlePremiumGiftStarsAsync(input, giftStarsInvoice);
        }

        // Bot invoice from message (inputInvoiceMessage)
        if (obj.Invoice is TInputInvoiceMessage invoiceMessage)
        {
            return await HandleBotInvoiceMessageAsync(input, invoiceMessage);
        }

        // Bot invoice from slug (inputInvoiceSlug)
        if (obj.Invoice is TInputInvoiceSlug invoiceSlug)
        {
            return await HandleBotInvoiceSlugAsync(input, invoiceSlug);
        }

        RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
        return default!;
    }

    /// <summary>
    /// The Stars form for gifting Premium; the price comes from the same table sendStarsForm charges.
    /// See https://corefork.telegram.org/constructor/inputInvoicePremiumGiftStars
    /// </summary>
    private async Task<IPaymentForm> HandlePremiumGiftStarsAsync(IRequestInput input, TInputInvoicePremiumGiftStars invoice)
    {
        var period = premiumOptions.Value.FindGift(invoice.Months);
        if (period == null || period.AmountStars <= 0)
        {
            RpcErrors.RpcErrors400.MonthInvalid.ThrowRpcError();
        }

        var recipientUserId = invoice.UserId switch
        {
            TInputUserSelf => input.UserId,
            TInputUser u => u.UserId,
            _ => peerHelper.GetPeer(invoice.UserId, input.UserId).PeerId
        };

        await premiumPurchaseGate.EnsureAllowedAsync(input.UserId, recipientUserId);

        return new TPaymentFormStars
        {
            FormId = Random.Shared.NextInt64(),
            BotId = MyTelegramConsts.NotificationServiceUserId,
            Title = "Telegram Premium Gift",
            Description = $"Gift {invoice.Months} months of Telegram Premium",
            Invoice = new TInvoice
            {
                Currency = "XTR",
                Prices = [new TLabeledPrice { Label = $"Telegram Premium ({invoice.Months} months)", Amount = period!.AmountStars }],
            },
            Users = new TVector<IUser>(),
        };
    }

    private async Task<TVector<IPaymentSavedCredentials>?> GetSavedCredentialsAsync(long userId)
    {
        var col = mongoDatabase.GetCollection<SavedPaymentCredentialDocument>("saved-payment-credentials");
        var cards = await col.Find(x => x.UserId == userId).ToListAsync();
        if (cards.Count == 0) return null;
        return new TVector<IPaymentSavedCredentials>(
            cards.Select(c => (IPaymentSavedCredentials)new TPaymentSavedCredentialsCard
            {
                Id = c.PaymentMethodId,
                Title = c.Title,
            }).ToList());
    }

    /// <summary>
    /// The card form for Premium bought with money: gift codes for specific users, a giveaway, or a
    /// subscription for oneself. Prices come from the server's period table, never from the
    /// <c>premiumGiftCodeOption</c> the client echoes back.
    /// See https://corefork.telegram.org/api/giveaways
    /// </summary>
    private async Task<IPaymentForm> HandlePremiumGiftCodeAsync(IRequestInput input, TInputInvoicePremiumGiftCode invoice)
    {
        var premium = premiumOptions.Value;
        var months = invoice.Option.Months;
        var period = invoice.Purpose is TInputStorePaymentPremiumSubscription
            ? premium.FindSubscription(months)
            : premium.FindGift(months);
        if (period == null || period.Amount <= 0)
        {
            RpcErrors.RpcErrors400.MonthInvalid.ThrowRpcError();
        }

        if (!string.Equals(invoice.Option.Currency, premium.Currency, StringComparison.OrdinalIgnoreCase))
        {
            RpcErrors.RpcErrors400.CurrencyTotalAmountInvalid.ThrowRpcError();
        }

        string kind;
        List<long> recipients;
        long quantity;
        string description;
        switch (invoice.Purpose)
        {
            case TInputStorePaymentPremiumGiftCode giftCode:
                recipients = giftCode.Users
                    .Select(u => u is TInputUserSelf ? input.UserId : peerHelper.GetPeer(u, input.UserId).PeerId)
                    .Distinct()
                    .ToList();
                if (recipients.Count == 0)
                {
                    RpcErrors.RpcErrors400.UsersTooFew.ThrowRpcError();
                }

                foreach (var recipient in recipients.Where(id => id != input.UserId))
                {
                    await PeerKindHelper.EnsureNotBotOrSystemAsync(queryProcessor, recipient);
                }

                kind = PremiumCardCheckout.KindGiftCode;
                quantity = recipients.Count;
                description = $"Telegram Premium Gift ({recipients.Count} users, {months} months)";
                break;

            case TInputStorePaymentPremiumGiveaway:
                kind = PremiumCardCheckout.KindGiveaway;
                recipients = [];
                quantity = Math.Max(1, invoice.Option.Users);
                description = $"Telegram Premium Giveaway ({quantity} winners, {months} months)";
                break;

            case TInputStorePaymentPremiumSubscription:
                kind = PremiumCardCheckout.KindSubscription;
                recipients = [input.UserId];
                quantity = 1;
                description = $"Telegram Premium ({months} months)";
                break;

            default:
                RpcErrors.RpcErrors400.PurposeInvalid.ThrowRpcError();
                return null!;
        }

        await premiumPurchaseGate.EnsureAllowedAsync([input.UserId, .. recipients]);

        // A subscription is the term's own price; gifts and giveaways follow the per-quantity table.
        var amount = invoice.Purpose is TInputStorePaymentPremiumSubscription
            ? period!.Amount
            : premium.GiftPrice(months, (int)quantity);
        if (amount <= 0)
        {
            RpcErrors.RpcErrors400.MonthInvalid.ThrowRpcError();
        }
        var form = await premiumCardCheckout.CreateAsync(input.UserId, amount, premium.Currency, description, intent =>
        {
            intent.PremiumKind = kind;
            intent.PremiumMonths = months;
            intent.PremiumQuantity = (int)quantity;
            intent.PremiumRecipientUserIds = recipients;
            intent.GiveawayPurpose = invoice.Purpose is TInputStorePaymentPremiumGiveaway ? invoice.Purpose.ToBytes() : null;
        });

        return new TPaymentForm
        {
            FormId = form.FormId,
            BotId = MyTelegramConsts.NotificationServiceUserId,
            Title = description,
            Description = description,
            NativeProvider = form.NativeProvider,
            NativeParams = new TDataJSON { Data = form.NativeParamsJson },
            ProviderId = 0,
            Url = string.Empty,
            Invoice = new TInvoice
            {
                Currency = premium.Currency,
                Prices = [new TLabeledPrice { Label = description, Amount = amount }],
            },
            SavedCredentials = await GetSavedCredentialsAsync(input.UserId),
            Users = new TVector<IUser>(),
        };
    }

    /// <summary>
    /// Payment form for a paid invite link, i.e. a Telegram Star subscription to a channel.
    /// See https://corefork.telegram.org/api/invites#paid-invite-links
    /// </summary>
    private async Task<IPaymentForm> HandleChatInviteSubscriptionAsync(IRequestInput input, TInputInvoiceChatInviteSubscription invoice)
    {
        if (string.IsNullOrEmpty(invoice.Hash))
        {
            RpcErrors.RpcErrors400.InviteHashEmpty.ThrowRpcError();
        }

        var chatInviteReadModel = await queryProcessor.ProcessAsync(new GetChatInviteByLinkQuery(invoice.Hash));
        if (chatInviteReadModel == null || chatInviteReadModel.Revoked)
        {
            RpcErrors.RpcErrors400.InviteHashInvalid.ThrowRpcError();
        }

        if (chatInviteReadModel!.SubscriptionPricingAmount is not > 0)
        {
            RpcErrors.RpcErrors400.PricingChatInvalid.ThrowRpcError();
        }

        var channelReadModel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(chatInviteReadModel.PeerId));
        if (channelReadModel == null)
        {
            RpcErrors.RpcErrors400.ChannelIdInvalid.ThrowRpcError();
        }

        return new TPaymentFormStars
        {
            // Form ids are not persisted: payments.sendStarsForm re-resolves the invite by hash,
            // so the id only has to be unique for the client's bookkeeping.
            FormId = Random.Shared.NextInt64(),
            BotId = input.UserId,
            Title = channelReadModel!.Title,
            Description = channelReadModel.About ?? string.Empty,
            Invoice = new TInvoice
            {
                Currency = "XTR",
                Prices = [new TLabeledPrice { Label = "Subscription", Amount = chatInviteReadModel.SubscriptionPricingAmount!.Value }]
            },
            Users = new TVector<IUser>()
        };
    }

    /// <summary>
    /// The server's price for a Stars pack. The amount the client quotes is never charged: a pack
    /// that is not on sale is refused instead of being sold at whatever price was sent.
    /// </summary>
    private static long StarsPackPrice(long stars)
    {
        var offer = StarsCatalog.FindTopup(stars);
        if (offer == null)
        {
            RpcErrors.RpcErrors400.StarsAmountInvalid.ThrowRpcError();
        }

        return offer!.Amount;
    }

    private async Task<IPaymentForm> HandleStarsTopupAsync(IRequestInput input, TInputInvoiceStars invoice)
    {
        var stripe = options.Value.Stripe;
        long amountCents = 0;
        long stars = 0;
        long recipientUserId = 0;

        // Stars off sale for this account (or, for a gift, its recipient): see IStarsPurchaseGate.
        if (invoice.Purpose is TInputStorePaymentStarsTopup or TInputStorePaymentStarsGiveaway)
        {
            await starsPurchaseGate.EnsureAllowedAsync(input.UserId);
        }

        if (invoice.Purpose is TInputStorePaymentStarsTopup topup)
        {
            stars = topup.Stars;
            amountCents = StarsPackPrice(topup.Stars);
        }
        else if (invoice.Purpose is TInputStorePaymentStarsGift gift)
        {
            stars = gift.Stars;
            amountCents = StarsPackPrice(gift.Stars);
            recipientUserId = gift.UserId is TInputUser inputUser ? inputUser.UserId : peerHelper.GetPeer(gift.UserId, input.UserId).PeerId;
            await starsPurchaseGate.EnsureAllowedAsync(input.UserId, recipientUserId);
        }
        else if (invoice.Purpose is TInputStorePaymentStarsGiveaway giveaway)
        {
            stars = giveaway.Stars;
            // The catalogue's price for the pool, never the amount the client quotes.
            var offer = StarsCatalog.FindGiveaway(giveaway.Stars);
            if (offer == null)
            {
                RpcErrors.RpcErrors400.StarsAmountInvalid.ThrowRpcError();
            }

            amountCents = offer!.Amount;
        }
        else if (invoice.Purpose is TInputStorePaymentPremiumGiveaway premiumGiveaway)
        {
            stars = 0; // Premium giveaway doesn't use Stars
            amountCents = premiumGiveaway.Amount;
        }

        string nativeParamsJson;
        long formId = Random.Shared.NextInt64();

        if (!string.IsNullOrEmpty(stripe.SecretKey))
        {
            var (clientSecret, paymentIntentId) = await StripeHelper.CreatePaymentIntentAsync(
                stripe.SecretKey, amountCents, "usd", $"Telegram Stars x{stars}");

            // Store mapping formId -> paymentIntentId for later verification // Force rebuild 407-2
            await mongoDatabase.GetCollection<StripePaymentIntentDocument>("stripe-payment-intents")
                .InsertOneAsync(new StripePaymentIntentDocument
                {
                    FormId = formId,
                    PaymentIntentId = paymentIntentId,
                    ClientSecret = clientSecret,
                    UserId = input.UserId,
                    RecipientUserId = recipientUserId,
                    Stars = stars,
                    CreatedAt = DateTime.UtcNow,
                    GiveawayPurpose = invoice.Purpose is TInputStorePaymentStarsGiveaway or TInputStorePaymentPremiumGiveaway
                        ? invoice.Purpose.ToBytes()
                        : null,
                });

            nativeParamsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                publishable_key = stripe.PublishableKey,
                payment_intent_client_secret = clientSecret,
                need_country = false,
                need_zip = false,
                need_cardholder_name = false,
            });
        }
        else
        {
            // No Stripe - store payment intent without real payment
            await mongoDatabase.GetCollection<StripePaymentIntentDocument>("stripe-payment-intents")
                .InsertOneAsync(new StripePaymentIntentDocument
                {
                    FormId = formId,
                    PaymentIntentId = $"test_{formId}",
                    ClientSecret = string.Empty,
                    UserId = input.UserId,
                    RecipientUserId = recipientUserId,
                    Stars = stars,
                    CreatedAt = DateTime.UtcNow,
                    GiveawayPurpose = invoice.Purpose is TInputStorePaymentStarsGiveaway or TInputStorePaymentPremiumGiveaway
                        ? invoice.Purpose.ToBytes()
                        : null,
                });

            nativeParamsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                publishable_key = stripe.PublishableKey,
                need_country = false,
                need_zip = false,
                need_cardholder_name = false,
            });
        }

        return new TPaymentForm
        {
            FormId = formId,
            BotId = MyTelegramConsts.NotificationServiceUserId,
            Title = $"Buy {stars} Telegram Stars",
            Description = $"Top up your Telegram Stars balance with {stars} stars",
            NativeProvider = "stripe",
            NativeParams = new TDataJSON { Data = nativeParamsJson },
            ProviderId = 0,
            Url = string.Empty,
            Invoice = new TInvoice
            {
                Currency = StarsCatalog.Currency,
                Prices = [new TLabeledPrice { Label = $"{stars} Telegram Stars", Amount = amountCents }],
            },
            SavedCredentials = await GetSavedCredentialsAsync(input.UserId),
            Users = new TVector<IUser>(),
        };
    }

    private async Task<IPaymentForm> HandleBotInvoiceMessageAsync(IRequestInput input, TInputInvoiceMessage invoiceMessage)
    {
        // Get invoice from message
        var peer = peerHelper.GetPeer(invoiceMessage.Peer, input.UserId);

        // Paid-media invoices use the message the user clicked. For forwards this
        // is the forwarded message (for example inputPeerSelf/msg_id in Saved
        // Messages), while the sellable media and revenue destination live on the
        // original channel/bot post referenced by SavedFromPeer/SavedFromMsgId.
        var paidMediaContext = await PaidMediaHelper.ResolveInvoiceAsync(mongoDatabase, peerHelper, input, invoiceMessage);
        if (paidMediaContext != null)
        {
            return new TPaymentFormStars
            {
                FormId = Random.Shared.NextInt64(),
                BotId = MyTelegramConsts.NotificationServiceUserId,
                Title = "Paid media",
                Description = $"Unlock paid media for {paidMediaContext.Document.StarsAmount} Telegram Stars",
                Invoice = new TInvoice
                {
                    Currency = "XTR",
                    Prices = [new TLabeledPrice { Label = "Paid media", Amount = paidMediaContext.Document.StarsAmount }],
                },
                Users = new TVector<IUser>(),
            };
        }

        // A bot invoice is resolved through the server side store, which carries the real price
        // breakdown and the invoice flags. It also knows how to cross the two message id spaces: the
        // buyer quotes the id of *their* copy, while the record is keyed by the bot's.
        var storedInvoice = await BotInvoiceHelper.ResolveMessageAsync(mongoDatabase, peerHelper, input, invoiceMessage);
        if (storedInvoice != null)
            return await BuildBotInvoiceFormAsync(input, storedInvoice);

        var messagesCol = mongoDatabase.GetCollection<MongoDB.Bson.BsonDocument>("eventflow-messagereadmodel");
        var filter = MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.And(
            MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("MessageId", invoiceMessage.MsgId),
            peer.PeerType == PeerType.User
                ? MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("OwnerPeerId", peer.PeerId)
                : MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("ToPeerId", peer.PeerId)
        );
        var messageDoc = await messagesCol.Find(filter).FirstOrDefaultAsync();
        if (messageDoc == null)
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();

        // Extract invoice data from message media
        if (!messageDoc.Contains("Media") || messageDoc["Media"].IsBsonNull)
            RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();

        var mediaBytes = messageDoc["Media"].AsByteArray;
        var mediaBuffer = new ReadOnlyMemory<byte>(mediaBytes);
        var media = mediaBuffer.Read<IMessageMedia>();

        if (media is TMessageMediaPaidMedia paidMedia)
        {
            var doc = await PaidMediaHelper.FindAsync(mongoDatabase, peer.PeerId, invoiceMessage.MsgId);
            if (doc == null)
                RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();

            return new TPaymentFormStars
            {
                FormId = Random.Shared.NextInt64(),
                BotId = MyTelegramConsts.NotificationServiceUserId,
                Title = "Paid media",
                Description = $"Unlock paid media for {paidMedia.StarsAmount} Telegram Stars",
                Invoice = new TInvoice
                {
                    Currency = "XTR",
                    Prices = [new TLabeledPrice { Label = "Paid media", Amount = paidMedia.StarsAmount }],
                },
                Users = new TVector<IUser>(),
            };
        }

        // The message carries invoice media but no record backs it: it predates the server side store,
        // so its payload and provider are gone and there is nothing left to charge against.
        RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
        return null!;
    }

    private async Task<IPaymentForm> HandleBotInvoiceSlugAsync(IRequestInput input, TInputInvoiceSlug invoiceSlug)
    {
        var storedInvoice = await BotInvoiceHelper.FindBySlugAsync(mongoDatabase, invoiceSlug.Slug);
        if (storedInvoice == null)
            RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();

        return await BuildBotInvoiceFormAsync(input, storedInvoice!);
    }

    private async Task<IPaymentForm> BuildBotInvoiceFormAsync(IRequestInput input, BotInvoiceDocument storedInvoice)
    {
        if (premiumBotService.IsPremiumBotInvoice(storedInvoice))
        {
            return await premiumBotService.BuildPaymentFormAsync(input, storedInvoice);
        }

        var botUser = await queryProcessor.ProcessAsync(new GetUserByIdQuery(storedInvoice.BotId));
        if (botUser == null || !botUser.Bot)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        return new TPaymentFormStars
        {
            FormId = Random.Shared.NextInt64(),
            BotId = storedInvoice.BotId,
            Title = storedInvoice.Title,
            Description = storedInvoice.Description,
            Photo = BotInvoiceHelper.ReadPhoto(storedInvoice),
            Invoice = BotInvoiceHelper.ReadInvoice(storedInvoice),
            Users = new TVector<IUser>(userConverterService.ToUser(input, botUser!, layer: input.Layer)),
        };
    }
}
