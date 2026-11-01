using MyTelegram.Messenger.Services.Business;

namespace MyTelegram.Messenger.Tests.Business;

/// <summary>
/// Feature: Telegram Business <a href="https://corefork.telegram.org/api/business#greeting-messages">greeting</a>
/// and <a href="https://corefork.telegram.org/api/business#away-messages">away</a> messages — who gets them
/// and when a greeting is due.
/// </summary>
public class BusinessAutoReplyTests
{
    private const long Peer = 42;

    private static BusinessRecipientsItem Recipients(bool existing = false, bool @new = false, bool contacts = false,
        bool nonContacts = false, bool exclude = false, params long[] users) =>
        new(existing, @new, contacts, nonContacts, exclude, users.ToList());

    [Fact]
    public void Categories_select_contacts_non_contacts_new_and_existing_chats()
    {
        BusinessRecipientsMatcher.Matches(Recipients(contacts: true), Peer, isContact: true, isNewChat: false)
            .ShouldBeTrue();
        BusinessRecipientsMatcher.Matches(Recipients(contacts: true), Peer, isContact: false, isNewChat: false)
            .ShouldBeFalse();
        BusinessRecipientsMatcher.Matches(Recipients(nonContacts: true), Peer, false, false).ShouldBeTrue();
        BusinessRecipientsMatcher.Matches(Recipients(@new: true), Peer, false, isNewChat: true).ShouldBeTrue();
        BusinessRecipientsMatcher.Matches(Recipients(@new: true), Peer, false, isNewChat: false).ShouldBeFalse();
        BusinessRecipientsMatcher.Matches(Recipients(existing: true), Peer, false, isNewChat: false).ShouldBeTrue();
    }

    [Fact]
    public void Listed_users_are_selected_and_exclude_selected_inverts_the_selection()
    {
        BusinessRecipientsMatcher.Matches(Recipients(users: Peer), Peer, false, false).ShouldBeTrue();
        BusinessRecipientsMatcher.Matches(Recipients(exclude: true, users: Peer), Peer, false, false)
            .ShouldBeFalse();
        // "Everybody except" with nobody named reaches everybody.
        BusinessRecipientsMatcher.Matches(Recipients(exclude: true), Peer, false, false).ShouldBeTrue();
        BusinessRecipientsMatcher.Matches(Recipients(contacts: true, exclude: true), Peer, isContact: true,
            isNewChat: false).ShouldBeFalse();
    }

    [Fact]
    public void Explicit_exclusions_always_win()
    {
        BusinessRecipientsMatcher.Matches(Recipients(nonContacts: true), Peer, false, false, [Peer])
            .ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, 1_000_000, 7, true)]
    [InlineData(1_000_000 - 7 * 86400, 1_000_000, 7, true)]
    [InlineData(1_000_000 - 7 * 86400 + 1, 1_000_000, 7, false)]
    [InlineData(1_000_000 - 60, 1_000_000, 28, false)]
    public void Greets_on_first_contact_or_after_the_inactivity_period(int? previous, int now, int days, bool greet)
    {
        BusinessAutoReplyService.ShouldGreet(previous, now, days).ShouldBe(greet);
    }
}
