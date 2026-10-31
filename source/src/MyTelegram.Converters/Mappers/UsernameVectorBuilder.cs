namespace MyTelegram.Converters.Mappers;

/// <summary>
/// Builds the <c>username</c> / <c>usernames</c> pair of a <c>user</c> or <c>channel</c>, see
/// <a href="https://corefork.telegram.org/api/fragment">Fragment</a>.
/// <para>
/// Once a collectible is attached, <c>username</c> must be left unset and the vector carries every
/// username, the first being the main one. tdlib (<c>Usernames::Usernames</c>) treats both being set as
/// a server error and drops all usernames of the peer, so Telegram X, Unigram and other tdlib clients
/// showed none at all.
/// </para>
/// </summary>
public static class UsernameVectorBuilder
{
    /// <param name="stored">The stored usernames vector, if any.</param>
    /// <param name="legacyUserName">The basic username, used when there is no vector.</param>
    /// <param name="includeDisabled">
    /// Whether disabled collectibles are listed. Only the owner may see them: a newly attached
    /// collectible stays invisible to everyone else until it is activated.
    /// </param>
    public static (string? Username, TVector<IUsername>? Usernames) Build(IEnumerable<UsernameInfo>? stored,
        string? legacyUserName, bool includeDisabled)
    {
        IEnumerable<UsernameItem> items = UsernameListEditor.Effective(stored.ToUsernameItems(), legacyUserName);
        if (!includeDisabled)
        {
            items = items.Where(p => p.Active);
        }

        var visible = items.ToList();
        if (!UsernameListEditor.HasCollectibles(visible))
        {
            // Only the basic username: Telegram describes it with the plain field and no vector.
            return (visible.FirstOrDefault(p => p.Active)?.Username, null);
        }

        return (null, new TVector<IUsername>(visible.Select(p => new TUsername
        {
            Username = p.Username,
            Editable = p.Editable,
            Active = p.Active
        })));
    }
}
