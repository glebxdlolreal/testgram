namespace MyTelegram.Messenger.Services.Business;

/// <summary>
/// Whether a private chat partner falls under a <c>businessRecipients</c> selection. Categories and listed
/// users select; with <c>exclude_selected</c> everybody except the selection is chosen (tdlib documents
/// <c>businessRecipients.exclude_selected</c> as "all private chats except the selected are chosen").
/// </summary>
public static class BusinessRecipientsMatcher
{
    /// <param name="isContact">The business account has the peer in its contacts.</param>
    /// <param name="isNewChat">The chat started after the setting was made.</param>
    public static bool Matches(BusinessRecipientsItem recipients, long peerUserId, bool isContact, bool isNewChat,
        IReadOnlyCollection<long>? excludedUserIds = null)
    {
        if (excludedUserIds?.Contains(peerUserId) == true)
        {
            return false;
        }

        var selected = recipients.UserIds.Contains(peerUserId) ||
                       (recipients.Contacts && isContact) ||
                       (recipients.NonContacts && !isContact) ||
                       (recipients.NewChats && isNewChat) ||
                       (recipients.ExistingChats && !isNewChat);

        return recipients.ExcludeSelected ? !selected : selected;
    }
}
