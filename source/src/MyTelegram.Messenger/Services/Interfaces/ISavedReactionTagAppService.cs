namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// Stores the <a href="https://corefork.telegram.org/api/saved-messages#tags">saved message tags</a>
/// a user created by reacting to their own Saved Messages.
/// </summary>
public interface ISavedReactionTagAppService
{
    /// <summary>
    /// Returns every tag the user owns, most used first. Tags with no messages left are omitted.
    /// </summary>
    Task<List<SavedReactionTagItem>> GetTagsAsync(long userId);

    /// <summary>
    /// Sets (or clears, when <paramref name="title"/> is null/empty) the title of a single tag.
    /// </summary>
    Task SetTitleAsync(long userId, IReaction reaction, string? title);

    /// <summary>
    /// Applies the message-count delta after a user changed their reactions on a Saved Message.
    /// </summary>
    Task UpdateTagCountsAsync(long userId, List<IReaction> removedReactions, List<IReaction> addedReactions);
}

/// <summary>
/// A single saved message tag: the reaction itself, its optional user-defined title, and how many
/// Saved Messages currently carry it.
/// </summary>
public sealed record SavedReactionTagItem(IReaction Reaction, string? Title, int Count);
