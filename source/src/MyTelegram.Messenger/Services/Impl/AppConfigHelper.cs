namespace MyTelegram.Messenger.Services.Impl;
public partial class AppConfigHelper : IAppConfigHelper, ISingletonDependency
{
    public AppConfigHelper()
    {
        GetAppConfig();
    }

    public int GetAppConfigHash()
    {
        return _hash;
    }

    /// <summary>
    /// Hashes the serialized config, so the value is identical in every process and changes with
    /// any edit. Zero is avoided: clients send it to mean "nothing cached".
    /// </summary>
    private static int ComputeHash(TJsonObject config)
    {
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        config.Serialize(writer);
        var hash = (int)System.IO.Hashing.XxHash32.HashToUInt32(writer.WrittenSpan);
        return hash == 0 ? 1 : hash;
    }

    private void AddCustomConfig()
    {
        SetConfig("stars_paid_messages_available", new TJsonBool { Value = true });
        SetConfig("stars_paid_reactions_available", new TJsonBool { Value = true });
        SetConfig("stars_paid_message_amount_max", new TJsonNumber { Value = 10000 });
        SetConfig("hidden_members_group_size_min", new TJsonNumber { Value = 10 });
        SetConfig("stargifts_blocked", new TJsonBool { Value = false });
        SetConfig("stars_gifts_enabled", new TJsonBool { Value = true });
        SetConfig("premium_gift_attach_menu_icon", new TJsonBool { Value = true });
        SetConfig("premium_gift_text_field_icon", new TJsonBool { Value = true });
        // Must always be sent: tdlib and tdesktop both read a missing key as "blocked". Accounts Premium
        // is off sale for get true in GetAppConfigHandler.
        SetConfig("premium_purchase_blocked", new TJsonBool { Value = false });

        // Telegram Premium, matched to real Telegram's appConfig (measured 2026-10-31). Purchases go
        // through @PremiumBot, which prod advertises with no premium_invoice_slug fallback and no
        // premium_manage_subscription_url; the generated "abc" slug and premiumbot URL pointed nowhere.
        // See https://corefork.telegram.org/api/premium
        SetConfig("premium_bot_username", new TJsonString { Value = MyTelegramConsts.PremiumBotUserName });
        RemoveConfig("premium_invoice_slug");
        RemoveConfig("premium_manage_subscription_url");
        SetConfig("premium_promo_order", new TJsonArray
        {
            Value = new TVector<IJSONValue>(new[]
            {
                "stories", "more_upload", "double_limits", "business", "last_seen", "voice_to_text",
                "faster_download", "translations", "animated_emoji", "emoji_status", "saved_tags", "peer_colors",
                "wallpapers", "profile_badge", "message_privacy", "pm_noforwards", "advanced_chat_management",
                "no_ads", "app_icons", "infinite_reactions", "animated_userpics", "premium_stickers", "effects",
                "ai_compose", "todo"
            }.Select(feature => (IJSONValue)new TJsonString { Value = feature }))
        });
        SetConfig("bots_create_limit_default", new TJsonNumber { Value = 20 });
        SetConfig("bots_create_limit_premium", new TJsonNumber { Value = 40 });
        SetConfig("giveaway_gifts_purchase_available", new TJsonBool { Value = true });
        SetConfig("settings_display_passkeys", new TJsonBool { Value = true });
        SetConfig("passkeys_account_passkeys_max", new TJsonNumber { Value = 20 });
        SetConfig("factcheck_length_limit", new TJsonNumber { Value = 1024 });

        // GIF search runs through the built-in @gif inline bot. Only the legacy config#cc1a241e field
        // carried this before, and the generated appConfig mentions the key in a comment without ever
        // emitting it, so clients that read it from appConfig found nothing.
        // See https://corefork.telegram.org/api/gifs#searching-gifs
        SetConfig("gif_search_username", new TJsonString { Value = "gif" });
        SetConfig("telegram_antispam_user_id", new TJsonString
        {
            Value = MyTelegramConsts.AntiSpamBotUserId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });

        // Telegram advertises 200 here, which no group on this instance reaches, so the toggle could
        // never be enabled. Lowered like hidden_members_group_size_min above: both the client UI and
        // ToggleAntiSpamHandler read this same advertised value.
        // See https://core.telegram.org/api/antispam
        SetConfig("telegram_antispam_group_size_min", new TJsonNumber { Value = 10 });

        // Affiliate Programs configuration
        SetConfig("starref_program_allowed", new TJsonBool { Value = true });
        SetConfig("starref_connect_allowed", new TJsonBool { Value = true });
        SetConfig("starref_min_commission_permille", new TJsonNumber { Value = 10 });
        SetConfig("starref_max_commission_permille", new TJsonNumber { Value = 500 });
    }

    private void RemoveConfig(string key)
    {
        var oldValue = _jsonValue?.Value.FirstOrDefault(p => p.Key == key);
        if (oldValue != null)
        {
            _jsonValue?.Value.Remove(oldValue);
        }
    }

    private void SetConfig(string key, IJSONValue value)
    {
        var oldValue = _jsonValue?.Value.FirstOrDefault(p => p.Key == key);
        if (oldValue != null)
        {
            _jsonValue?.Value.Remove(oldValue);
        }
        _jsonValue?.Value.Add(new TJsonObjectValue
        {
            Key = key,
            Value = value
        });
    }
}
