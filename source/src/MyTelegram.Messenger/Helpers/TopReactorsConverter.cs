namespace MyTelegram.Messenger.Helpers;

/// <summary>
/// Converts the stored paid reaction leaderboard into its TL form.
/// See https://corefork.telegram.org/api/reactions#paid-reactions
/// </summary>
public static class TopReactorsConverter
{
    /// <summary>
    /// Builds the leaderboard for a single viewer. Anonymous reactors carry no peer_id so clients
    /// render them as "Anonymous"; only the viewer's own entry is flagged with "my".
    /// </summary>
    public static TVector<IMessageReactor>? ToTl(List<MessageReactor>? topReactors, long selfUserId)
    {
        if (topReactors == null || topReactors.Count == 0)
        {
            return null;
        }

        return new TVector<IMessageReactor>(topReactors.Select(r => (IMessageReactor)new TMessageReactor
        {
            Top = r.Top,
            My = r.SenderUserId == selfUserId,
            Anonymous = r.Anonymous,
            PeerId = r.Anonymous || r.PeerId == null ? null : r.PeerId.ToPeer(),
            Count = r.Count
        }));
    }
}
