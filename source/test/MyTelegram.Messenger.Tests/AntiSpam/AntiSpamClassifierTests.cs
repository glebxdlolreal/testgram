using Microsoft.Extensions.Options;
using MyTelegram.Messenger.Services.AntiSpam;

namespace MyTelegram.Messenger.Tests.AntiSpam;

public class AntiSpamClassifierTests
{
    private const string Text = "This is a sufficiently long repeated promotion https://example.org/sale";
    private static AntiSpamClassifier Create(AntiSpamOptions? options = null) => new(Microsoft.Extensions.Options.Options.Create(options ?? new()));

    [Fact]
    public void Requires_four_distinct_messages_and_never_a_single_link()
    {
        var c = Create(); var hash = c.Fingerprint(Text)!;
        Assert.False(c.IsSpam(hash, [(1, 100, hash)], 100));
        Assert.False(c.IsSpam(hash, Enumerable.Repeat((1, 100L, hash), 10), 100));
        Assert.True(c.IsSpam(hash, Enumerable.Range(1, 4).Select(id => (id, 100L, hash)), 100));
    }

    [Fact]
    public void Rejects_short_nonlink_oversized_and_disabled_content()
    {
        Assert.Null(Create().Fingerprint("https://example.org"));
        Assert.Null(Create().Fingerprint(new string('a', 100)));
        Assert.Null(Create().Fingerprint(Text + new string('x', 4096)));
        Assert.Null(Create(new() { Enabled = false }).Fingerprint(Text));
    }

    [Fact]
    public void Normalizes_whitespace_but_not_url_case_or_distinct_suffixes()
    {
        var c = Create();
        Assert.Equal(c.Fingerprint(Text), c.Fingerprint(Text.Replace(" ", " \n ")));
        Assert.NotEqual(c.Fingerprint(Text), c.Fingerprint(Text + "2"));
        Assert.NotEqual(c.Fingerprint(Text), c.Fingerprint(Text.Replace("/sale", "/SALE")));
    }

    [Fact]
    public void Ignores_expired_future_and_different_fingerprints()
    {
        var c = Create(); var hash = c.Fingerprint(Text)!;
        Assert.False(c.IsSpam(hash, [(1, 100, hash), (2, 100, hash), (3, 100, hash), (4, -21, hash)], 100));
        Assert.False(c.IsSpam(hash, [(1, 100, hash), (2, 100, hash), (3, 100, hash), (4, 101, hash)], 100));
        Assert.False(c.IsSpam(hash, [(1, 100, hash), (2, 100, hash), (3, 100, hash), (4, 100, "other")], 100));
    }

    [Fact]
    public void Configuration_cannot_lower_conservative_safety_floor()
    {
        var c = Create(new() { RepeatThreshold = 1, MinimumTextLength = 0 });
        Assert.Equal(4, c.Threshold);
        Assert.Null(c.Fingerprint("https://x.org"));
    }
}
