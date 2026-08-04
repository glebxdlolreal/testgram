using System.Globalization;

namespace MyTelegram;

/// <summary>
/// Tracks when a user last read the votes cast on their own polls, so
/// <c>messages.getUnreadPollVotes</c> and <c>unread_poll_votes_count</c> have a cutoff to compare
/// against. Mirrors <see cref="ReactionReadState"/>: the cutoff is stored as a user config value
/// rather than a per-vote read flag.
/// </summary>
public static class PollVoteReadState
{
    private const string KeyPrefix = "read_poll_votes";

    public static string GetKey(Peer peer, int? topMsgId = null)
    {
        if (topMsgId.HasValue)
        {
            return $"{KeyPrefix}:{(int)peer.PeerType}:{peer.PeerId}:topic:{topMsgId.Value}";
        }

        return $"{KeyPrefix}:{(int)peer.PeerType}:{peer.PeerId}";
    }

    public static int ParseReadDate(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var date) ? date : 0;
    }
}
