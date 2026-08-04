using System.Text;

namespace MyTelegram.Converters;

/// <summary>
/// Computes the <c>poll.hash</c> value clients pass back in
/// <a href="https://corefork.telegram.org/method/messages.getPollResults">messages.getPollResults</a>
/// so the server can answer "nothing changed" without re-sending the full results.
/// </summary>
public static class PollHashHelper
{
    /// <summary>
    /// Derives a hash from everything a client would render: the tallies, the total, the
    /// closed state and the set of answers. Any change to those changes the hash.
    /// </summary>
    public static long ComputeHash(IPollReadModel pollReadModel)
    {
        var sb = new StringBuilder();
        sb.Append(pollReadModel.PollId);
        sb.Append('|');
        sb.Append(pollReadModel.TotalVoters);
        sb.Append('|');
        sb.Append(pollReadModel.Closed ? '1' : '0');

        // Answers are ordered by option id so the hash doesn't depend on storage order.
        if (pollReadModel.AnswerVoters != null)
        {
            foreach (var voter in pollReadModel.AnswerVoters.OrderBy(p => p.Option, StringComparer.Ordinal))
            {
                sb.Append('|');
                sb.Append(voter.Option);
                sb.Append(':');
                sb.Append(voter.Voters);
            }
        }
        else
        {
            foreach (var answer in pollReadModel.Answers.OrderBy(p => p.Option, StringComparer.Ordinal))
            {
                sb.Append('|');
                sb.Append(answer.Option);
                sb.Append(":0");
            }
        }

        // FNV-1a 64-bit: stable across processes and runtime versions, unlike string.GetHashCode.
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(sb.ToString()))
        {
            hash ^= b;
            hash *= prime;
        }

        // Keep it non-negative: clients treat 0 as "no hash known" and re-request anyway.
        return (long)(hash & 0x7FFFFFFFFFFFFFFF);
    }
}
