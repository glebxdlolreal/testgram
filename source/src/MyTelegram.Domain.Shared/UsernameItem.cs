namespace MyTelegram;

/// <summary>
/// One entry of the <c>usernames</c> vector of a user, bot or channel, see
/// <a href="https://corefork.telegram.org/api/fragment">Fragment</a>.
/// </summary>
/// <param name="Username">The username, without <c>@</c>.</param>
/// <param name="Editable">
/// Set for the basic username, which is changed with <c>account.updateUsername</c> or
/// <c>channels.updateUsername</c>; unset for a collectible bought on Fragment.
/// </param>
/// <param name="Active">Whether others can see and resolve it.</param>
public sealed record UsernameItem(string Username, bool Editable, bool Active);
