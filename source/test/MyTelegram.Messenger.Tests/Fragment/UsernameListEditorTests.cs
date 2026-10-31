namespace MyTelegram.Messenger.Tests.Fragment;

/// <summary>
/// Feature: the <a href="https://corefork.telegram.org/api/fragment">usernames vector</a> edits behind
/// <c>*.toggleUsername</c>, <c>*.reorderUsernames</c>, <c>channels.deactivateAllUsernames</c> and the
/// basic username changing. Clients apply the same edits locally (tdlib <c>Usernames</c>), so the
/// server has to end up with exactly the list they expect.
/// </summary>
public class UsernameListEditorTests
{
    private static readonly UsernameItem Basic = new("basic", true, true);
    private static readonly UsernameItem First = new("first", false, true);
    private static readonly UsernameItem Second = new("second", false, true);
    private static readonly UsernameItem Disabled = new("disabled", false, false);

    [Fact]
    public void Disabling_a_collectible_moves_it_to_the_front_of_the_disabled_ones()
    {
        var result = UsernameListEditor.Toggle([Basic, First, Second, Disabled], "FIRST", false, forBot: false)!;

        result.ShouldBe([Basic, Second, First with { Active = false }, Disabled]);
    }

    [Fact]
    public void Enabling_a_collectible_appends_it_to_the_active_ones()
    {
        var result = UsernameListEditor.Toggle([Basic, First, Disabled], "disabled", true, forBot: false)!;

        result.ShouldBe([Basic, First, Disabled with { Active = true }]);
    }

    [Fact]
    public void Toggling_to_the_current_state_changes_nothing()
    {
        UsernameListEditor.Toggle([Basic, First], "first", true, forBot: false).ShouldBeNull();
    }

    [Fact]
    public void An_unknown_username_is_USERNAME_INVALID()
    {
        Should.Throw<RpcException>(() => UsernameListEditor.Toggle([Basic], "nobody", true, forBot: false))
            .RpcError.Message.ShouldBe("USERNAME_INVALID");
    }

    [Fact]
    public void A_user_cannot_disable_the_basic_username()
    {
        Should.Throw<RpcException>(() => UsernameListEditor.Toggle([Basic, First], "basic", false, forBot: false))
            .RpcError.Message.ShouldBe("USERNAME_INVALID");
    }

    [Fact]
    public void A_bot_may_disable_its_basic_username_while_another_stays_active()
    {
        var result = UsernameListEditor.Toggle([Basic, First], "basic", false, forBot: true)!;

        result.ShouldBe([First, Basic with { Active = false }]);
    }

    [Fact]
    public void A_bot_cannot_disable_its_only_active_username()
    {
        Should.Throw<RpcException>(() => UsernameListEditor.Toggle([Basic, Disabled], "basic", false, forBot: true))
            .RpcError.Message.ShouldBe("USERNAME_INVALID");
    }

    [Fact]
    public void A_bot_losing_its_last_active_collectible_gets_the_basic_username_back()
    {
        var result = UsernameListEditor.Toggle([First, Basic with { Active = false }], "first", false, forBot: true)!;

        result.ShouldBe([Basic, First with { Active = false }]);
    }

    [Fact]
    public void More_than_ten_active_usernames_is_USERNAMES_ACTIVE_TOO_MUCH()
    {
        var list = Enumerable.Range(0, 10).Select(i => new UsernameItem($"name{i}", i == 0, true))
            .Append(Disabled).ToList();

        Should.Throw<RpcException>(() => UsernameListEditor.Toggle(list, "disabled", true, forBot: false))
            .RpcError.Message.ShouldBe("USERNAMES_ACTIVE_TOO_MUCH");
    }

    [Fact]
    public void Reorder_puts_the_active_usernames_in_the_requested_order_and_keeps_the_disabled_ones()
    {
        var result = UsernameListEditor.Reorder([Basic, First, Second, Disabled], ["Second", "basic", "first"])!;

        result.ShouldBe([Second, Basic, First, Disabled]);
    }

    [Fact]
    public void Reorder_to_the_same_order_changes_nothing()
    {
        UsernameListEditor.Reorder([Basic, First, Disabled], ["basic", "first"]).ShouldBeNull();
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("basic,basic")]
    [InlineData("basic,first,disabled")]
    [InlineData("basic,nobody")]
    public void Reorder_must_name_every_active_username_exactly_once(string order)
    {
        Should.Throw<RpcException>(() => UsernameListEditor.Reorder([Basic, First, Disabled], order.Split(',')))
            .RpcError.Message.ShouldBe("ORDER_INVALID");
    }

    [Fact]
    public void DeactivateAll_keeps_the_basic_username_and_disables_every_collectible()
    {
        var result = UsernameListEditor.DeactivateAll([First, Basic, Second, Disabled])!;

        result.ShouldBe([Basic, First with { Active = false }, Second with { Active = false }, Disabled]);
    }

    [Fact]
    public void DeactivateAll_without_active_collectibles_changes_nothing()
    {
        UsernameListEditor.DeactivateAll([Basic, Disabled]).ShouldBeNull();
    }

    [Fact]
    public void A_new_basic_username_replaces_the_editable_entry_in_place()
    {
        UsernameListEditor.ChangeEditable([First, Basic, Disabled], "renamed")
            .ShouldBe([First, Basic with { Username = "renamed" }, Disabled]);
    }

    [Fact]
    public void Clearing_the_basic_username_removes_the_editable_entry()
    {
        UsernameListEditor.ChangeEditable([Basic, First], null).ShouldBe([First]);
    }

    [Fact]
    public void A_basic_username_set_after_clearing_goes_first()
    {
        UsernameListEditor.ChangeEditable([First], "fresh").ShouldBe([new UsernameItem("fresh", true, true), First]);
    }

    [Fact]
    public void A_peer_without_a_vector_keeps_none()
    {
        UsernameListEditor.ChangeEditable(null, "fresh").ShouldBeNull();
        UsernameListEditor.ChangeEditable([], "fresh").ShouldBeEmpty();
    }

    [Fact]
    public void A_new_collectible_starts_disabled_next_to_the_legacy_username()
    {
        UsernameListEditor.AddCollectible(null, "basic", "nft")
            .ShouldBe([Basic, new UsernameItem("nft", false, false)]);
    }

    [Fact]
    public void Attaching_a_collectible_twice_changes_nothing()
    {
        UsernameListEditor.AddCollectible([Basic, First], "basic", "FIRST").ShouldBeNull();
    }

    [Fact]
    public void Detaching_a_collectible_removes_it_but_never_the_basic_username()
    {
        UsernameListEditor.RemoveCollectible([Basic, First], "basic", "first").ShouldBe([Basic]);
        UsernameListEditor.RemoveCollectible([Basic, First], "basic", "basic").ShouldBeNull();
    }
}
