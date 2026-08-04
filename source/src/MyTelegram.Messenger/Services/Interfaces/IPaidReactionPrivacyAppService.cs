namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// How a user's paid reactions are attributed: publicly, anonymously, or on behalf of a channel.
/// See https://corefork.telegram.org/api/reactions#paid-reactions
/// </summary>
public enum PaidReactionPrivacyType
{
    /// <summary>Show the sender's own profile.</summary>
    Default = 0,

    /// <summary>Hide the sender entirely.</summary>
    Anonymous = 1,

    /// <summary>Attribute the reaction to another peer (a channel the user owns).</summary>
    Peer = 2
}

public sealed record PaidReactionPrivacySetting(PaidReactionPrivacyType Type, long PeerId = 0);

/// <summary>
/// Stores the paid reaction privacy the user picked, both as an account-wide default and as a
/// per-message override.
/// </summary>
public interface IPaidReactionPrivacyAppService
{
    /// <summary>
    /// The user's account-wide default, used when messages.sendPaidReaction omits the private flag.
    /// </summary>
    Task<PaidReactionPrivacySetting> GetDefaultAsync(long userId);

    /// <summary>
    /// The privacy applied to a specific message, falling back to the account-wide default.
    /// </summary>
    Task<PaidReactionPrivacySetting> GetForMessageAsync(long userId, long peerId, int msgId);

    /// <summary>
    /// Records the privacy for one message and makes it the new account-wide default.
    /// </summary>
    Task SetForMessageAsync(long userId, long peerId, int msgId, PaidReactionPrivacySetting setting);
}
