using System.ComponentModel.DataAnnotations;

namespace MyTelegram.Messenger;
#nullable disable
public class MyTelegramMessengerServerOptions
{
    public string FileServerGrpcServiceUrl { get; set; }


    [RegularExpression("^([\\d]{3,6})|(\\s*)$")]
    public string FixedVerifyCode { get; set; }

    [Range(3, 6)]
    public int VerificationCodeLength { get; set; } = 5;

    [Range(60, int.MaxValue)]
    public int VerificationCodeExpirationSeconds { get; set; } = 300;
    public string JoinChatDomain { get; set; }

    public int ChannelGetDifferenceIntervalSeconds { get; set; }

    public bool UseInMemoryFilters { get; set; }
    public int EditTimeLimit { get; set; }
    public List<WebRtcConnection> WebRtcConnections { get; set; }
    public int ThisDcId { get; set; }
    public List<DcOption> DcOptions { get; set; }
    public bool AutoCreateSuperGroup { get; set; }
    public bool EnableFutureAuthToken { get; set; }
    public bool SetPremiumToTrueAfterUserCreated { get; set; }
    public bool SendWelcomeMessageAfterUserSignIn { get; set; }
    public bool SetupPasswordRequired { get; set; }
    public bool EnableEmailLogin { get; set; }

    [RegularExpression("^([\\d]{6})|(\\s*)$")]
    public string FixedEmailVerificationCode { get; set; }

    public string? PasskeyRpId { get; set; }
    public string? PasskeyRpName { get; set; }
    public int PasskeysAccountPasskeysMax { get; set; } = 20;

    //public long? SupportUserId { get; set; }
    // https://github.com/dotnet/runtime/issues/36510
    [RegularExpression("^([\\d]{1,19})|(\\s*)$")]
    public string SupportUserId { get; set; }
    public int MaxInMemoryContactCount { get; set; }
    public bool CheckPhoneNumberFormat { get; set; }
    public bool EnableSearchNonContacts { get; set; }
    public int RpcResultExpirationMinutes { get; set; }
    public string RtmpStreamUrl { get; set; } = "rtmp://testgram.xie.su:1935/live";
    public string RtmpHlsUrl { get; set; } = "http://rtmp-server:8888/live";
    public EncryptionConfig EncryptionConfig { get; set; }
    public StripeConfig Stripe { get; set; } = new();
    public PushConfig Push { get; set; } = new();
    public StatsConfig Stats { get; set; } = new();
    public RatesConfig Rates { get; set; } = new();
    public CallsConfig Calls { get; set; } = new();
    public WebAppsConfig WebApps { get; set; } = new();
}

/// <summary>
/// Mini app (<c>bots/webapps</c>) configuration. Mini apps are ordinary web pages served over
/// HTTPS from outside this server; these settings only decide which URL clients are pointed at.
/// See https://corefork.telegram.org/api/bots/webapps .
/// </summary>
public class WebAppsConfig
{
    /// <summary>
    /// Base URL used when a bot has no explicit mini app URL configured. Must be HTTPS: clients
    /// refuse to open a webview over plain HTTP.
    /// </summary>
    public string BaseUrl { get; set; } = "https://testgram.xie.su";

    /// <summary>
    /// Seconds a webview session stays valid without a <c>messages.prolongWebView</c> call. Clients
    /// are expected to prolong every 60 seconds, so this allows a couple of missed beats.
    /// </summary>
    [Range(60, int.MaxValue)]
    public int SessionTimeoutSeconds { get; set; } = 180;
}

/// <summary>
/// 1:1 call (<c>phone.*</c>) configuration: the server-side expiry deadlines for abandoned call
/// sessions, plus the tgcalls runtime knobs returned by <c>phone.getCallConfig</c>.
/// </summary>
public class CallsConfig
{
    /// <summary>
    /// Seconds a session may stay in <c>requested</c> before the server discards it as missed.
    /// Must match <c>call_receive_timeout_ms</c> in <c>help.getConfig</c> (see
    /// <c>ConfigConverter</c>), which is what the client's own timer runs off.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int ReceiveTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// Seconds a session may keep ringing (<c>received</c>) before the server discards it as missed.
    /// Must match <c>call_ring_timeout_ms</c> in <c>help.getConfig</c>.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int RingTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Seconds an answered call (<c>accepted</c>) may take to connect before the server discards it.
    /// Must match <c>call_connect_timeout_ms</c> in <c>help.getConfig</c>.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int ConnectTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Backstop for a connected (<c>confirmed</c>) call whose participants both vanished without
    /// discarding it. Deliberately long - a multi-hour call is legitimate, and this only exists so a
    /// session cannot mark both users busy forever. No grace period is added to this one.
    /// </summary>
    [Range(60, int.MaxValue)]
    public int MaxCallDurationSeconds { get; set; } = 24 * 60 * 60;

    /// <summary>
    /// Added to every pre-connect deadline so the server never beats the client's own timer to the
    /// punch: the client is expected to send <c>phone.discardCall</c> itself, and the sweeper is only
    /// a fallback for clients that died or lost connectivity.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int ExpiryGraceSeconds { get; set; } = 10;

    /// <summary>Maximum sessions examined per sweep, bounding the work of a single pass.</summary>
    [Range(1, int.MaxValue)]
    public int ExpiryBatchSize { get; set; } = 200;

    /// <summary>How often the background worker sweeps for expired sessions.</summary>
    [Range(1, int.MaxValue)]
    public int ExpirySweepIntervalSeconds { get; set; } = 10;

    /// <summary>The tgcalls runtime knobs served by <c>phone.getCallConfig</c>.</summary>
    public CallRuntimeConfig RuntimeConfig { get; set; } = new();
}

/// <summary>
/// The tgcalls runtime configuration served as the <c>dataJSON</c> payload of
/// <c>phone.getCallConfig</c>. Keys are looked up by tgcalls itself (<c>Instance.ServerConfig</c> in
/// the Android client) under fixed snake_case names; unrecognised keys are ignored by clients.
/// Defaults mirror what the clients fall back to when the server says nothing.
/// </summary>
public class CallRuntimeConfig
{
    /// <summary>Use the platform noise suppressor rather than WebRTC's (<c>use_system_ns</c>).</summary>
    public bool UseSystemNs { get; set; } = true;

    /// <summary>Use the platform echo canceller rather than WebRTC's (<c>use_system_aec</c>).</summary>
    public bool UseSystemAec { get; set; } = true;

    /// <summary>Mark STUN packets for QoS (<c>voip_enable_stun_marking</c>). Off by default: it needs
    /// network support and misbehaves on some carriers.</summary>
    public bool EnableStunMarking { get; set; }

    /// <summary>Seconds the hangup UI lingers after the call ends (<c>hangup_ui_timeout</c>).</summary>
    [Range(0.0, 600.0)]
    public double HangupUiTimeout { get; set; } = 5;

    public bool EnableVp8Encoder { get; set; } = true;
    public bool EnableVp8Decoder { get; set; } = true;
    public bool EnableVp9Encoder { get; set; } = true;
    public bool EnableVp9Decoder { get; set; } = true;
    public bool EnableH264Encoder { get; set; } = true;
    public bool EnableH264Decoder { get; set; } = true;
    public bool EnableH265Encoder { get; set; } = true;
    public bool EnableH265Decoder { get; set; } = true;
}

/// <summary>
/// Fiat conversion rates surfaced to clients (e.g. <c>payments.starsRevenueStats.usd_rate</c>).
/// Defaults mirror the appConfig values (<c>ton_usd_rate</c>, <c>stars_usd_sell_rate_x1000</c>).
/// </summary>
public class RatesConfig
{
    /// <summary>
    /// USD per one whole TON. Clients multiply by <c>amount / 1e9</c> (balances are in nanotons).
    /// </summary>
    [Range(0.0, 1_000_000.0)]
    public double TonUsdRate { get; set; } = 3.5293105384415675;

    /// <summary>USD per one Telegram Star (sell rate: 1410 / 100000).</summary>
    [Range(0.0, 1_000.0)]
    public double StarsUsdRate { get; set; } = 0.0141;
}

/// <summary>
/// Statistics subsystem configuration (Stats API). See https://corefork.telegram.org/api/stats .
/// </summary>
public class StatsConfig
{
    /// <summary>
    /// The reporting window, in whole days, used to compute the statistics <c>period</c>
    /// (<c>min_date = max_date - ReportingWindowDays</c>), per Requirement 10.3. Default 7 days;
    /// valid range 1..365 (values outside the range are clamped by the Metrics_Store).
    /// </summary>
    [Range(1, 365)]
    public int ReportingWindowDays { get; set; } = 7;
}

/// <summary>
/// Push-notification (FCM/APNS/APNS-VoIP/Web-Push) delivery configuration.
/// Mirrors https://corefork.telegram.org/api/push-updates . Disabled by default; set
/// <c>Enabled=true</c> and fill in provider credentials to activate delivery.
/// </summary>
public class PushConfig
{
    /// <summary>Master switch. When false, no push payloads are dispatched to providers.</summary>
    public bool Enabled { get; set; } = false;

    public FcmConfig Fcm { get; set; } = new();
    public ApnsConfig Apns { get; set; } = new();
    public WebPushConfig WebPush { get; set; } = new();

    /// <summary>
    /// Firebase Cloud Messaging (token_type = 2). Uses the HTTP v1 API with a service-account JSON.
    /// </summary>
    public class FcmConfig
    {
        /// <summary>Path to the Firebase service-account JSON file, or the JSON contents inline.</summary>
        public string ServiceAccountJson { get; set; } = string.Empty;
        public int PushTimeoutSec { get; set; } = 30;
        public bool Enabled => !string.IsNullOrWhiteSpace(ServiceAccountJson);
    }

    /// <summary>
    /// Apple Push Notification service (token_type = 1 APNS, 9 APNS VoIP).
    /// </summary>
    public class ApnsConfig
    {
        /// <summary>Contents of the .p8 APNs Auth Key (Apple Developer "Keys").</summary>
        public string AuthKeyP8 { get; set; } = string.Empty;
        public string KeyId { get; set; } = string.Empty;
        public string TeamId { get; set; } = string.Empty;
        public string BundleId { get; set; } = string.Empty;
        public int PushTimeoutSec { get; set; } = 30;
        public bool Enabled => !string.IsNullOrWhiteSpace(AuthKeyP8)
                               && !string.IsNullOrWhiteSpace(KeyId)
                               && !string.IsNullOrWhiteSpace(TeamId);
    }

    /// <summary>
    /// Web Push (token_type = 10). Token is a JSON object with endpoint/keys.p256dh/keys.auth.
    /// </summary>
    public class WebPushConfig
    {
        /// <summary>VAPID private key (P-256) as base64url, used to sign push messages.</summary>
        public string VapidPrivateKey { get; set; } = string.Empty;
        /// <summary>VAPID public key (P-256) as base64url.</summary>
        public string VapidPublicKey { get; set; } = string.Empty;
        /// <summary>mailto: or https:// contact for VAPID JWT "sub".</summary>
        public string VapidSubject { get; set; } = string.Empty;
        public int PushTimeoutSec { get; set; } = 30;
        public bool Enabled => !string.IsNullOrWhiteSpace(VapidPrivateKey)
                               && !string.IsNullOrWhiteSpace(VapidPublicKey);
    }
}

public class EncryptionConfig
{
    public bool Enabled { get; set; }
    public string PhoneKey { get; set; }
    public List<KeyConfig> IndexKeys { get; set; }
    public List<KeyConfig> MessageKeys { get; set; }
}

public class KeyConfig
{
    public int Id { get; set; }
    public string Key { get; set; }
}


public class StripeConfig
{
    public string PublishableKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
}
