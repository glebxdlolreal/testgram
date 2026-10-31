namespace MyTelegram.Messenger.Services;

/// <summary>
/// The name and username values a caller already knows to be current, used when the read model may
/// not have caught up yet.
/// </summary>
public sealed record UserNameSnapshot(string FirstName, string? LastName, string? UserName);

/// <summary>
/// Pushes <c>updateUserName</c> after a username list changed through <c>account.updateUsername</c>,
/// <c>account|bots.toggleUsername</c>, <c>account|bots.reorderUsernames</c> or a Fragment collectible
/// being attached or detached.
/// <para>
/// The <a href="https://corefork.telegram.org/api/peers#peer-info-database">peer info database</a>
/// is meant to be kept fresh reactively: without this update the caller's other sessions, and
/// everybody holding them in a contact list, keep serving a stale username until something else
/// forces a refetch.
/// </para>
/// </summary>
public interface IUsernameUpdateNotifier
{
    /// <summary>
    /// Re-reads the user and delivers <c>updateUserName</c> to their other sessions and to everyone
    /// who has them as a contact.
    /// </summary>
    /// <param name="excludeAuthKeyId">
    /// The permanent auth key of the session that made the call. It already learns the outcome from
    /// the rpc result and applies the change locally, as the API docs require.
    /// </param>
    /// <param name="snapshot">
    /// Values that win over the read model. Domain event handlers run alongside the read model
    /// update, so they pass what the event carried rather than risk announcing the previous name.
    /// </param>
    /// <param name="usernames">
    /// The complete usernames vector, when the triggering event carried it.
    /// </param>
    Task NotifyUserNameChangedAsync(long userId, long? excludeAuthKeyId, UserNameSnapshot? snapshot = null,
        IReadOnlyList<UsernameItem>? usernames = null);
}

public sealed class UsernameUpdateNotifier(
    IUserAppService userAppService,
    IQueryProcessor queryProcessor,
    IObjectMessageSender objectMessageSender)
    : IUsernameUpdateNotifier, ITransientDependency
{
    public async Task NotifyUserNameChangedAsync(long userId, long? excludeAuthKeyId,
        UserNameSnapshot? snapshot = null, IReadOnlyList<UsernameItem>? usernames = null)
    {
        // Locally executed commands never reach the cached read model, so drop it before reading.
        userAppService.InvalidateCache(userId);

        var userReadModel = await userAppService.GetAsync((long?)userId);
        if (userReadModel == null && snapshot == null)
        {
            return;
        }

        var all = BuildUsernames(userReadModel, snapshot, usernames);
        var firstName = snapshot?.FirstName ?? userReadModel!.FirstName;
        var lastName = snapshot?.LastName ?? userReadModel?.LastName ?? string.Empty;

        // The owner sees disabled collectibles too; to everyone else they do not exist.
        await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, userId),
            CreateUpdates(userId, firstName, lastName, all), excludeAuthKeyId: excludeAuthKeyId);

        var contactUserIds = await queryProcessor.ProcessAsync(
            new GetContactSelfUserIdListByTargetUserIdQuery(userId));
        var othersUpdates = CreateUpdates(userId, firstName, lastName, all.Where(p => p.Active).ToList());

        foreach (var contactUserId in contactUserIds.Where(p => p != userId).Distinct())
        {
            await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, contactUserId), othersUpdates);
        }
    }

    private static TUpdates CreateUpdates(long userId, string firstName, string lastName,
        IReadOnlyList<UsernameItem> usernames)
    {
        var update = new TUpdateUserName
        {
            UserId = userId,
            FirstName = firstName,
            LastName = lastName,
            Usernames = new TVector<IUsername>(usernames.Select(p => new TUsername
            {
                Username = p.Username,
                Editable = p.Editable,
                Active = p.Active
            }))
        };

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(update),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    /// <summary>
    /// The full usernames vector: the one the event carried, else the stored one (or the legacy
    /// username alone). A snapshot's username replaces the editable entry in place, the way the read
    /// model applies it, since the read model may not have caught up yet.
    /// </summary>
    internal static IReadOnlyList<UsernameItem> BuildUsernames(IUserReadModel? userReadModel,
        UserNameSnapshot? snapshot, IReadOnlyList<UsernameItem>? usernames)
    {
        var stored = usernames ?? userReadModel?.Usernames.ToUsernameItems();
        if (snapshot == null)
        {
            return UsernameListEditor.Effective(stored, userReadModel?.UserName);
        }

        if (stored is not { Count: > 0 })
        {
            return string.IsNullOrEmpty(snapshot.UserName) ? [] : [new UsernameItem(snapshot.UserName, true, true)];
        }

        return UsernameListEditor.ChangeEditable(stored, snapshot.UserName)!;
    }
}
