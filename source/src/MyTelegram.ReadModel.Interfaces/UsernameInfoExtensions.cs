namespace MyTelegram.ReadModel.Interfaces;

/// <summary>
/// Converts between the stored <see cref="UsernameInfo"/> shape and the <see cref="UsernameItem"/>
/// events carry.
/// </summary>
public static class UsernameInfoExtensions
{
    public static List<UsernameItem>? ToUsernameItems(this IEnumerable<UsernameInfo>? usernames)
    {
        return usernames?.Select(p => new UsernameItem(p.Username, p.Editable, p.Active)).ToList();
    }

    public static List<UsernameInfo>? ToUsernameInfos(this IEnumerable<UsernameItem>? usernames)
    {
        return usernames?.Select(p => new UsernameInfo
        {
            Username = p.Username,
            Editable = p.Editable,
            Active = p.Active
        }).ToList();
    }

    /// <summary>
    /// Whether <paramref name="username"/> is a Fragment collectible of this peer that is not active.
    /// Such a username is visible to its owner only: for everyone else it does not exist.
    /// </summary>
    public static bool IsDisabledCollectible(this IEnumerable<UsernameInfo>? usernames, string? username)
    {
        return usernames?.Any(p => !p.Editable && !p.Active &&
                                   string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase)) == true;
    }
}
