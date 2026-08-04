namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// Reads the server's catalogue of available reactions (the "reactions" collection, seeded by
/// scripts/seed_reactions.py) so the various reaction list methods share one source of truth.
/// See https://corefork.telegram.org/api/reactions
/// </summary>
public interface IReactionListAppService
{
    /// <summary>
    /// Active, non-Premium emoji reactions in display order.
    /// </summary>
    Task<List<IReaction>> GetActiveEmojiReactionsAsync(int limit);

    /// <summary>
    /// True when the emoticon exists in the catalogue and is not marked inactive.
    /// </summary>
    Task<bool> IsKnownEmoticonAsync(string emoticon);
}
