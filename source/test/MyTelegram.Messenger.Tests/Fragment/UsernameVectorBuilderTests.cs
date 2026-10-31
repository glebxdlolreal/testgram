using MyTelegram.Converters.Mappers;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Fragment;

/// <summary>
/// Feature: the <c>username</c> / <c>usernames</c> pair of <c>user</c> and <c>channel</c>.
/// <para>
/// Both used to be set at once. tdlib treats that as a server error and drops every username of the
/// peer, so Telegram X and Unigram showed none for anybody owning a Fragment collectible. And disabled
/// collectibles, which only their owner may see, were handed to everyone.
/// </para>
/// </summary>
public class UsernameVectorBuilderTests
{
    private static readonly List<UsernameInfo> WithCollectibles =
    [
        new() { Username = "basic", Editable = true, Active = true },
        new() { Username = "nft", Editable = false, Active = true },
        new() { Username = "hidden", Editable = false, Active = false }
    ];

    [Fact]
    public void With_a_collectible_the_username_field_is_unset_and_the_vector_carries_everything()
    {
        var (username, usernames) = UsernameVectorBuilder.Build(WithCollectibles, "basic", includeDisabled: true);

        username.ShouldBeNull();
        usernames!.Cast<TUsername>().Select(p => (p.Username, p.Editable, p.Active))
            .ShouldBe([("basic", true, true), ("nft", false, true), ("hidden", false, false)]);
    }

    [Fact]
    public void Others_do_not_see_disabled_collectibles()
    {
        var (username, usernames) = UsernameVectorBuilder.Build(WithCollectibles, "basic", includeDisabled: false);

        username.ShouldBeNull();
        usernames!.Cast<TUsername>().Select(p => p.Username).ShouldBe(["basic", "nft"]);
    }

    [Fact]
    public void When_only_the_basic_username_is_visible_it_goes_in_the_plain_field()
    {
        List<UsernameInfo> stored =
        [
            new() { Username = "basic", Editable = true, Active = true },
            new() { Username = "hidden", Editable = false, Active = false }
        ];

        UsernameVectorBuilder.Build(stored, "basic", includeDisabled: false).ShouldBe(("basic", null));
    }

    [Fact]
    public void Without_a_vector_the_legacy_username_is_used()
    {
        UsernameVectorBuilder.Build(null, "legacy", includeDisabled: false).ShouldBe(("legacy", null));
        UsernameVectorBuilder.Build(null, null, includeDisabled: true).ShouldBe((null, null));
    }

    [Fact]
    public void Nothing_is_visible_when_every_collectible_is_disabled_and_there_is_no_basic_username()
    {
        List<UsernameInfo> stored = [new() { Username = "hidden", Editable = false, Active = false }];

        UsernameVectorBuilder.Build(stored, null, includeDisabled: false).ShouldBe((null, null));
    }
}
