using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.EmojiCategories;

/// <summary>
/// Unit tests for the <a href="https://corefork.telegram.org/api/emoji-categories">emoji category</a>
/// cache hash served by the four <c>messages.getEmoji*Groups</c> methods.
///
/// The hash previously came from <c>max(Version)</c> over the stored category documents, which cannot
/// fall: deleting a category left every client pinned to a stale <c>emojiGroupsNotModified</c> forever.
/// These tests pin the replacement down — the hash is derived from the content clients actually render,
/// so any change to it invalidates the cached copy.
/// </summary>
public class EmojiGroupsHashTests
{
    private static TEmojiGroup Group(string title, long iconEmojiId, params string[] emoticons) =>
        new()
        {
            Title = title,
            IconEmojiId = iconEmojiId,
            Emoticons = new TVector<string>(emoticons.ToList())
        };

    [Fact]
    public void Empty_category_list_hashes_to_zero()
    {
        // Zero is the client's "I have no cached copy" sentinel, so an empty list must report it
        // rather than a real hash the client could match against.
        EmojiGroupsAppService.ComputeHash([]).ShouldBe(0);
    }

    [Fact]
    public void Same_categories_hash_identically()
    {
        var first = EmojiGroupsAppService.ComputeHash([Group("Animals", 111, "🐈", "🦊")]);
        var second = EmojiGroupsAppService.ComputeHash([Group("Animals", 111, "🐈", "🦊")]);

        first.ShouldBe(second);
    }

    [Fact]
    public void Hash_is_never_zero_for_a_non_empty_list()
    {
        // A real category set colliding with the sentinel would be served as notModified against a
        // client that has nothing cached.
        EmojiGroupsAppService.ComputeHash([Group("Animals", 111, "🐈")]).ShouldNotBe(0);
    }

    [Fact]
    public void Hash_is_positive_so_it_survives_the_int_field()
    {
        // messages.emojiGroups.hash is a signed int; a negative value round-trips fine but makes the
        // stored/compared values needlessly awkward, so the fold keeps it in the positive range.
        EmojiGroupsAppService.ComputeHash([Group("Animals", 111, "🐈", "🦊", "🐻")]).ShouldBePositive();
    }

    [Fact]
    public void Removing_a_category_changes_the_hash()
    {
        // The defect the content hash exists to fix: with max(Version) both of these produced the
        // same number, so a deleted category never reached clients.
        var both = EmojiGroupsAppService.ComputeHash([
            Group("Animals", 111, "🐈"),
            Group("Food", 222, "🍕")
        ]);
        var onlyOne = EmojiGroupsAppService.ComputeHash([Group("Animals", 111, "🐈")]);

        both.ShouldNotBe(onlyOne);
    }

    [Fact]
    public void Renaming_a_category_changes_the_hash()
    {
        var before = EmojiGroupsAppService.ComputeHash([Group("Animals", 111, "🐈")]);
        var after = EmojiGroupsAppService.ComputeHash([Group("Creatures", 111, "🐈")]);

        before.ShouldNotBe(after);
    }

    [Fact]
    public void Changing_the_icon_changes_the_hash()
    {
        var before = EmojiGroupsAppService.ComputeHash([Group("Animals", 111, "🐈")]);
        var after = EmojiGroupsAppService.ComputeHash([Group("Animals", 222, "🐈")]);

        before.ShouldNotBe(after);
    }

    [Fact]
    public void Changing_the_emoticons_changes_the_hash()
    {
        var before = EmojiGroupsAppService.ComputeHash([Group("Animals", 111, "🐈")]);
        var after = EmojiGroupsAppService.ComputeHash([Group("Animals", 111, "🐈", "🦊")]);

        before.ShouldNotBe(after);
    }

    [Fact]
    public void Reordering_categories_changes_the_hash()
    {
        // Order is what the client renders in the category bar, so it is part of the cached state.
        var forward = EmojiGroupsAppService.ComputeHash([
            Group("Animals", 111, "🐈"),
            Group("Food", 222, "🍕")
        ]);
        var reversed = EmojiGroupsAppService.ComputeHash([
            Group("Food", 222, "🍕"),
            Group("Animals", 111, "🐈")
        ]);

        forward.ShouldNotBe(reversed);
    }

    [Fact]
    public void Category_kind_is_part_of_the_hash()
    {
        // emojiGroup / emojiGroupGreeting / emojiGroupPremium drive completely different client
        // behaviour, so swapping the constructor must not reuse a cached copy.
        var plain = EmojiGroupsAppService.ComputeHash([Group("Greeting", 111, "👋")]);
        var greeting = EmojiGroupsAppService.ComputeHash([
            new TEmojiGroupGreeting
            {
                Title = "Greeting",
                IconEmojiId = 111,
                Emoticons = new TVector<string>(["👋"])
            }
        ]);

        plain.ShouldNotBe(greeting);
    }

    [Fact]
    public void Premium_categories_hash_on_title_and_icon()
    {
        // emojiGroupPremium carries no emoticons; only its title and icon can change.
        var before = EmojiGroupsAppService.ComputeHash([
            new TEmojiGroupPremium { Title = "Premium", IconEmojiId = 111 }
        ]);
        var after = EmojiGroupsAppService.ComputeHash([
            new TEmojiGroupPremium { Title = "Premium", IconEmojiId = 222 }
        ]);

        before.ShouldNotBe(after);
    }

    [Fact]
    public void Field_boundaries_are_respected()
    {
        // Without a separator between fields, ("ab", "c") and ("a", "bc") would collapse to the same
        // byte stream and one edit would be invisible to the client.
        var first = EmojiGroupsAppService.ComputeHash([Group("ab", 1, "c")]);
        var second = EmojiGroupsAppService.ComputeHash([Group("a", 1, "bc")]);

        first.ShouldNotBe(second);
    }
}
