using MyTelegram.Messenger.Handlers.LatestLayer.Account;
using MyTelegram.Messenger.Services.Business;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Business;

/// <summary>
/// Feature: <a href="https://corefork.telegram.org/api/business#business-chat-links">business chat links</a>
/// — <c>t.me/m/slug</c> links that open a chat with a pre-filled message.
/// </summary>
public class BusinessChatLinkTests
{
    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("https://t.me/m/abc", "abc")]
    [InlineData("t.me/m/abc/", "abc")]
    [InlineData("  ", "")]
    public void Edit_and_delete_accept_the_slug_or_the_whole_link(string input, string slug)
    {
        new BusinessChatLinkStore(null!).ParseSlug(input).ShouldBe(slug);
    }

    [Fact]
    public void Links_use_the_configured_domain()
    {
        BusinessChatLinkHelper.ToLink("https://t.me", "abc").ShouldBe("https://t.me/m/abc");
        BusinessChatLinkHelper.ToLink("https://example.org/", "abc").ShouldBe("https://example.org/m/abc");
    }

    [RequiresMongoDbFact]
    public async Task Entities_survive_storage_and_views_count_only_on_resolve()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new BusinessChatLinkStore(mongo.Database);
        var entities = new TVector<IMessageEntity>(new TMessageEntityBold { Offset = 0, Length = 5 });

        var created = await store.CreateAsync(1, "Shop", "Hello there", entities);
        (await store.GetByUserAsync(1)).Single().Entities!.Single().ShouldBeOfType<TMessageEntityBold>()
            .Length.ShouldBe(5);

        (await store.ResolveAsync(created.Slug, false))!.Views.ShouldBe(0);
        (await store.ResolveAsync(created.Slug, true))!.Views.ShouldBe(1);
        (await store.DeleteAsync(2, created.Slug)).ShouldBeFalse();
        (await store.DeleteAsync(1, created.Slug)).ShouldBeTrue();
        (await store.GetAsync(created.Slug)).ShouldBeNull();
    }
}
