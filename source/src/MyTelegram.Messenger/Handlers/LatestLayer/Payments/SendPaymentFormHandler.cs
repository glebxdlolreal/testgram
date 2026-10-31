using MyTelegram.Schema.Payments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Bson;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Messenger.Services.PaidMedia;
using MyTelegram.Messenger.Services.Payments;
using MyTelegram.Messenger.Services.StarsSubscriptions;
using MyTelegram.Messenger.Services.Premium;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class SendPaymentFormHandler(
    IMongoDatabase mongoDatabase,
    IOptions<MyTelegramMessengerServerOptions> options,
    IMessageAppService messageAppService,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IObjectMessageSender objectMessageSender,
    IPrivacyAppService privacyAppService,
    IBotInvoicePaymentService botInvoicePaymentService,
    ILogger<SendPaymentFormHandler> logger,
    IPremiumPurchaseGate premiumPurchaseGate,
    IPremiumCardCheckout premiumCardCheckout,
    IPremiumGiftService premiumGiftService,
    IPremiumSubscriptionService premiumSubscriptionService,
    IPremiumBotService premiumBotService,
    IPremiumWelcomeNotifier premiumWelcomeNotifier,
    IStarsPurchaseGate starsPurchaseGate)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestSendPaymentForm, MyTelegram.Schema.Payments.IPaymentResult>
{
    protected override async Task<MyTelegram.Schema.Payments.IPaymentResult> HandleCoreAsync(
        IRequestInput input, MyTelegram.Schema.Payments.RequestSendPaymentForm obj)
    {
        // Bot invoice payment (inputInvoiceMessage or inputInvoiceSlug)
        if (obj.Invoice is TInputInvoiceMessage or TInputInvoiceSlug)
            return await HandleBotInvoicePaymentAsync(input, obj);

        // Auction bid (XTR payment, no Stripe)
        if (obj.Invoice is TInputInvoiceStarGiftAuctionBid auctionBid)
            return await HandleAuctionBidAsync(input, auctionBid);

        // Star Gift sending (XTR payment, no Stripe)
        if (obj.Invoice is TInputInvoiceStarGift starGiftInvoice)
            return await HandleStarGiftSendAsync(input, starGiftInvoice);

        // Premium gift via Stars is handled by SendStarsFormHandler (payments.sendStarsForm)
        // This handler only processes Stripe payments (USD/EUR)
        // if (obj.Invoice is TInputInvoicePremiumGiftStars premiumStarsInvoice)
        //     return await HandlePremiumGiftStarsAsync(input, premiumStarsInvoice);

        // Premium gift code via Stripe
        if (obj.Invoice is TInputInvoicePremiumGiftCode premiumGiftCode)
            return await HandlePremiumGiftCodeAsync(input, premiumGiftCode, obj);

        // Nothing above matched, so this is an invoice type this server cannot settle. Report it as an
        // RPC error rather than letting an internal exception reach the client.
        if (obj.Invoice is not TInputInvoiceStars)
            RpcErrors.RpcErrors400.BotInvoiceInvalid.ThrowRpcError();

        var col = mongoDatabase.GetCollection<StripePaymentIntentDocument>("stripe-payment-intents");
        var payerUserId = input.UserId;
        // Matched on the account as well as on the form id: a form id belonging to someone else is
        // their purchase, and settling it here would credit their Stars to the caller.
        var intent = await col.Find(x => x.FormId == obj.FormId && x.UserId == payerUserId).FirstOrDefaultAsync();

        logger.LogInformation("SendPaymentForm: userId={UserId} formId={FormId} intentFound={Found} recipientUserId={RecipientUserId}",
            input.UserId, obj.FormId, intent != null, intent?.RecipientUserId ?? -1);

        if (intent == null)
            RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();

        // Checked again at payment time: a block switched on after the form was issued still applies.
        if (intent!.PremiumKind == null)
        {
            await starsPurchaseGate.EnsureAllowedAsync(input.UserId, intent.RecipientUserId);
        }

        var stripe = options.Value.Stripe;
        var credentialsJson = (obj.Credentials as TInputPaymentCredentials)?.Data?.Data ?? "{}";

        // Nothing below may settle until the money is known to have moved. Both of the conditions
        // guarding the Stripe call — an unconfigured server, credentials carrying no token — leave
        // that unknown, and each used to fall straight through to the credit.
        var chargeVerified = false;

        if (!string.IsNullOrEmpty(stripe.SecretKey))
        {
            string? stripeTokenId = null;
            try { var doc = JsonDocument.Parse(credentialsJson).RootElement; if (doc.TryGetProperty("id", out var idProp)) stripeTokenId = idProp.GetString(); } catch { }

            if (!string.IsNullOrEmpty(stripeTokenId))
            {
                var (existingStatus, _, _) = await StripeHelper.GetPaymentIntentAsync(stripe.SecretKey, intent!.PaymentIntentId);
                if (existingStatus != "succeeded" && existingStatus != "requires_capture")
                {
                    var paymentMethodId = await ConfirmPaymentIntentAsync(stripe.SecretKey, intent.PaymentIntentId, stripeTokenId!);
                    var (status, _, _) = await StripeHelper.GetPaymentIntentAsync(stripe.SecretKey, intent.PaymentIntentId);
                    if (status != "succeeded" && status != "requires_capture")
                        RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();

                    // Save card for future use
                    if (paymentMethodId != null)
                    {
                        var title = await StripeHelper.GetPaymentMethodTitleAsync(stripe.SecretKey, paymentMethodId);
                        if (title != null)
                        {
                            try
                            {
                                var credCol = mongoDatabase.GetCollection<SavedPaymentCredentialDocument>("saved-payment-credentials");
                                await credCol.ReplaceOneAsync(
                                    x => x.UserId == input.UserId && x.PaymentMethodId == paymentMethodId,
                                    new SavedPaymentCredentialDocument { UserId = input.UserId, PaymentMethodId = paymentMethodId, Title = title, CreatedAt = DateTime.UtcNow },
                                    new ReplaceOptions { IsUpsert = true });
                            }
                            catch (MongoWriteException ex) when (ex.WriteError?.Code == 11000)
                            {
                                // Ignore duplicate key error - card already saved
                                logger.LogWarning("Duplicate payment credential ignored: {PaymentMethodId}", paymentMethodId);
                            }
                        }
                    }
                }

                chargeVerified = true;
            }
        }

        if (!chargeVerified)
        {
            // The intent id would repeat across stands that never talked to Stripe, so the record's
            // own id is what identifies this one purchase.
            await StoreTransactionHelper.AuthorizeUnverifiedTopupAsync(
                mongoDatabase, options.Value.Payments, "stripe", intent!.Id.ToString(), payerUserId, intent.Stars);
        }

        // Check if this is a giveaway payment
        bool isGiveaway = intent!.GiveawayPurpose != null;
        logger.LogInformation("[SendPaymentForm] isGiveaway={IsGiveaway}, GiveawayPurpose length={Length}", isGiveaway, intent.GiveawayPurpose?.Length ?? 0);

        if (isGiveaway)
        {
            logger.LogInformation("[SendPaymentForm] Launching giveaway...");
            // Direct client payment → launch giveaway immediately
            // Bot/Fragment flow would use different invoice type or come from specific bot

            await col.DeleteOneAsync(x => x.Id == intent.Id);

            var purposeBytes = intent.GiveawayPurpose!;
            var buffer = new ReadOnlyMemory<byte>(purposeBytes);
            var purpose = buffer.Read<IInputStorePaymentPurpose>();
            logger.LogInformation("[SendPaymentForm] Purpose type: {Type}", purpose?.GetType().Name);

            if (purpose is TInputStorePaymentStarsGiveaway starsGiveaway)
            {
                logger.LogInformation("[SendPaymentForm] Launching Stars giveaway");
                // Launch Stars giveaway immediately
                return await LaunchStarsGiveawayAsync(input, starsGiveaway);
            }
            else
            {
                logger.LogInformation("[SendPaymentForm] Launching Premium giveaway");
                // Launch Premium giveaway immediately
                var premiumGiveaway = (TInputStorePaymentPremiumGiveaway)purpose;
                // A Premium giveaway is paid through inputInvoicePremiumGiftCode; one arriving with a Stars
                // top-up form carries no quote, so it gets the smallest one.
                return await LaunchPremiumGiveawayAsync(input, premiumGiveaway, winners: 1, months: 1);
            }
        }

        var giftPurpose = (obj.Invoice as TInputInvoiceStars)?.Purpose as TInputStorePaymentStarsGift;
        await StarsPurchaseHelper.CreditAsync(mongoDatabase, messageAppService, input, intent!.RecipientUserId, intent.Stars,
            giftPurpose?.Currency ?? StarsCatalog.Currency, giftPurpose?.Amount ?? 0, intent.PaymentIntentId);

        await col.DeleteOneAsync(x => x.Id == intent.Id);

        return new TPaymentResult
        {
            Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 }
        };
    }

    private static readonly HttpClient Http = new();

    private async Task<IPaymentResult> HandleAuctionBidAsync(IRequestInput input, TInputInvoiceStarGiftAuctionBid auctionBid)
    {
        var auctionCol = mongoDatabase.GetCollection<AuctionDocument>("star-gift-auctions");
        var auction = await auctionCol.Find(x => x.GiftId == auctionBid.GiftId && !x.Finished).FirstOrDefaultAsync();
        if (auction == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        if (auctionBid.BidAmount < auction!.MinBidAmount) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);

        var existingBid = auction.Bids.FirstOrDefault(b => b.UserId == input.UserId && !b.Returned);
        long extraNeeded = existingBid != null ? auctionBid.BidAmount - existingBid.Amount : auctionBid.BidAmount;
        if (balance < extraNeeded) RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

        // Refund old bid if updating
        if (existingBid != null)
        {
            existingBid.Returned = true;
            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, existingBid.Amount);
        }

        // Deduct new bid
        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -auctionBid.BidAmount);
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -auctionBid.BidAmount,
            title: $"Auction bid for gift {auctionBid.GiftId}");

        auction.Bids.Add(new AuctionBid
        {
            UserId = input.UserId,
            Amount = auctionBid.BidAmount,
            Date = DateTime.UtcNow.ToTimestamp(),
            NameHidden = auctionBid.HideName,
            MessageText = (auctionBid.Message as TTextWithEntities)?.Text,
        });
        auction.Version++;

        await auctionCol.ReplaceOneAsync(x => x.Id == auction.Id, auction);

        return new TPaymentResult
        {
            Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 }
        };
    }

    private static async Task<string?> ConfirmPaymentIntentAsync(string secretKey, string paymentIntentId, string tokenId)
    {
        var paymentMethodId = await StripeHelper.CreatePaymentMethodAsync(secretKey, tokenId);
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.stripe.com/v1/payment_intents/{paymentIntentId}/confirm");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["payment_method"] = paymentMethodId });
        var response = await Http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json).RootElement;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Stripe confirm error: {doc.GetProperty("error").GetProperty("message").GetString()}");
        return paymentMethodId;
    }

    /// <summary>
    /// Rejects a gift the recipient has opted out of via
    /// <c>globalPrivacySettings.disallowed_gifts</c>.
    /// See https://corefork.telegram.org/api/privacy
    /// </summary>
    private async Task ThrowIfGiftDisallowedAsync(Peer recipientPeer, StarGiftDocument gift, long selfUserId)
    {
        if (recipientPeer.PeerType != PeerType.User || recipientPeer.PeerId == selfUserId)
        {
            return;
        }

        var gps = await queryProcessor.ProcessAsync(new GetGlobalPrivacySettingsQuery(recipientPeer.PeerId));
        if (gps == null)
        {
            return;
        }

        // A gift is "limited" when its supply is capped, "unlimited" otherwise; the premium
        // flag is carried separately by the gift itself.
        var disallowed = gift switch
        {
            { RequirePremium: true } when gps.DisallowPremiumGifts => true,
            { Limited: true } when gps.DisallowLimitedStargifts => true,
            { Limited: false } when gps.DisallowUnlimitedStargifts => true,
            _ => false
        };

        if (disallowed)
        {
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }
    }

    /// <summary>
    /// Whether the gift should be pinned to the recipient's profile immediately, per their
    /// <c>privacyKeyStarGiftsAutoSave</c> rule.
    /// </summary>
    private async Task<bool> IsStarGiftAutoSaveAllowedAsync(long selfUserId, Peer recipientPeer)
    {
        if (recipientPeer.PeerType != PeerType.User || recipientPeer.PeerId == selfUserId)
        {
            return false;
        }

        var autoSave = true;
        await privacyAppService.ApplyPrivacyAsync(selfUserId, recipientPeer.PeerId,
            _ => autoSave = false,
            [PrivacyType.StarGiftsAutoSave]);

        return autoSave;
    }

    private async Task<IPaymentResult> HandleStarGiftSendAsync(IRequestInput input, TInputInvoiceStarGift invoice)
    {
        // Get gift from MongoDB
        var giftCol = mongoDatabase.GetCollection<StarGiftDocument>("star-gifts");
        var gift = await giftCol.Find(d => d.GiftId == invoice.GiftId).FirstOrDefaultAsync();
        if (gift == null)
            throw new RpcException(new RpcError(400, "STARGIFT_NOT_FOUND"));

        // Check if gift is sold out (STARGIFT_USAGE_LIMITED)
        if (gift!.SoldOut || (gift.AvailabilityRemains.HasValue && gift.AvailabilityRemains.Value <= 0))
            throw new RpcException(new RpcError(400, "STARGIFT_USAGE_LIMITED"));

        // Check per-user limit (STARGIFT_USER_USAGE_LIMITED)
        if (gift.LimitedPerUser && gift.PerUserTotal.HasValue && gift.PerUserRemains.HasValue)
        {
            var userGiftCount = await mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts")
                .CountDocumentsAsync(d =>
                    d.FromUserId == input.UserId &&
                    d.GiftId == gift.GiftId);

            if (userGiftCount >= gift.PerUserTotal.Value)
                throw new RpcException(new RpcError(400, "STARGIFT_USER_USAGE_LIMITED"));
        }

        // Check if gift is locked (not yet available for purchase)
        var currentTime = DateTime.UtcNow.ToTimestamp();
        if (gift.LockedUntilDate.HasValue && gift.LockedUntilDate.Value > currentTime)
            throw new RpcException(new RpcError(400, "STARGIFT_INVALID"));

        // Calculate total cost
        long totalStars = gift.Stars;
        if (invoice.IncludeUpgrade && gift.UpgradeStars.HasValue)
            totalStars += gift.UpgradeStars.Value;

        // Check sender's balance
        var senderBalance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
        if (senderBalance < totalStars)
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

        // Get recipient peer
        var recipientPeer = peerHelper.GetPeer(invoice.Peer, input.UserId);
        if (recipientPeer == null)
            throw new RpcException(new RpcError(400, "STARGIFT_PEER_INVALID"));

        // globalPrivacySettings.disallowed_gifts was surfaced to clients via userFull but never
        // enforced, so a client that ignored it could still deliver an unwanted gift. Checked
        // before any stars move so a rejected gift costs nothing.
        await ThrowIfGiftDisallowedAsync(recipientPeer, gift, input.UserId);

        // Whether the gift lands on the recipient's profile right away is theirs to decide via
        // privacyKeyStarGiftsAutoSave; it used to be hardcoded to "not saved".
        var autoSaveToProfile = await IsStarGiftAutoSaveAllowedAsync(input.UserId, recipientPeer);

        // Deduct stars from sender
        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -totalStars);
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -totalStars,
            title: $"Gift: {gift.Title}", peerUserId: recipientPeer.PeerId);

        // Decrease availability if limited
        if (gift.Limited && gift.AvailabilityRemains.HasValue)
        {
            await giftCol.UpdateOneAsync(
                d => d.GiftId == gift.GiftId,
                Builders<StarGiftDocument>.Update.Inc(d => d.AvailabilityRemains, -1)
            );
        }

        // Decrease per-user availability if limited per user
        if (gift.LimitedPerUser && gift.PerUserRemains.HasValue)
        {
            await giftCol.UpdateOneAsync(
                d => d.GiftId == gift.GiftId,
                Builders<StarGiftDocument>.Update.Inc(d => d.PerUserRemains, -1)
            );
        }

        // Create SavedStarGiftDocument for recipient
        var now = DateTime.UtcNow.ToTimestamp();
        var randomId = Random.Shared.NextInt64();

        // Set 21-day transfer cooldown for new gifts (from telelakel)
        // Note: Some gifts have cooldown removed, but we apply it by default
        int? canResellAt = now + (21 * 24 * 60 * 60);

        var savedGift = new SavedStarGiftDocument
        {
            OwnerUserId = recipientPeer.PeerType == PeerType.User ? recipientPeer.PeerId : 0,
            OwnerChannelId = recipientPeer.PeerType == PeerType.Channel ? recipientPeer.PeerId : 0,
            FromUserId = input.UserId,
            MessageId = 0, // Will be set after message is created
            GiftId = gift.GiftId,
            Stars = gift.Stars,
            ConvertStars = gift.ConvertStars,
            UpgradeStars = gift.UpgradeStars,
            NameHidden = invoice.HideName,
            Saved = autoSaveToProfile,
            Date = now,
            MessageText = (invoice.Message as TTextWithEntities)?.Text,
            RandomId = randomId,
            PinnedToTop = false,
            IsUnique = false,
            PrepaidUpgrade = invoice.IncludeUpgrade,
            CanResellAt = canResellAt,
            DocumentId = gift.DocumentId,
            DocumentAccessHash = gift.DocumentAccessHash,
            FileReference = gift.FileReference,
            DocumentDate = gift.DocumentDate,
            MimeType = gift.MimeType,
            DocumentSize = gift.DocumentSize,
            DcId = gift.DcId,
        };

        var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        await savedCol.InsertOneAsync(savedGift);

        // Build TStarGift for message action
        var starGiftTl = new TStarGift
        {
            Id = gift.GiftId,
            Stars = gift.Stars,
            ConvertStars = gift.ConvertStars,
            UpgradeStars = gift.UpgradeStars,
            Limited = gift.Limited,
            SoldOut = gift.SoldOut,
            Birthday = gift.Birthday,
            RequirePremium = gift.RequirePremium,
            LimitedPerUser = gift.LimitedPerUser,
            AvailabilityRemains = gift.AvailabilityRemains,
            AvailabilityTotal = gift.AvailabilityTotal,
            FirstSaleDate = gift.FirstSaleDate,
            LastSaleDate = gift.LastSaleDate,
            ResellMinStars = gift.ResellMinStars,
            Title = gift.Title,
            PerUserTotal = gift.PerUserTotal,
            PerUserRemains = gift.PerUserRemains,
            LockedUntilDate = gift.LockedUntilDate,
            Sticker = new TDocument
            {
                Id = gift.DocumentId,
                AccessHash = gift.DocumentAccessHash,
                FileReference = gift.FileReference,
                Date = gift.DocumentDate,
                MimeType = gift.MimeType,
                Size = gift.DocumentSize,
                DcId = gift.DcId,
                Attributes = [new TDocumentAttributeSticker { Alt = "🎁", Stickerset = new TInputStickerSetEmpty() }],
            },
        };

        // Create service message with messageActionStarGift
        var messageAction = new TMessageActionStarGift
        {
            NameHidden = invoice.HideName,
            Saved = false,
            CanUpgrade = gift.UpgradeStars.HasValue && !invoice.IncludeUpgrade,
            PrepaidUpgrade = invoice.IncludeUpgrade,
            Gift = starGiftTl,
            Message = invoice.Message,
            ConvertStars = gift.ConvertStars,
            // Android interprets upgrade_stars as an already-paid upgrade amount,
            // not as the regular upgrade price. Keep it unset unless prepaid.
            UpgradeStars = invoice.IncludeUpgrade ? gift.UpgradeStars : null,
            FromId = invoice.HideName ? null : new TPeerUser { UserId = input.UserId },
            // Set Peer and SavedId for channel gifts (both use flags.12)
            Peer = recipientPeer.PeerType == PeerType.Channel
                ? new TPeerChannel { ChannelId = recipientPeer.PeerId }
                : null,
            SavedId = recipientPeer.PeerType == PeerType.Channel
                ? savedGift.RandomId
                : null,
        };

        await messageAppService.SendMessageAsync([new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            recipientPeer,
            string.Empty,
            randomId,
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: messageAction
        )]);

        logger.LogInformation("Star gift sent: giftId={GiftId} from={From} to={To} stars={Stars} prepaidUpgrade={PrepaidUpgrade}",
            gift.GiftId, input.UserId, recipientPeer.PeerId, totalStars, invoice.IncludeUpgrade);

        return new TPaymentResult
        {
            Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = now, Seq = 0 }
        };
    }

    // Removed: HandlePremiumGiftStarsAsync - now handled by SendStarsFormHandler

    private async Task<IPaymentResult> LaunchStarsGiveawayAsync(IRequestInput input, TInputStorePaymentStarsGiveaway giveaway)
    {
        // Validate peer
        var targetPeer = peerHelper.GetPeer(giveaway.BoostPeer, input.UserId);
        if (targetPeer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        // Extract giveaway data
        var winners = giveaway.Users;
        var untilDate = giveaway.UntilDate;
        var onlyNewSubscribers = giveaway.OnlyNewSubscribers;
        var winnersAreVisible = giveaway.WinnersAreVisible;
        var stars = giveaway.Stars;
        var prizeDescription = giveaway.PrizeDescription;

        var additionalChannels = new List<long>();
        if (giveaway.AdditionalPeers != null)
        {
            foreach (var peer in giveaway.AdditionalPeers)
            {
                var p = peerHelper.GetPeer(peer, input.UserId);
                if (p.PeerType == PeerType.Channel)
                    additionalChannels.Add(p.PeerId);
            }
        }

        var countries = giveaway.CountriesIso2?.ToList() ?? new List<string>();

        // Create messageMediaGiveaway
        var media = new TMessageMediaGiveaway
        {
            OnlyNewSubscribers = onlyNewSubscribers,
            WinnersAreVisible = winnersAreVisible,
            Channels = new TVector<long> { targetPeer.PeerId },
            CountriesIso2 = countries.Count > 0 ? new TVector<string>(countries) : null,
            PrizeDescription = prizeDescription,
            UntilDate = untilDate,
            Quantity = winners,
            Stars = stars
        };

        // Add additional channels if any
        if (additionalChannels.Count > 0)
        {
            media.Channels.AddRange(additionalChannels);
        }

        // Send giveaway message to channel
        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            targetPeer,
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.Media,
            messageType: MessageType.Text,
            media: media
        );

        await messageAppService.SendMessageAsync([sendInput]);

        // Create giveaway document in MongoDB
        var giveawayCol = mongoDatabase.GetCollection<BsonDocument>("giveaways");
        var giveawayId = $"giveaway-{targetPeer.PeerId}-{giveaway.RandomId}";

        await giveawayCol.InsertOneAsync(new BsonDocument
        {
            ["_id"] = giveawayId,
            ["ChannelId"] = targetPeer.PeerId,
            ["RandomId"] = giveaway.RandomId,
            ["MsgId"] = 0, // Will be updated when message is created
            ["Type"] = "stars",
            ["Winners"] = winners,
            ["Stars"] = stars,
            ["UntilDate"] = untilDate,
            ["OnlyNewSubscribers"] = onlyNewSubscribers,
            ["WinnersAreVisible"] = winnersAreVisible,
            ["PrizeDescription"] = prizeDescription ?? "",
            ["Countries"] = new BsonArray(countries),
            ["AdditionalChannels"] = new BsonArray(additionalChannels),
            ["Status"] = "active",
            ["StartDate"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["CreatedBy"] = input.UserId
        });

        logger.LogInformation("Stars giveaway launched: stars={Stars} winners={Winners} channel={Channel}",
            stars, winners, targetPeer.PeerId);

        // Return empty Updates (message will arrive via push)
        return new TPaymentResult
        {
            Updates = new TUpdates
            {
                Updates = new TVector<IUpdate>(),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Seq = 0
            }
        };
    }

    private async Task<IPaymentResult> LaunchPremiumGiveawayAsync(IRequestInput input, TInputStorePaymentPremiumGiveaway giveaway,
        int winners, int months)
    {
        await premiumPurchaseGate.EnsureAllowedAsync(input.UserId);

        logger.LogInformation("[LaunchPremiumGiveaway] START - UserId={UserId}", input.UserId);
        logger.LogInformation("[LaunchPremiumGiveaway] BoostPeer type: {Type}", giveaway.BoostPeer?.GetType().Name);

        // Validate peer
        var targetPeer = peerHelper.GetPeer(giveaway.BoostPeer, input.UserId);
        logger.LogInformation("[LaunchPremiumGiveaway] Target peer: {PeerType} {PeerId}", targetPeer.PeerType, targetPeer.PeerId);

        if (targetPeer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        // Winners and months are not part of TInputStorePaymentPremiumGiveaway; they come from the
        // premiumGiftCodeOption the payment form was issued for.
        var untilDate = giveaway.UntilDate;
        var onlyNewSubscribers = giveaway.OnlyNewSubscribers;
        var winnersAreVisible = giveaway.WinnersAreVisible;
        var prizeDescription = giveaway.PrizeDescription;

        var additionalChannels = new List<long>();
        if (giveaway.AdditionalPeers != null)
        {
            foreach (var peer in giveaway.AdditionalPeers)
            {
                var p = peerHelper.GetPeer(peer, input.UserId);
                if (p.PeerType == PeerType.Channel)
                    additionalChannels.Add(p.PeerId);
            }
        }

        var countries = giveaway.CountriesIso2?.ToList() ?? new List<string>();

        logger.LogInformation("[LaunchPremiumGiveaway] Creating media: winners={Winners} months={Months} untilDate={UntilDate}", winners, months, untilDate);

        // Create messageMediaGiveaway
        var media = new TMessageMediaGiveaway
        {
            OnlyNewSubscribers = onlyNewSubscribers,
            WinnersAreVisible = winnersAreVisible,
            Channels = new TVector<long> { targetPeer.PeerId },
            CountriesIso2 = countries.Count > 0 ? new TVector<string>(countries) : null,
            PrizeDescription = prizeDescription,
            UntilDate = untilDate,
            Quantity = winners,
            Months = months
        };

        // Add additional channels if any
        if (additionalChannels.Count > 0)
        {
            media.Channels.AddRange(additionalChannels);
        }

        logger.LogInformation("[LaunchPremiumGiveaway] Sending message to channel {ChannelId}", targetPeer.PeerId);

        // Generate RandomId for message
        var randomId = Random.Shared.NextInt64();

        // Send giveaway message to channel
        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            targetPeer,
            string.Empty,
            randomId,
            sendMessageType: SendMessageType.Media,
            messageType: MessageType.Text,
            media: media
        );

        await messageAppService.SendMessageAsync([sendInput]);

        logger.LogInformation("[LaunchPremiumGiveaway] Message sent successfully");

        // Save giveaway to MongoDB for GetGiveawayInfoHandler
        // Note: We use RandomId as temporary identifier since SendMessageAsync doesn't return MessageId
        // The actual MessageId will be assigned by event sourcing
        var giveawayCol = mongoDatabase.GetCollection<BsonDocument>("giveaways");
        await giveawayCol.InsertOneAsync(new BsonDocument
        {
            ["_id"] = $"giveaway-{targetPeer.PeerId}-{randomId}",
            ["ChannelId"] = targetPeer.PeerId,
            ["RandomId"] = randomId,
            ["MsgId"] = 0, // Will be updated by domain event handler
            ["Type"] = "premium",
            ["Winners"] = winners,
            ["Months"] = months,
            ["UntilDate"] = untilDate,
            ["OnlyNewSubscribers"] = onlyNewSubscribers,
            ["WinnersAreVisible"] = winnersAreVisible,
            ["PrizeDescription"] = prizeDescription ?? "",
            ["Countries"] = new BsonArray(countries),
            ["AdditionalChannels"] = new BsonArray(additionalChannels),
            ["Status"] = "active",
            ["StartDate"] = DateTime.UtcNow.ToTimestamp(),
            ["CreatedBy"] = input.UserId
        });

        logger.LogInformation("Premium giveaway launched: months={Months} winners={Winners} channel={Channel}",
            months, winners, targetPeer.PeerId);

        // Return empty Updates (message will arrive via push)
        return new TPaymentResult
        {
            Updates = new TUpdates
            {
                Updates = new TVector<IUpdate>(),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Seq = 0
            }
        };
    }

    /// <summary>
    /// Settles a card payment for Premium issued by getPaymentForm: gift codes for named users, a
    /// giveaway, or a subscription for oneself. What is granted is what the form quoted and stored
    /// with the intent, not what the client restates in <paramref name="invoice"/>.
    /// </summary>
    private async Task<IPaymentResult> HandlePremiumGiftCodeAsync(IRequestInput input, TInputInvoicePremiumGiftCode invoice, RequestSendPaymentForm obj)
    {
        var intent = await premiumCardCheckout.FindAsync(obj.FormId, input.UserId);
        if (intent.PremiumKind == null || intent.PremiumMonths <= 0)
        {
            RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
        }

        var recipients = intent.PremiumRecipientUserIds ?? [];

        // Re-checked at settlement: the switch may have been thrown after the form was issued.
        await premiumPurchaseGate.EnsureAllowedAsync([input.UserId, .. recipients]);

        await premiumCardCheckout.ChargeAsync(intent, obj.Credentials);

        var currency = intent.Currency ?? invoice.Option.Currency;
        switch (intent.PremiumKind)
        {
            case PremiumCardCheckout.KindGiftCode:
                var amountPerUser = recipients.Count == 0 ? intent.Amount : intent.Amount / recipients.Count;
                foreach (var recipientUserId in recipients)
                {
                    await premiumGiftService.DeliverAsync(input, recipientUserId, intent.PremiumMonths, currency, amountPerUser,
                        message: null, source: "gift-card");
                }

                logger.LogInformation("Premium gift purchased: from={From} users={Count} months={Months}",
                    input.UserId, recipients.Count, intent.PremiumMonths);
                break;

            case PremiumCardCheckout.KindSubscription:
                var granted = await premiumSubscriptionService.GrantAsync(input.UserId, PremiumDuration.DaysFromMonths(intent.PremiumMonths),
                    "card", input.UserId);
                if (granted.BecamePremium)
                {
                    await premiumWelcomeNotifier.NotifyAsync(input.UserId);
                }

                break;

            case PremiumCardCheckout.KindGiveaway when intent.GiveawayPurpose != null:
                var purposeBuffer = new ReadOnlyMemory<byte>(intent.GiveawayPurpose);
                var purpose = purposeBuffer.Read<IInputStorePaymentPurpose>();
                if (purpose is TInputStorePaymentPremiumGiveaway premiumGiveaway)
                {
                    return await LaunchPremiumGiveawayAsync(input, premiumGiveaway, Math.Max(1, intent.PremiumQuantity), intent.PremiumMonths);
                }

                RpcErrors.RpcErrors400.PurposeInvalid.ThrowRpcError();
                break;

            default:
                RpcErrors.RpcErrors400.PurposeInvalid.ThrowRpcError();
                break;
        }

        return new TPaymentResult
        {
            Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 }
        };
    }

    private async Task<bool> IsPaidMediaInvoiceAsync(IRequestInput input, TInputInvoiceMessage invoiceMessage)
    {
        return await PaidMediaHelper.ResolveInvoiceAsync(mongoDatabase, peerHelper, input, invoiceMessage) != null;
    }

    private async Task<IPaymentResult> HandlePaidMediaPaymentAsync(IRequestInput input, TInputInvoiceMessage invoiceMessage)
    {
        var context = await PaidMediaHelper.ResolveInvoiceAsync(mongoDatabase, peerHelper, input, invoiceMessage);
        if (context == null)
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();

        var doc = context.Document;
        var revealed = PaidMediaHelper.ToRevealedExtendedMedia(doc.ExtendedMedia);
        if (!PaidMediaHelper.IsPurchasedBy(doc, input.UserId))
        {
            var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
            if (balance < doc.StarsAmount)
                RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -doc.StarsAmount);
            await PaidMediaHelper.MarkPurchasedAsync(mongoDatabase, doc, input.UserId);

            var txMedia = PaidMediaHelper.ToTransactionExtendedMedia(doc.ExtendedMedia);
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -doc.StarsAmount,
                peerChannelId: doc.ChannelId, title: "Paid media", msgId: doc.MsgId, extendedMedia: txMedia);

            var sender = await queryProcessor.ProcessAsync(new GetUserByIdQuery(doc.SenderUserId));
            if (sender?.Bot == true)
            {
                await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, doc.SenderUserId, doc.StarsAmount);
                await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, doc.SenderUserId, doc.StarsAmount,
                    peerChannelId: doc.ChannelId, title: "Paid media", msgId: doc.MsgId, extendedMedia: txMedia);
                await BalancePushHelper.PushStarsBalanceAsync(objectMessageSender, mongoDatabase, doc.SenderUserId);
            }
            else
            {
                await ChannelRevenueHelper.CreditAsync(mongoDatabase, doc.ChannelId, ChannelRevenueHelper.StarsCurrency,
                    doc.StarsAmount, sourceUserId: input.UserId, title: "Paid media");

                var channel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(doc.ChannelId));
                if (channel != null)
                {
                    await BalancePushHelper.PushChannelRevenueStatusAsync(objectMessageSender, mongoDatabase,
                        doc.ChannelId, PeerType.Channel, channel.CreatorId, ChannelRevenueHelper.StarsCurrency);
                }
            }
            await BalancePushHelper.PushStarsBalanceAsync(objectMessageSender, mongoDatabase, input.UserId);

            var update = new TUpdateMessageExtendedMedia
            {
                Peer = context.DisplayPeer.ToPeer(),
                MsgId = context.DisplayMsgId,
                ExtendedMedia = revealed
            };
            var updates = new TUpdates
            {
                Updates = new TVector<IUpdate> { update },
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = DateTime.UtcNow.ToTimestamp(),
                Seq = 0
            };
            await objectMessageSender.PushMessageToPeerAsync(input.UserId.ToUserPeer(), updates, pts: 0);

            if (!string.IsNullOrEmpty(doc.Payload))
            {
                if (sender?.Bot == true)
                {
                    var qts = await PaidMediaHelper.NextQtsAsync(mongoDatabase, doc.SenderUserId);
                    await objectMessageSender.PushMessageToPeerAsync(doc.SenderUserId.ToUserPeer(), new TUpdates
                    {
                        Updates = new TVector<IUpdate>
                        {
                            new TUpdateBotPurchasedPaidMedia { UserId = input.UserId, Payload = doc.Payload!, Qts = qts }
                        },
                        Users = new TVector<IUser>(),
                        Chats = new TVector<IChat>(),
                        Date = DateTime.UtcNow.ToTimestamp(),
                        Seq = 0
                    }, pts: 0);
                }
            }
        }

        return new TPaymentResult
        {
            Updates = new TUpdates
            {
                Updates = new TVector<IUpdate>
                {
                    new TUpdateMessageExtendedMedia
                    {
                        Peer = context.DisplayPeer.ToPeer(),
                        MsgId = context.DisplayMsgId,
                        ExtendedMedia = revealed
                    }
                },
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = DateTime.UtcNow.ToTimestamp(),
                Seq = 0
            }
        };
    }

    private async Task<IPaymentResult> HandleBotInvoicePaymentAsync(IRequestInput input, RequestSendPaymentForm obj)
    {
        // @PremiumBot's invoices are charged to a card rather than to a Stars balance.
        var storedInvoice = await BotInvoiceHelper.ResolveAsync(mongoDatabase, peerHelper, input, obj.Invoice);
        if (storedInvoice != null && premiumBotService.IsPremiumBotInvoice(storedInvoice))
        {
            return await premiumBotService.PayAsync(input, storedInvoice, obj);
        }

        return await botInvoicePaymentService.PayAsync(input, obj.Invoice, obj.RequestedInfoId, obj.ShippingOptionId);
    }
}
