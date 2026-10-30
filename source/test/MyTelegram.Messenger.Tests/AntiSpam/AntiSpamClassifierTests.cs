using MyTelegram.Messenger.Services.AntiSpam;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.AntiSpam;

public class AntiSpamClassifierTests
{
    private const string Text = "This is a sufficiently long repeated promotion https://example.org/sale";
    private static AntiSpamClassifier Create(AntiSpamOptions? options = null) => new(Microsoft.Extensions.Options.Options.Create(options ?? new()));
    private static AntiSpamKeys Keys(string text, IEnumerable<IMessageEntity>? entities = null) => Create().Analyze(text, entities)!.Value;

    [Fact]
    public void Rejects_short_nonlink_oversized_and_disabled_content()
    {
        Create().Analyze("https://example.org", null).ShouldBeNull();
        Create().Analyze(new string('a', 100), null).ShouldBeNull();
        Create().Analyze("A long enough message with no link of any kind, just ordinary chatter.", null).ShouldBeNull();
        Create().Analyze(Text + new string('x', 4096), null).ShouldBeNull();
        Create(new() { Enabled = false }).Analyze(Text, null).ShouldBeNull();
    }

    [Fact]
    public void Configuration_cannot_lower_conservative_safety_floor()
    {
        var c = Create(new() { RepeatThreshold = 1, LooseRepeatThreshold = 2, MinimumTextLength = 0 });
        c.Threshold.ShouldBe(4);
        c.LooseThreshold.ShouldBe(4);
        c.Analyze("https://x.org", null).ShouldBeNull();
        Create(new() { RepeatThreshold = 100 }).Threshold.ShouldBe(AntiSpamBurstTracker.RingSize);
    }

    [Theory]
    [InlineData("This  is a sufficiently\n\nlong   repeated promotion https://example.org/sale  ")]
    [InlineData("\u200BThis is a suffi\u200Dciently long repeated promotion\uFEFF https://example.org/sale")]
    [InlineData("This is a sufficiently long repeated promotion \U0001F389\U0001F525 https://example.org/sale \u2705\uFE0F")]
    [InlineData("\uFF34\uFF48\uFF49\uFF53 is a sufficiently long repeated promotion https://example.org/sale")] // fullwidth
    [InlineData("\U0001D413\U0001D421\U0001D422\U0001D42C is a sufficiently long repeated promotion https://example.org/sale")] // math bold
    [InlineData("This is a sufficiently long repeated promotion https\uFF1A//example\uFF0Eorg/sale")]
    [InlineData("This\u00A0is a sufficiently\u2028long repeated pro\u00ADmotion https://example.org/sale")]
    public void Strict_key_ignores_spacing_invisibles_and_emoji(string variant) =>
        Keys(variant).Strict.ShouldBe(Keys(Text).Strict);

    [Theory]
    [InlineData("THIS is a sufficiently long repeated promotion https://example.org/sale")]
    [InlineData("This is a sufficiently long repeated promotion!!! https://example.org/sale")]
    [InlineData("This, is a sufficiently-long repeated promotion: https://example.org/sale.")]
    [InlineData("Th\u0456s \u0456s \u0430 suff\u0456c\u0456ently long repe\u0430ted promot\u0456on https://example.org/sale")] // Cyrillic look-alikes
    public void Case_punctuation_and_lookalikes_change_only_the_strict_key(string variant)
    {
        Keys(variant).Strict.ShouldNotBe(Keys(Text).Strict);
        Keys(variant).Loose.ShouldBe(Keys(Text).Loose);
    }

    [Fact]
    public void Different_texts_have_different_keys()
    {
        var other = Keys(Text.Replace("promotion", "invitation"));
        other.Strict.ShouldNotBe(Keys(Text).Strict);
        other.Loose.ShouldNotBe(Keys(Text).Loose);
        Keys(Text + " 2").Loose.ShouldNotBe(Keys(Text).Loose);
    }

    [Theory]
    [InlineData("Join our channel for daily giveaways and big prizes: http:// example.org")]
    [InlineData("Join our channel for daily giveaways and big prizes: hxxps[:]//example.org")]
    [InlineData("Join our channel for daily giveaways and big prizes: t.me/joinchat_prizes")]
    [InlineData("Join our channel for daily giveaways and big prizes: t . me/prizes")]
    [InlineData("Join our channel for daily giveaways and big prizes: @prizes_channel")]
    [InlineData("Join our channel for daily giveaways and big prizes at example.com today")]
    [InlineData("Join our channel for daily giveaways and big prizes at example[.]com today")]
    [InlineData("Join our channel for daily giveaways and big prizes at example(dot)ru today")]
    [InlineData("Join our channel for daily giveaways and big prizes at example . com today")]
    [InlineData("Join our channel for daily giveaways and big prizes at www.example")]
    [InlineData("Заходите в наш канал, ежедневные розыгрыши и большие призы: призы.рф")]
    public void Links_in_any_form_put_the_message_under_scrutiny(string text) =>
        Create().Analyze(text, null).ShouldNotBeNull();

    [Theory]
    [InlineData("Please read README.md and run the build.sh script before you ask again.")]
    [InlineData("We met at 3.14 pm, e.g. after lunch, i.e. later than planned. Fine.")]
    [InlineData("Hello. It is a long and ordinary message. So we wait. Do it now.")]
    [InlineData("Write to me by email: someone at home, I will answer tomorrow morning.")]
    public void Ordinary_prose_is_not_mistaken_for_a_link(string text) =>
        Create().Analyze(text, null).ShouldBeNull();

    [Fact]
    public void Link_only_in_entities_counts()
    {
        const string text = "A long enough message that hides its link behind ordinary looking words";
        Create().Analyze(text, null).ShouldBeNull();
        Create().Analyze(text, [new TMessageEntityTextUrl { Offset = 0, Length = 6, Url = "https://spam.example" }]).ShouldNotBeNull();
        Create().Analyze(text, [new TMessageEntityUrl { Offset = 0, Length = 6 }]).ShouldNotBeNull();
        Create().Analyze(text, [new TMessageEntityMention { Offset = 0, Length = 6 }]).ShouldNotBeNull();
        Create().Analyze(text, [new TMessageEntityBold { Offset = 0, Length = 6 }]).ShouldBeNull();
    }
}
