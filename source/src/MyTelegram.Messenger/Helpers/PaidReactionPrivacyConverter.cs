using MyTelegram.Messenger.Services.Interfaces;

namespace MyTelegram.Messenger.Helpers;

/// <summary>
/// Translates between the stored paid reaction privacy and its TL representation.
/// See https://corefork.telegram.org/api/reactions#paid-reactions
/// </summary>
public static class PaidReactionPrivacyConverter
{
    public static IPaidReactionPrivacy ToTl(PaidReactionPrivacySetting setting, IRequestInput input,
        IAccessHashHelper2 accessHashHelper)
    {
        switch (setting.Type)
        {
            case PaidReactionPrivacyType.Anonymous:
                return new TPaidReactionPrivacyAnonymous();

            case PaidReactionPrivacyType.Peer when setting.PeerId != 0:
                return new TPaidReactionPrivacyPeer
                {
                    Peer = new TInputPeerChannel
                    {
                        ChannelId = setting.PeerId,
                        AccessHash = accessHashHelper.GenerateAccessHash(input.UserId, input.AccessHashKeyId,
                            setting.PeerId, AccessHashType.Channel)
                    }
                };

            default:
                return new TPaidReactionPrivacyDefault();
        }
    }

    public static PaidReactionPrivacySetting FromTl(IPaidReactionPrivacy? privacy, IPeerHelper peerHelper, long selfUserId)
    {
        switch (privacy)
        {
            case TPaidReactionPrivacyAnonymous:
                return new PaidReactionPrivacySetting(PaidReactionPrivacyType.Anonymous);

            case TPaidReactionPrivacyPeer peerPrivacy:
            {
                var peer = peerHelper.GetPeer(peerPrivacy.Peer, selfUserId);
                // Reacting "as" yourself is just the public default.
                return peer.PeerType == PeerType.Channel
                    ? new PaidReactionPrivacySetting(PaidReactionPrivacyType.Peer, peer.PeerId)
                    : new PaidReactionPrivacySetting(PaidReactionPrivacyType.Default);
            }

            default:
                return new PaidReactionPrivacySetting(PaidReactionPrivacyType.Default);
        }
    }
}
