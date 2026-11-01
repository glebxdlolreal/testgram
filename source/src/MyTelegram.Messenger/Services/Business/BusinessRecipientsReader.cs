namespace MyTelegram.Messenger.Services.Business;

/// <summary>
/// Reads the recipient selection of greeting/away messages
/// (<a href="https://corefork.telegram.org/constructor/inputBusinessRecipients">inputBusinessRecipients</a>).
/// </summary>
public interface IBusinessRecipientsReader
{
    /// <summary>
    /// Validates the access hash of every listed user and refuses an empty selection with
    /// <c>BUSINESS_RECIPIENTS_EMPTY</c>.
    /// </summary>
    Task<BusinessRecipientsItem> ReadAsync(IRequestInput input, IInputBusinessRecipients recipients);

    /// <summary>The listed users, access hashes checked, without the caller and duplicates.</summary>
    Task<List<long>> ReadUsersAsync(IRequestInput input, IEnumerable<IInputUser>? users);
}

public sealed class BusinessRecipientsReader(IAccessHashHelper2 accessHashHelper, IPeerHelper peerHelper)
    : IBusinessRecipientsReader, ITransientDependency
{
    public async Task<BusinessRecipientsItem> ReadAsync(IRequestInput input, IInputBusinessRecipients recipients)
    {
        if (recipients is not TInputBusinessRecipients r)
        {
            RpcErrors.RpcErrors400.BusinessRecipientsEmpty.ThrowRpcError();
            return null!;
        }

        var userIds = await ReadUsersAsync(input, r.Users);
        var item = new BusinessRecipientsItem(r.ExistingChats, r.NewChats, r.Contacts, r.NonContacts,
            r.ExcludeSelected, userIds);
        if (IsEmpty(item))
        {
            RpcErrors.RpcErrors400.BusinessRecipientsEmpty.ThrowRpcError();
        }

        return item;
    }

    /// <summary>
    /// With <c>exclude_selected</c> the categories and users name who is left out ("all private chats
    /// except the selected", as tdlib documents <c>businessRecipients</c>), so only an inclusive selection
    /// can be empty.
    /// </summary>
    public static bool IsEmpty(BusinessRecipientsItem item)
    {
        var anyCategory = item.ExistingChats || item.NewChats || item.Contacts || item.NonContacts;
        return !item.ExcludeSelected && !anyCategory && item.UserIds.Count == 0;
    }

    public async Task<List<long>> ReadUsersAsync(IRequestInput input, IEnumerable<IInputUser>? users)
    {
        var result = new List<long>();
        foreach (var inputUser in users ?? [])
        {
            if (inputUser is TInputUserSelf or TInputUserEmpty)
            {
                continue;
            }

            await accessHashHelper.CheckAccessHashAsync(input, inputUser);
            var peer = peerHelper.GetPeer(inputUser, input.UserId);
            if (peer.PeerType == PeerType.User && peer.PeerId != input.UserId && !result.Contains(peer.PeerId))
            {
                result.Add(peer.PeerId);
            }
        }

        return result;
    }
}
