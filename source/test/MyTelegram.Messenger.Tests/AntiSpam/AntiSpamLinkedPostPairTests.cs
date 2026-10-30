using MyTelegram.Messenger.Services.AntiSpam;

namespace MyTelegram.Messenger.Tests.AntiSpam;

public sealed class AntiSpamLinkedPostPairTests
{
    [Fact]
    public void Historical_pair_does_not_depend_on_current_channel_link()
    {
        var owner = new Peer(PeerType.Channel, 100);
        var item = new MessageItem(owner, owner, new Peer(PeerType.User, 200), 200, 1, "post", 1, 1, true)
        {
            FwdHeader = new MessageFwdHeader { ForwardFromLinkedChannel = true,
                SavedFromPeer = new Peer(PeerType.Channel, 300), SavedFromMsgId = 7 }
        };
        Assert.True(AntiSpamLinkedPostPairValidator.Matches(item, 300, 7));
        Assert.False(AntiSpamLinkedPostPairValidator.Matches(item, 400, 7));
        Assert.False(AntiSpamLinkedPostPairValidator.Matches(item, 300, 8));
    }

    [Fact]
    public void Missing_root_or_nonlinked_forward_is_not_pair_evidence()
    {
        Assert.False(AntiSpamLinkedPostPairValidator.Matches(null, 300, 7));
        var owner = new Peer(PeerType.Channel, 100);
        var item = new MessageItem(owner, owner, new Peer(PeerType.User, 200), 200, 1, "post", 1, 1, true)
        {
            FwdHeader = new MessageFwdHeader { ForwardFromLinkedChannel = false,
                SavedFromPeer = new Peer(PeerType.Channel, 300), SavedFromMsgId = 7 }
        };
        Assert.False(AntiSpamLinkedPostPairValidator.Matches(item, 300, 7));
    }
}
