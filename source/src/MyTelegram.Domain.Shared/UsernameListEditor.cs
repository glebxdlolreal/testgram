namespace MyTelegram;

/// <summary>
/// The rules for editing a <c>usernames</c> vector, see
/// <a href="https://corefork.telegram.org/api/fragment">Fragment</a>. They mirror tdlib's
/// <c>Usernames</c> class (td/telegram/Usernames.cpp), which keeps the list as the active usernames
/// in display order followed by the disabled ones; clients apply the same edits locally and must end
/// up with what the server stores.
/// </summary>
public static class UsernameListEditor
{
    /// <summary>
    /// How many usernames may be active at once; one more is <c>USERNAMES_ACTIVE_TOO_MUCH</c>.
    /// </summary>
    public const int MaxActiveUsernames = 10;

    /// <summary>
    /// Whether the list carries a collectible. Without one, Telegram describes the peer with the
    /// plain <c>username</c> field and no vector.
    /// </summary>
    public static bool HasCollectibles(IReadOnlyList<UsernameItem>? usernames)
    {
        return usernames?.Any(p => !p.Editable) == true;
    }

    /// <summary>
    /// <c>account.toggleUsername</c>, <c>bots.toggleUsername</c> and <c>channels.toggleUsername</c>.
    /// Returns <see langword="null"/> when the username already has the requested state.
    /// </summary>
    /// <param name="forBot">
    /// A bot may disable its editable username while another username stays active; users and
    /// channels never can (tdlib <c>Usernames::can_toggle</c>).
    /// </param>
    public static IReadOnlyList<UsernameItem>? Toggle(IReadOnlyList<UsernameItem>? usernames, string username,
        bool active, bool forBot)
    {
        var list = usernames ?? [];
        var index = IndexOf(list, username);
        if (index < 0)
        {
            RpcErrors.RpcErrors400.UsernameInvalid.ThrowRpcError();
        }

        var target = list[index];
        if (target.Active == active)
        {
            return null;
        }

        var activeCount = list.Count(p => p.Active);
        if (!active && target.Editable && (!forBot || activeCount < 2))
        {
            RpcErrors.RpcErrors400.UsernameInvalid.ThrowRpcError();
        }

        if (active && activeCount >= MaxActiveUsernames)
        {
            RpcErrors.RpcErrors400.UsernamesActiveTooMuch.ThrowRpcError();
        }

        var result = list.Where((_, i) => i != index).ToList();
        if (active)
        {
            // An activated username goes to the end of the active ones.
            result.Insert(result.Count(p => p.Active), target with { Active = true });
        }
        else
        {
            // A disabled one goes to the front of the disabled ones.
            result.Insert(result.Count(p => p.Active), target with { Active = false });

            if (forBot && !result.Any(p => p.Active))
            {
                // A bot is never left without a reachable username: tdlib turns the disabled
                // editable username back on when the last collectible goes.
                var editableIndex = result.FindIndex(p => p.Editable);
                if (editableIndex >= 0)
                {
                    var editable = result[editableIndex];
                    result.RemoveAt(editableIndex);
                    result.Insert(0, editable with { Active = true });
                }
            }
        }

        return result;
    }

    /// <summary>
    /// <c>account.reorderUsernames</c>, <c>bots.reorderUsernames</c> and
    /// <c>channels.reorderUsernames</c>: <paramref name="order"/> must name every active username
    /// exactly once (tdlib <c>Usernames::can_reorder_to</c>). Returns <see langword="null"/> when the
    /// order does not change.
    /// </summary>
    public static IReadOnlyList<UsernameItem>? Reorder(IReadOnlyList<UsernameItem>? usernames,
        IEnumerable<string> order)
    {
        var list = usernames ?? [];
        var activeItems = list.Where(p => p.Active).ToList();
        var requested = order.ToList();
        if (requested.Count != activeItems.Count)
        {
            RpcErrors.RpcErrors400.OrderInvalid.ThrowRpcError();
        }

        var remaining = activeItems.ToDictionary(p => p.Username, StringComparer.OrdinalIgnoreCase);
        var reordered = new List<UsernameItem>(list.Count);
        foreach (var username in requested)
        {
            if (string.IsNullOrEmpty(username) || !remaining.Remove(username, out var item))
            {
                RpcErrors.RpcErrors400.OrderInvalid.ThrowRpcError();
                return null;
            }

            reordered.Add(item);
        }

        reordered.AddRange(list.Where(p => !p.Active));

        return reordered.SequenceEqual(list) ? null : reordered;
    }

    /// <summary>
    /// <c>channels.deactivateAllUsernames</c>: every collectible becomes disabled, the editable
    /// username (if active) stays (tdlib <c>Usernames::deactivate_all</c>). Returns
    /// <see langword="null"/> when nothing was active besides the editable username.
    /// </summary>
    public static IReadOnlyList<UsernameItem>? DeactivateAll(IReadOnlyList<UsernameItem>? usernames)
    {
        var list = usernames ?? [];
        var keptActive = list.Where(p => p.Active && p.Editable).ToList();
        var disabled = list.Where(p => p.Active && !p.Editable).Select(p => p with { Active = false })
            .Concat(list.Where(p => !p.Active));
        var result = keptActive.Concat(disabled).ToList();

        return result.SequenceEqual(list) ? null : result;
    }

    /// <summary>
    /// Applies a change of the basic username (<c>account.updateUsername</c>,
    /// <c>channels.updateUsername</c>) to the vector, as tdlib's
    /// <c>Usernames::change_editable_username</c> does: the editable entry keeps its position, an
    /// empty name removes it, and a new one is put first. A peer without a vector keeps none.
    /// </summary>
    public static List<UsernameItem>? ChangeEditable(IReadOnlyList<UsernameItem>? usernames, string? newUsername)
    {
        if (usernames == null || usernames.Count == 0)
        {
            return usernames?.ToList();
        }

        var result = usernames.ToList();
        var index = result.FindIndex(p => p.Editable);
        if (index >= 0)
        {
            if (string.IsNullOrEmpty(newUsername))
            {
                result.RemoveAt(index);
            }
            else
            {
                result[index] = result[index] with { Username = newUsername };
            }
        }
        else if (!string.IsNullOrEmpty(newUsername))
        {
            result.Insert(0, new UsernameItem(newUsername, true, true));
        }

        return result;
    }

    /// <summary>
    /// Attaches a collectible bought on Fragment. It starts disabled, visible only to the owner until
    /// they turn it on. A peer that only had the legacy username gets it as the editable entry.
    /// Returns <see langword="null"/> when the collectible is already attached.
    /// </summary>
    public static List<UsernameItem>? AddCollectible(IReadOnlyList<UsernameItem>? usernames, string? legacyUsername,
        string collectible)
    {
        var result = Effective(usernames, legacyUsername);
        if (IndexOf(result, collectible) >= 0)
        {
            return null;
        }

        result.Add(new UsernameItem(collectible, false, false));
        return result;
    }

    /// <summary>
    /// Detaches a collectible (sold or revoked). Returns <see langword="null"/> when it is not attached.
    /// </summary>
    public static List<UsernameItem>? RemoveCollectible(IReadOnlyList<UsernameItem>? usernames,
        string? legacyUsername, string collectible)
    {
        var result = Effective(usernames, legacyUsername);
        var index = IndexOf(result, collectible);
        if (index < 0 || result[index].Editable)
        {
            return null;
        }

        result.RemoveAt(index);
        return result;
    }

    /// <summary>
    /// The usernames a peer effectively has: the stored vector, or just the legacy username as the
    /// editable entry when there is no vector.
    /// </summary>
    public static List<UsernameItem> Effective(IReadOnlyList<UsernameItem>? usernames, string? legacyUsername)
    {
        if (usernames is { Count: > 0 })
        {
            return usernames.ToList();
        }

        return string.IsNullOrEmpty(legacyUsername) ? [] : [new UsernameItem(legacyUsername, true, true)];
    }

    private static int IndexOf(IReadOnlyList<UsernameItem> usernames, string username)
    {
        for (var i = 0; i < usernames.Count; i++)
        {
            if (string.Equals(usernames[i].Username, username, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
