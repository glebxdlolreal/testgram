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
    /// <summary>
    /// How long entries of the <a href="https://corefork.telegram.org/api/recent-actions">admin log</a>
    /// are kept. The official server keeps the last 48 hours.
    /// </summary>
    [Range(3600, int.MaxValue)]
    public int AdminLogRetentionSeconds { get; set; } = 48 * 60 * 60;

    public EncryptionConfig EncryptionConfig { get; set; }
    public StripeConfig Stripe { get; set; } = new();
    public PushConfig Push { get; set; } = new();
    public StatsConfig Stats { get; set; } = new();
    public RatesConfig Rates { get; set; } = new();
    public CallsConfig Calls { get; set; } = new();
    public WebAppsConfig WebApps { get; set; } = new();
    public VideoProcessingConfig VideoProcessing { get; set; } = new();
    public AccountDeletionConfig AccountDeletion { get; set; } = new();
    public HistoryImportConfig HistoryImport { get; set; } = new();
    public PassportConfig Passport { get; set; } = new();
    public PaymentsConfig Payments { get; set; } = new();
    public GifsConfig Gifs { get; set; } = new();
    public WebFilesConfig WebFiles { get; set; } = new();
    public TranscriptionConfig Transcription { get; set; } = new();
    public TranslationConfig Translation { get; set; } = new();
}

/// <summary>
/// Which speech recognition API the transcription client speaks.
/// </summary>
public enum TranscriptionProvider
{
    /// <summary>
    /// Deepgram: <c>POST {BaseUrl}/listen</c>, <c>Authorization: Token …</c>, the audio as the raw request
    /// body. It accepts a Telegram voice note (OGG OPUS) and a round video note (MP4) as they are, so
    /// nothing has to be transcoded first.
    /// </summary>
    Deepgram,

    /// <summary>
    /// The OpenAI-compatible shape: <c>POST {BaseUrl}/audio/transcriptions</c>,
    /// <c>Authorization: Bearer …</c>, <c>multipart/form-data</c>. Reaches OpenAI itself, VoidAI, and most
    /// self-hosted whisper servers — but refuses OGG, so the body is transcoded to MP3 first.
    /// </summary>
    OpenAiCompatible
}

/// <summary>
/// Voice message transcription, see https://corefork.telegram.org/api/transcribe. Recognition itself is
/// done by an external service, so a deployment can point this at Deepgram, OpenAI, VoidAI, or a locally
/// hosted whisper without a rebuild.
/// </summary>
public class TranscriptionConfig
{
    /// <summary>
    /// Master switch. Off means <c>messages.transcribeAudio</c> answers <c>TRANSCRIPTION_FAILED</c>
    /// rather than queueing work nothing will ever pick up.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Which API shape <see cref="BaseUrl"/> speaks.</summary>
    public TranscriptionProvider Provider { get; set; } = TranscriptionProvider.Deepgram;

    /// <summary>Base URL including the version segment, without a trailing slash.</summary>
    public string BaseUrl { get; set; } = "https://api.deepgram.com/v1";

    /// <summary>
    /// API key. Empty disables recognition the same way <see cref="Enabled"/> does. Belongs in
    /// <c>docker/compose/.env</c>, never in a committed file.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Recognition model. <c>nova-3</c> is Deepgram's current general model and covers 142 languages
    /// including Russian; on the OpenAI-compatible path the equivalent is <c>whisper-1</c>.
    /// </summary>
    public string Model { get; set; } = "nova-3";

    /// <summary>
    /// Detect the spoken language instead of assuming one. A messenger has no single language, and the
    /// detected value is only recorded — clients are never told it.
    /// </summary>
    public bool DetectLanguage { get; set; } = true;

    /// <summary>
    /// Deepgram's <c>smart_format</c>: punctuation, capitalisation and number formatting. Without it the
    /// transcript arrives as one unpunctuated lower-case run, which is what a client would display.
    /// </summary>
    public bool SmartFormat { get; set; } = true;

    /// <summary>Wall clock limit for one recognition call.</summary>
    [Range(5, 600)]
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Ceiling on the duration a Premium caller may have transcribed, in seconds. 0 means only
    /// <see cref="MaxUploadBytes"/> binds. Non-Premium callers are bounded by
    /// <c>transcribe_audio_trial_duration_max</c> instead, which is the number their client was told.
    /// </summary>
    public int MaxDurationSeconds { get; set; }

    /// <summary>
    /// Largest body handed to the provider. 25 MB is the documented cap of the OpenAI-compatible
    /// transcription endpoint; Deepgram's is far higher, but a voice note never comes close either way.
    /// </summary>
    public long MaxUploadBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>
    /// Recognition attempts before a transcription is given up on. tdlib fails a pending transcription
    /// after 60 seconds (<c>AUDIO_TRANSCRIPTION_TIMEOUT</c>), so there is no point retrying for long.
    /// </summary>
    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Length of the free-trial window for non-Premium callers, in days. The API calls the allowance
    /// "per week" and every client renders the reset date it is given, so this should stay at 7.
    /// </summary>
    [Range(1, 90)]
    public int TrialWindowDays { get; set; } = 7;
}

/// <summary>
/// Web files this server fetches on a client's behalf for <c>upload.getWebFile</c> — the bodies behind a
/// proxied <c>webDocument</c>, which is how the previews of a GIF search reach the client.
/// See https://corefork.telegram.org/method/upload.getWebFile
/// </summary>
public class WebFilesConfig
{
    /// <summary>
    /// Hosts that may be fetched, matched on the host itself or any subdomain of it. A URL still has to
    /// carry the signature this server issued, so this is a second fence rather than the only one.
    /// Tenor serves its renditions from <c>media.tenor.com</c> and its numbered siblings; @PremiumBot's
    /// invoice picture is real Telegram's own, on <c>telegram.org</c>.
    /// </summary>
    public List<string> AllowedHosts { get; set; } = ["tenor.com", "tenorapi.com", "googleusercontent.com", "telegram.org"];

    /// <summary>
    /// Largest body that will be proxied. A GIF preview is tens of kilobytes. The cache keeps a body in
    /// one document, and BSON caps a document at 16 MB, so this stays well under that.
    /// </summary>
    public long MaxBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>How long a fetched body is kept, so reading it in slices costs one download.</summary>
    public int CacheSeconds { get; set; } = 3600;

    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// GIF search, served by the built-in <c>@gif</c> inline bot named by
/// <c>config.gif_search_username</c>. Results come from the GIFs already stored on this server plus,
/// when enabled, Tenor.
/// See https://corefork.telegram.org/api/gifs#searching-gifs
/// </summary>
public class GifsConfig
{
    /// <summary>How many results one inline query answers with. Telegram caps a page at 50.</summary>
    [Range(1, 50)]
    public int ResultLimit { get; set; } = 30;

    /// <summary>
    /// How many of the results may come from this server's own GIFs, so a searched-for term still
    /// mostly returns Tenor matches rather than being crowded out by locally stored files.
    /// </summary>
    [Range(0, 50)]
    public int LocalResultLimit { get; set; } = 6;

    /// <summary>
    /// How long clients may cache an answer, in seconds — echoed back in <c>botResults.cache_time</c>.
    /// </summary>
    public int CacheTimeSeconds { get; set; } = 300;

    public TenorConfig Tenor { get; set; } = new();
}

/// <summary>
/// Tenor is the provider Telegram itself credits through <c>appConfig.gif_search_branding</c>.
/// The default key is the public gboard client key; a deployment may swap it or switch Tenor off
/// entirely, in which case GIF search answers from this server's own GIFs alone.
/// </summary>
public class TenorConfig
{
    public bool Enabled { get; set; } = true;

    public string ApiKey { get; set; } = "AIzaSyAyimkuYQYF_FXVALexPuGQctUWRURdCYQ";

    public string ClientKey { get; set; } = "gboard";

    public string BaseUrl { get; set; } = "https://tenor.googleapis.com/v2";

    /// <summary><c>off</c>, <c>low</c>, <c>medium</c> or <c>high</c>, as Tenor defines them.</summary>
    public string ContentFilter { get; set; } = "medium";

    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Bot payments, see https://corefork.telegram.org/api/payments. Invoices are settled in Telegram
/// Stars only — there is no per bot payment provider — so the knobs here are the card issuer table
/// and the store top-up escape hatch.
/// </summary>
public class PaymentsConfig
{
    /// <summary>
    /// Path to the JSON BIN prefix -> issuer table behind <c>payments.getBankCardData</c>.
    /// Empty means the table shipped with the server.
    /// </summary>
    public string BankBinsFile { get; set; } = string.Empty;

    /// <summary>
    /// Lets a Stars purchase settle when the server could not confirm that any money changed hands:
    /// an App Store or Play receipt it cannot check with Apple or Google, or a
    /// <c>payments.sendPaymentForm</c> whose charge was never confirmed with Stripe.
    /// </summary>
    /// <remarks>
    /// In all of those cases the amount is, in the end, whatever the caller asked for, so this is a
    /// test-stand convenience and must stay off in production — there the only honest answer is
    /// <c>PAYMENT_PROVIDER_INVALID</c>. Even when on, each receipt settles exactly once and
    /// <see cref="UnverifiedTopupLimit"/> caps what an account can ever be granted this way.
    /// </remarks>
    public bool AllowUnverifiedTopup { get; set; }

    /// <summary>
    /// Ceiling, in Stars, on everything one account may be credited over its lifetime through the
    /// unverified path. Ignored while <see cref="AllowUnverifiedTopup"/> is off.
    /// </summary>
    public long UnverifiedTopupLimit { get; set; } = 10_000;
}

/// <summary>
/// Telegram Passport, see https://corefork.telegram.org/api/passport. The server stores the documents
/// end-to-end encrypted and never holds a key, so the only knobs here are size limits and the
/// country/language table served by <c>help.getPassportConfig</c>.
/// </summary>
public class PassportConfig
{
    /// <summary>
    /// Largest single passport file accepted. The clients cap scans at 10 MB before encrypting, so
    /// anything above that comes from a client that is not playing by the rules.
    /// </summary>
    [Range(1024, 64 * 1024 * 1024)]
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Path to the JSON country code -> form language table returned as
    /// <c>help.passportConfig.countries_langs</c>. Empty means the table shipped with the server.
    /// </summary>
    public string CountriesLangsFile { get; set; } = string.Empty;
}

/// <summary>
/// Import of a chat history exported from another chat app.
/// See https://corefork.telegram.org/api/import
/// </summary>
public class HistoryImportConfig
{
    /// <summary>Runs the queued imports. Turning this off leaves them parked.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Largest export file accepted. The official clients refuse to upload anything above 32 MB, so
    /// a bigger file can only come from a client that is not playing by the rules.
    /// </summary>
    [Range(1024, 256 * 1024 * 1024)]
    public long MaxFileSizeBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>Messages accepted from a single export file.</summary>
    [Range(1, 1_000_000)]
    public int MaxMessages { get; set; } = 100_000;

    /// <summary>Media files accepted alongside a single export file.</summary>
    [Range(0, 100_000)]
    public int MaxMediaCount { get; set; } = 1000;

    /// <summary>Messages sent per batch by the background worker.</summary>
    [Range(1, 500)]
    public int BatchSize { get; set; } = 50;

    /// <summary>Pause between two batches, so a large import cannot flood the command bus.</summary>
    [Range(0, 60_000)]
    public int BatchDelayMilliseconds { get; set; } = 200;

    /// <summary>
    /// How long a chat is considered busy with an import, which is also the number of minutes reported
    /// by <c>PREVIOUS_CHAT_IMPORT_ACTIVE_WAIT_%dMIN</c>.
    /// </summary>
    [Range(1, 1440)]
    public int ActiveImportTimeoutMinutes { get; set; } = 30;

    /// <summary>Runs before the worker gives up on an import that keeps failing.</summary>
    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 3;
}

/// <summary>
/// Account deletion, see https://corefork.telegram.org/api/account-deletion: the delay granted to
/// an account protected by a 2FA password the caller could not provide, and the self-destruction
/// timer of inactive accounts set through <c>account.setAccountTTL</c>.
/// </summary>
public class AccountDeletionConfig
{
    /// <summary>Executes delayed deletions. Turning this off leaves pending deletions parked forever.</summary>
    public bool Enabled { get; set; } = true;

    [Range(10, int.MaxValue)]
    public int SweepIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// How long a deletion is delayed when the account has a 2FA password that was not provided.
    /// The official server grants one week, which is also how long the confirmphone link stays valid.
    /// </summary>
    [Range(1, 30)]
    public int TwoFaDelayDays { get; set; } = 7;

    /// <summary>
    /// Deletes accounts that have not come online for longer than their <c>account.setAccountTTL</c>
    /// period. Telegram's own default is 18 months of inactivity.
    /// </summary>
    public bool SelfDestructEnabled { get; set; } = true;

    /// <summary>Accounts deleted per self-destruct pass, so one sweep cannot stall the worker.</summary>
    [Range(1, 10000)]
    public int SelfDestructBatchSize { get; set; } = 100;
}

/// <summary>
/// Server side video processing: videos posted to a big channel are converted into alternative
/// qualities before the message is delivered, and the extra renditions ride along in
/// <c>messageMediaDocument.alt_documents</c>.
/// See https://corefork.telegram.org/api/scheduled-messages#automatic-video-processing
/// </summary>
public class VideoProcessingConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many participants a broadcast channel needs before its videos are converted. Telegram
    /// only does this for "big channels"; on a self hosted server the bar is a configuration choice.
    /// </summary>
    public int MinChannelParticipants { get; set; } = 1;

    /// <summary>
    /// Heights of the alternative qualities to generate. A rung is skipped when the source video is
    /// not taller than it, so a 480p upload never gets a 720p "alternative".
    /// </summary>
    public List<int> Heights { get; set; } = [360, 480, 720];

    public string FfmpegPath { get; set; } = "ffmpeg";

    public string FfprobePath { get; set; } = "ffprobe";

    public string Preset { get; set; } = "veryfast";

    [Range(0, 51)]
    public int Crf { get; set; } = 28;

    public string AudioBitrate { get; set; } = "96k";

    /// <summary>Videos larger than this are delivered untouched.</summary>
    public long MaxSourceSizeBytes { get; set; } = 200L * 1024 * 1024;

    /// <summary>Videos longer than this are delivered untouched.</summary>
    public int MaxDurationSeconds { get; set; } = 30 * 60;

    /// <summary>Wall clock limit for one ffmpeg run.</summary>
    public int TimeoutSeconds { get; set; } = 30 * 60;

    /// <summary>
    /// Seconds of conversion assumed per second of video, used for the estimated conversion date the
    /// client shows while the message sits in the queue.
    /// </summary>
    public double EstimateSecondsPerSecond { get; set; } = 0.5;

    [Range(5, int.MaxValue)]
    public int MinEstimateSeconds { get; set; } = 15;
}

/// <summary>
/// Mini app (<c>bots/webapps</c>) configuration. Mini apps are ordinary web pages served over
/// HTTPS by the bot's own developer; the server never supplies a URL of its own - each bot owner
/// sets theirs through BotFather (/newapp, /editapp, "Configure Mini App").
/// See https://corefork.telegram.org/api/bots/webapps .
/// </summary>
public class WebAppsConfig
{
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

/// <summary>
/// Which translation API the text translation client speaks.
/// </summary>
public enum TextTranslationProvider
{
    /// <summary>
    /// DeepL: <c>POST {BaseUrl}/translate</c>, <c>Authorization: DeepL-Auth-Key …</c>,
    /// <c>application/x-www-form-urlencoded</c> with the texts as repeated <c>text</c> fields.
    /// A key ending in <c>:fx</c> is a free-tier key and only works against
    /// <c>https://api-free.deepl.com</c>; a paid key only works against <c>https://api.deepl.com</c>.
    /// </summary>
    DeepL
}

/// <summary>
/// Message translation, see https://corefork.telegram.org/api/translation. Translation itself is done by
/// an external service, so a deployment can point this at DeepL without a rebuild.
/// </summary>
public class TranslationConfig
{
    /// <summary>
    /// Master switch. Off — or an empty <see cref="ApiKey"/> — means <c>messages.translateText</c>
    /// answers <c>406 TRANSLATIONS_DISABLED</c> plus an <c>updateServiceNotification</c>, which is what
    /// the method documents for a deployment with no translation backend. Answering fabricated text
    /// instead, as this used to, is indistinguishable from a working translation to every client.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Which API shape <see cref="BaseUrl"/> speaks.</summary>
    public TextTranslationProvider Provider { get; set; } = TextTranslationProvider.DeepL;

    /// <summary>
    /// Base URL including the version segment, without a trailing slash. Must match the key's tier:
    /// <c>https://api-free.deepl.com/v2</c> for a <c>:fx</c> key, <c>https://api.deepl.com/v2</c> otherwise.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api-free.deepl.com/v2";

    /// <summary>
    /// API key. Empty disables translation the same way <see cref="Enabled"/> does. Belongs in
    /// <c>docker/compose/.env</c>, never in a committed file.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Whether styled text entities are carried through the translation for Premium callers. Off falls
    /// back to plain text for everybody, which is what a provider without inline-markup support needs.
    /// </summary>
    public bool PreserveEntities { get; set; } = true;

    /// <summary>Wall clock limit for one translation call. Past it the caller gets TRANSLATION_TIMEOUT.</summary>
    [Range(3, 120)]
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// How long a translated text stays in <c>translation_texts</c>. The cache is not an optimisation:
    /// clients re-request the messages on screen, and DeepL bills per character.
    /// </summary>
    [Range(1, 365)]
    public int CacheDays { get; set; } = 30;

    /// <summary>
    /// Largest number of texts in one request. Android caps itself at 20
    /// (<c>MAX_MESSAGES_PER_REQUEST</c>) and so does tdesktop (<c>kRequestCountLimit</c>), so this must
    /// not be lower than that or an ordinary batch is refused.
    /// </summary>
    [Range(1, 100)]
    public int MaxMessagesPerRequest { get; set; } = 20;

    /// <summary>
    /// Largest total number of UTF-16 code units in one request. Android caps itself at 25000
    /// (<c>MAX_SYMBOLS_PER_REQUEST</c>), tdesktop at 24576 (<c>kRequestLengthLimit</c>).
    /// </summary>
    [Range(1000, 200000)]
    public int MaxCharactersPerRequest { get; set; } = 25000;
}


