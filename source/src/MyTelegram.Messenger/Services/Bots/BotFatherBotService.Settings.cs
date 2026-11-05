using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// Botpic, description picture, group privacy, default admin rights, linked domain and ownership
/// transfer. Prompts and screens follow real BotFather (measured 2026-11-02).
/// </summary>
public partial class BotFatherBotService
{
    // --- Media inputs ---

    private async Task HandleBotMediaAsync(IRequestInput input, long fromUserId, string flow, string username,
        IMessageMedia media)
    {
        var bot = await GetBotAsync(fromUserId, username);
        if (bot == null)
        {
            await ClearStateAsync(fromUserId);
            await SendAsync(input, fromUserId, InvalidBotText);
            return;
        }

        switch (flow)
        {
            case "setuserpic":
                if (media is not TMessageMediaPhoto { Photo: TPhoto photo })
                {
                    await SendAsync(input, fromUserId, "Please send a photo.");
                    return;
                }

                // The photo the owner sent is a stored photo already; it becomes the bot's profile photo
                // the way photos.uploadProfilePhoto would make it the account's.
                await UpdateBotUserAsync(BotUserIdOf(bot), Builders<BsonDocument>.Update
                    .Set("ProfilePhotoId", photo.Id)
                    .Set("ProfilePhotoUpdateDate", DateTime.UtcNow.ToTimestamp()));
                await ClearStateAsync(fromUserId);
                await SendAsync(input, fromUserId, "Success! Profile photo updated. /help");
                return;

            case "setdescriptionpic":
                if (media is TMessageMediaPhoto && !BotAppMediaValidator.IsValidPreviewPhoto(media))
                {
                    await SendAsync(input, fromUserId, "Sorry, the image dimensions are invalid. Must be 640x360.");
                    return;
                }

                if (media is TMessageMediaDocument && !BotAppMediaValidator.IsValidDescriptionAnimation(media))
                {
                    await SendAsync(input, fromUserId,
                        "Sorry, the GIF dimensions are invalid. Must be 320x180, 640x360 or 960x540.");
                    return;
                }

                if (media is not (TMessageMediaPhoto or TMessageMediaDocument))
                {
                    await SendAsync(input, fromUserId, DescriptionPicPrompt);
                    return;
                }

                // Kept as the messageMedia it was sent as; getFullUser serves the photo or the document.
                await UpdateBotInfoAsync(BotUserIdOf(bot),
                    Builders<BsonDocument>.Update.Set("DescriptionMedia", media.ToBytes()));
                await ClearStateAsync(fromUserId);
                await SendAsync(input, fromUserId, "Success! Description picture updated. /help");
                return;

            case "settransferrecepient":
                if (media is not TMessageMediaContact contact)
                {
                    await SendAsync(input, fromUserId, "Please share the new owner's contact or their username.");
                    return;
                }

                await HandleTransferRecipientAsync(input, fromUserId, bot, string.Empty, contact.UserId);
                return;
        }
    }

    // --- Group privacy ---

    /// <summary>
    /// <c>BotPrivacyMode</c> on the user read model; BotGroupPrivacyFilter applies it when group messages
    /// are delivered. Bots made before the setting existed have it off, which is how they always worked.
    /// </summary>
    private async Task<bool> IsPrivacyModeAsync(BsonDocument bot)
    {
        var user = await GetBotUserAsync(BotUserIdOf(bot));
        return user != null && user.TryGetValue("BotPrivacyMode", out var value) && value.IsBoolean && value.AsBoolean;
    }

    private async Task SetPrivacyModeAsync(BsonDocument bot, bool enabled)
        => await UpdateBotUserAsync(BotUserIdOf(bot), Builders<BsonDocument>.Update.Set("BotPrivacyMode", enabled));

    private async Task SendPrivacyMenuAsync(IRequestInput input, long fromUserId, int msgId, BsonDocument bot)
    {
        var username = BotUsername(bot);
        var enabled = await IsPrivacyModeAsync(bot);
        await EditMessageAsync(input, fromUserId, msgId,
            new BotText().Link("Privacy mode", "https://core.telegram.org/bots/features#privacy-mode")
                .Append(" is ").Bold(enabled ? "enabled" : "disabled").Append($" for {BotName(bot)} @{username}."),
            BackToSettings(username, enabled
                ? Btn("Turn off", $"privacy_off:{username}")
                : Btn("Turn on", $"privacy_on:{username}")));
    }

    private async Task HandlePrivacyInputAsync(IRequestInput input, long fromUserId, BsonDocument bot, string text)
    {
        bool enabled;
        if (text.Equals("Enable", StringComparison.OrdinalIgnoreCase))
        {
            enabled = true;
        }
        else if (text.Equals("Disable", StringComparison.OrdinalIgnoreCase))
        {
            enabled = false;
        }
        else
        {
            await SendAsync(input, fromUserId, "Please choose 'Enable' or 'Disable'.", ReplyKeyboard([["Enable", "Disable"]]));
            return;
        }

        await SetPrivacyModeAsync(bot, enabled);
        await ClearStateAsync(fromUserId);
        await SendAsync(input, fromUserId, $"Success! The new status is: {(enabled ? "ENABLED" : "DISABLED")}. /help");
    }

    // --- Default admin rights ---

    private async Task SendAdminRightsMenuAsync(IRequestInput input, long fromUserId, int msgId, BsonDocument bot,
        string kind)
    {
        var username = BotUsername(bot);
        var broadcast = kind == "b";
        var choices = broadcast ? BotDefaultAdminRights.BroadcastChoices : BotDefaultAdminRights.GroupChoices;
        var rights = await BotDefaultAdminRights.GetAsync(mongoDatabase, BotUserIdOf(bot),
            broadcast ? BotDefaultAdminRights.Broadcast : BotDefaultAdminRights.Group);
        var current = rights == null ? new BsonDocument() : BotDefaultAdminRights.ToDocument(rights);

        var buttons = choices.Select((choice, index) =>
        {
            var on = current.TryGetValue(choice.Field, out var value) && value.IsBoolean && value.AsBoolean;
            return Btn(on ? $"✅ {choice.Label}" : choice.Label, $"rights_toggle:{username}:{kind}:{index}");
        });

        var rows = buttons.Chunk(2).Select(InlineRow)
            .Append(InlineRow(Btn("« Back to Settings", $"bot_settings:{username}")))
            .ToArray();

        var place = broadcast ? "channel" : "group";
        var managed = broadcast ? "channels" : "groups";
        await EditMessageAsync(input, fromUserId, msgId,
            $"You can choose which rights the bot {BotName(bot)} @{username} will request by default when added as a " +
            $"{place} admin.\n\nIf the bot doesn't support managing {managed}, please remove all checks.",
            InlineRows(rows));
    }

    private async Task ToggleAdminRightAsync(BsonDocument bot, string kind, int choice)
    {
        var broadcast = kind == "b";
        var choices = broadcast ? BotDefaultAdminRights.BroadcastChoices : BotDefaultAdminRights.GroupChoices;
        if (choice < 0 || choice >= choices.Length)
        {
            return;
        }

        var botUserId = BotUserIdOf(bot);
        var type = broadcast ? BotDefaultAdminRights.Broadcast : BotDefaultAdminRights.Group;
        var document = BotDefaultAdminRights.ToDocument(
            await BotDefaultAdminRights.GetAsync(mongoDatabase, botUserId, type) ?? new TChatAdminRights());
        var field = choices[choice].Field;
        document[field] = !document[field].AsBoolean;

        await BotDefaultAdminRights.SetAsync(mongoDatabase, botUserId, type, BotDefaultAdminRights.FromDocument(document));
        await BumpBotInfoVersionAsync(botUserId);
    }

    // --- Domain ---

    private static string? GetDomain(BsonDocument bot) =>
        bot.TryGetValue("Domain", out var value) && value.IsString && value.AsString.Length > 0 ? value.AsString : null;

    private async Task SendDomainMenuAsync(IRequestInput input, long fromUserId, int msgId, BsonDocument bot)
    {
        var username = BotUsername(bot);
        var domain = GetDomain(await GetBotAsync(fromUserId, username) ?? bot);

        if (domain == null)
        {
            await EditMessageAsync(input, fromUserId, msgId,
                $"Web login is currently unavailable for {BotName(bot)} @{username}.",
                BackToSettings(username, Btn("Set domain", $"domain_set:{username}")));
            return;
        }

        await EditMessageAsync(input, fromUserId, msgId,
            new BotText($"Web login is enabled for {BotName(bot)} @{username}.\n\n").Bold("Domain").Append($": {domain}"),
            BackToSettings(username,
                Btn("Change domain", $"domain_set:{username}"),
                Btn("Remove domain", $"domain_remove:{username}")));
    }

    private async Task SetDomainAsync(BsonDocument bot, string? domain)
    {
        await mongoDatabase.GetCollection<BsonDocument>(BotCollection).UpdateOneAsync(
            new BsonDocument("BotUserId", BotUserIdOf(bot)),
            domain == null
                ? new BsonDocument("$unset", new BsonDocument("Domain", ""))
                : new BsonDocument("$set", new BsonDocument("Domain", domain)));
    }

    private async Task HandleDomainInputAsync(IRequestInput input, long fromUserId, BsonDocument bot, string text)
    {
        if (IsEmpty(text))
        {
            await SetDomainAsync(bot, null);
            await ClearStateAsync(fromUserId);
            await SendAsync(input, fromUserId, "Success! Domain removed. /help");
            return;
        }

        var domain = NormalizeDomain(text);
        if (domain == null)
        {
            await SendAsync(input, fromUserId, "Sorry, the domain is invalid. Please send me a domain like example.com.");
            return;
        }

        await SetDomainAsync(bot, domain);
        await ClearStateAsync(fromUserId);
        await SendAsync(input, fromUserId, "Success! Domain updated. /help");
    }

    /// <summary>The host of "example.com" or "https://example.com/path", or null when it is not a domain name.</summary>
    internal static string? NormalizeDomain(string text)
    {
        var value = text.Trim();
        var host = Uri.TryCreate(value.Contains("://") ? value : "https://" + value, UriKind.Absolute, out var uri)
            ? uri.Host.ToLowerInvariant()
            : null;

        return host != null && DomainRegex().IsMatch(host) ? host : null;
    }

    [GeneratedRegex(@"^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,61}[a-z0-9]$")]
    private static partial Regex DomainRegex();

    // --- Paid broadcast ---

    private const string PaidBroadcastFaqUrl =
        "https://core.telegram.org/bots/faq#how-can-i-message-all-of-my-bot-39s-subscribers-at-once";

    /// <summary>The text explaining why paid broadcasts cannot be turned on, or null when they can.</summary>
    private async Task<string?> GetPaidBroadcastProblemAsync(BsonDocument bot)
    {
        var botUserId = BotUserIdOf(bot);
        var mau = await botBroadcastLimiter.CountMonthlyActiveUsersAsync(botUserId);
        if (mau < BotBroadcastLimiter.MinMonthlyActiveUsers)
        {
            return "Paid Broadcast cannot be enabled because the MAU is too low.\n" +
                   $"Current MAU is {mau}.\nMinimum MAU is {BotBroadcastLimiter.MinMonthlyActiveUsers}.";
        }

        var balance = await MyTelegram.Messenger.Handlers.LatestLayer.Payments.StarsBalanceHelper.GetBalanceAsync(
            mongoDatabase, botUserId);
        if (balance < BotBroadcastLimiter.MinStarsBalance)
        {
            return "Paid Broadcast cannot be enabled because the Stars balance is too low.\n" +
                   $"Current balance is {balance} Stars.\nMinimum balance is {BotBroadcastLimiter.MinStarsBalance} Stars.";
        }

        return null;
    }

    private async Task SendPaidBroadcastMenuAsync(IRequestInput input, long fromUserId, int msgId, BsonDocument bot)
    {
        var username = BotUsername(bot);
        var botUserId = BotUserIdOf(bot);
        var enabled = await botBroadcastLimiter.IsPaidBroadcastEnabledAsync(botUserId);

        // Turning it off is always possible; turning it on needs the MAU and the balance.
        var problem = enabled ? null : await GetPaidBroadcastProblemAsync(bot);
        if (problem != null)
        {
            await EditMessageAsync(input, fromUserId, msgId,
                new BotText(problem + "\n\n").Link("Learn more about Paid Broadcasts", PaidBroadcastFaqUrl),
                BackToSettings(username, Btn("Refresh", $"paid_bcast:{username}")));
            return;
        }

        await EditMessageAsync(input, fromUserId, msgId,
            new BotText("Paid Broadcast is currently ").Bold(enabled ? "enabled" : "disabled")
                .Append($" for {BotName(bot)} @{username}.\n\n" +
                        $"With it, the bot can send up to {BotBroadcastLimiter.PaidMessagesPerSecond} messages per second " +
                        "when it passes allow_paid_broadcast. Each message over the free " +
                        $"{BotBroadcastLimiter.FreeMessagesPerSecond} per second costs 0.1 Stars from the bot's balance.\n\n")
                .Link("Learn more about Paid Broadcasts", PaidBroadcastFaqUrl),
            BackToSettings(username, enabled
                ? Btn("Turn off", $"paid_bcast_off:{username}")
                : Btn("Turn on", $"paid_bcast_on:{username}")));
    }

    // --- Ownership transfer ---

    /// <summary>
    /// Checks a recipient picked by username or shared contact, then asks for the confirmation that
    /// requires the owner's 2FA password. "You can only transfer a bot to users who have interacted with
    /// it at least once." (https://core.telegram.org/bots/features#transfer-ownership)
    /// </summary>
    private async Task HandleTransferRecipientAsync(IRequestInput input, long fromUserId, BsonDocument bot, string text,
        long? contactUserId)
    {
        var recipientId = contactUserId ?? await ResolveUserNameAsync(text);
        var problem = recipientId is > 0
            ? await GetTransferProblemAsync(fromUserId, bot, recipientId.Value)
            : "Sorry, I couldn't find this user. Please share their contact or send their username.";
        if (problem != null)
        {
            await SendAsync(input, fromUserId, problem);
            return;
        }

        var recipient = await GetBotUserAsync(recipientId!.Value);
        var username = BotUsername(bot);
        await ClearStateAsync(fromUserId);
        await SendAsync(input, fromUserId,
            new BotText($"You are about to transfer @{username} to {DisplayName(recipient)}.\n\n" +
                        "They will get full control of the bot: they will be able to access the bot's messages and even " +
                        "delete it. The transfer is permanent, so please consider it carefully."),
            InlineRows(
                InlineRow(new TKeyboardButtonCallback
                {
                    Text = TransferConfirmation,
                    Data = $"transfer_do:{username}:{recipientId}",
                    RequiresPassword = true
                }),
                InlineRow(Btn("Nope, nevermind", $"bot_select:{username}"))));
    }

    /// <summary>Why <paramref name="recipientId"/> cannot take the bot, or null when they can.</summary>
    private async Task<string?> GetTransferProblemAsync(long ownerId, BsonDocument bot, long recipientId)
    {
        var recipient = await GetBotUserAsync(recipientId);
        if (recipient == null || GetBool(recipient, "Bot") || GetBool(recipient, "IsDeleted"))
        {
            return "Sorry, I couldn't find this user. Please share their contact or send their username.";
        }

        if (recipientId == ownerId)
        {
            return "You already own this bot. Please choose another user.";
        }

        var interacted = await mongoDatabase.GetCollection<BsonDocument>("eventflow-dialogreadmodel")
            .Find(Builders<BsonDocument>.Filter.Eq("OwnerId", recipientId) &
                  Builders<BsonDocument>.Filter.Eq("ToPeerId", BotUserIdOf(bot)))
            .AnyAsync();
        if (!interacted)
        {
            return "Sorry, you can only transfer a bot to a user who has interacted with it at least once.";
        }

        if (await IsAtBotLimitAsync(recipientId) != null)
        {
            return "Sorry, this user already has the maximum number of bots.";
        }

        return null;
    }

    public async Task CheckCallbackPasswordAsync(IRequestInput input, string data, IInputCheckPasswordSRP? password)
    {
        if (!data.StartsWith("transfer_do:", StringComparison.Ordinal))
        {
            return;
        }

        var stored = await twoFactorService.GetPasswordAsync(input.UserId);
        if (stored == null)
        {
            RpcErrors.RpcErrors400.PasswordMissing.ThrowRpcError();
            return;
        }

        if (stored.PasswordUpdatedAt is { } updatedAt && DateTime.UtcNow - updatedAt < TransferPasswordAge)
        {
            var secondsLeft = (int)Math.Ceiling((updatedAt + TransferPasswordAge - DateTime.UtcNow).TotalSeconds);
            RpcErrors.RpcErrors400.PasswordTooFreshX.ThrowRpcError(secondsLeft);
        }

        if (password is not TInputCheckPasswordSRP srp ||
            await twoFactorService.GetUserIdBySrpIdAsync(srp.SrpId) != input.UserId ||
            !await twoFactorService.VerifySrpAsync(input.UserId, srp.SrpId, srp.A.ToArray(), srp.M1.ToArray()))
        {
            RpcErrors.RpcErrors400.PasswordHashInvalid.ThrowRpcError();
        }
    }

    private async Task DoTransferAsync(IRequestInput input, long fromUserId, int msgId, BsonDocument bot, long recipientId)
    {
        // Checked again: the confirmation can be pressed long after it was sent.
        var problem = await GetTransferProblemAsync(fromUserId, bot, recipientId);
        if (problem != null)
        {
            await EditMessageAsync(input, fromUserId, msgId, problem, null);
            return;
        }

        var botUserId = BotUserIdOf(bot);
        await mongoDatabase.GetCollection<BsonDocument>(BotCollection).UpdateOneAsync(
            new BsonDocument("BotUserId", botUserId), new BsonDocument("$set", new BsonDocument("OwnerId", recipientId)));
        await mongoDatabase.GetCollection<BsonDocument>("bot-owners").UpdateManyAsync(
            new BsonDocument("BotId", botUserId), new BsonDocument("$set", new BsonDocument("OwnerId", recipientId)));
        await UpdateBotUserAsync(botUserId, Builders<BsonDocument>.Update.Set("CreatorUserId", recipientId));
        await ClearStateAsync(fromUserId);

        var recipient = await GetBotUserAsync(recipientId);
        var owner = await GetBotUserAsync(fromUserId);
        var username = BotUsername(bot);
        await EditMessageAsync(input, fromUserId, msgId,
            $"Success! @{username} now belongs to {DisplayName(recipient)}. /help", null);

        // Only someone who already talks to BotFather is told; BotFather does not start conversations.
        var knowsBotFather = await mongoDatabase.GetCollection<BsonDocument>("eventflow-dialogreadmodel")
            .Find(Builders<BsonDocument>.Filter.Eq("OwnerId", recipientId) &
                  Builders<BsonDocument>.Filter.Eq("ToPeerId", BotUserId))
            .AnyAsync();
        if (knowsBotFather)
        {
            await SendAsync(input, recipientId,
                $"{DisplayName(owner)} transferred the bot @{username} to you. Use /mybots to manage it.");
        }
    }

    private async Task<long?> ResolveUserNameAsync(string text)
    {
        var name = text.Trim().TrimStart('@');
        foreach (var prefix in new[] { "https://t.me/", "http://t.me/", "t.me/" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
            }
        }

        if (name.Length == 0)
        {
            return null;
        }

        var row = await mongoDatabase.GetCollection<BsonDocument>("eventflow-usernamereadmodel")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", $"username-{name.ToLowerInvariant()}"))
            .FirstOrDefaultAsync();

        return row != null && row.TryGetValue("PeerType", out var type) && type.ToInt32() == (int)PeerType.User
            ? row["PeerId"].ToInt64()
            : null;
    }

    private static string DisplayName(BsonDocument? user)
    {
        var name = $"{GetString(user, "FirstName")} {GetString(user, "LastName")}".Trim();
        var username = GetString(user, "UserName");
        return username.Length > 0 ? $"{name} (@{username})" : name;
    }

    private static bool GetBool(BsonDocument document, string name) =>
        document.TryGetValue(name, out var value) && value.IsBoolean && value.AsBoolean;
}
