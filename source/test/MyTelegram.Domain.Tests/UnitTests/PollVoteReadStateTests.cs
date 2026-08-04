namespace MyTelegram.Domain.Tests.UnitTests;

public class PollVoteReadStateTests
{
    [Fact]
    public void ShouldCreateDistinctKeysForPeerScopes()
    {
        var peer = new Peer(PeerType.Channel, 100);

        PollVoteReadState.GetKey(peer).ShouldBe("read_poll_votes:5:100");
        PollVoteReadState.GetKey(peer, 300).ShouldBe("read_poll_votes:5:100:topic:300");
    }

    [Fact]
    public void ShouldNotCollideWithReactionReadState()
    {
        var peer = new Peer(PeerType.Channel, 100);

        PollVoteReadState.GetKey(peer).ShouldNotBe(ReactionReadState.GetKey(peer));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("bad", 0)]
    [InlineData("123", 123)]
    public void ShouldParseReadDate(string? value, int expected)
    {
        PollVoteReadState.ParseReadDate(value).ShouldBe(expected);
    }
}
