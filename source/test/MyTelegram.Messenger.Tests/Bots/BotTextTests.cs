using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Bots;

/// <summary>
/// Feature: the entities of @BotFather's messages.
///
/// <para>
/// Clients do not detect links, mentions or commands in incoming messages: real BotFather sends every
/// one of them as an entity (measured 2026-11-02), and a message without them shows /help as plain,
/// untappable text. Marked spans must keep their own entity and not get a detected one on top.
/// </para>
/// </summary>
public class BotTextTests
{
    [Fact]
    public void The_cancel_answer_carries_the_same_entities_as_real_BotFather()
    {
        var entities = new BotText("The command setname has been cancelled. Anything else I can do for you?\n\n" +
                                   "Send /help for a list of commands. To learn more about Telegram Bots, see https://core.telegram.org/bots")
            .Entities()!;

        // Real BotFather: [BotCommand 78/5, Url 147/30].
        entities.Select(Describe).ShouldBe(["BotCommand 78/5", "Url 147/30"]);
    }

    [Fact]
    public void A_mention_is_marked_and_a_trailing_full_stop_is_not_part_of_it()
    {
        var entities = new BotText("Here it is: testprodchanges @testprodchangesbot.\nWhat do you want to do with the bot?")
            .Entities()!;

        // Real BotFather: [Mention 28/19].
        entities.Select(Describe).ShouldBe(["Mention 28/19"]);
    }

    [Fact]
    public void Marked_spans_keep_their_entity_and_the_rest_is_detected()
    {
        var text = new BotText("Edit @testprodchangesbot info.\n\n").Bold("Name").Append(": test\n")
            .Bold("About").Append(": https://t.me/xietgchanges");

        // Real BotFather: [Mention 5/19, Bold 32/4, Bold 43/5, Url 50/25].
        text.Entities()!.Select(Describe).ShouldBe(["Mention 5/19", "Bold 32/4", "Bold 43/5", "Url 50/25"]);
    }

    [Fact]
    public void A_link_is_not_also_reported_as_a_command_or_a_url()
    {
        var entities = new BotText("Send ").Link("this /help page", "https://core.telegram.org/bots").Entities()!;

        entities.Select(Describe).ShouldBe(["TextUrl 5/15"]);
    }

    [Fact]
    public void Paths_inside_links_and_words_are_not_commands()
    {
        var entities = new BotText("Your web app link is t.me/mybot/app and/or core/x").Entities()!;

        entities.Select(Describe).ShouldBe(["Url 21/14"]);
    }

    [Fact]
    public void Plain_text_has_no_entities()
    {
        new BotText("Choose a bot from the list below:").Entities().ShouldBeNull();
    }

    private static string Describe(IMessageEntity entity) =>
        $"{entity.GetType().Name.Replace("TMessageEntity", "")} {entity.Offset}/{entity.Length}";
}
