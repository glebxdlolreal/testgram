using System.Buffers;
using MyTelegram.Messenger.Handlers.LatestLayer.Updates;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Polls;

/// <summary>
/// pollAnswerVoters writes voters and recent_voters under the same flag bit. Poll updates stored in
/// the updates read model before recent voters were tracked have voters but no recent_voters, and
/// replaying one through updates.getDifference threw a NullReferenceException mid-serialization.
/// </summary>
public class StoredUpdateNormalizerTests
{
    private static TUpdateMessagePoll StoredPollUpdate() => new()
    {
        PollId = 1,
        Results = new TPollResults
        {
            Min = true,
            TotalVoters = 1,
            Results = new TVector<IPollAnswerVoters>(
                new TPollAnswerVoters { Option = "0"u8.ToArray(), Voters = 1 },
                new TPollAnswerVoters { Option = "1"u8.ToArray(), Voters = 0 })
        }
    };

    [Fact]
    public void A_stored_poll_update_without_recent_voters_serializes_after_normalization()
    {
        var update = StoredPollUpdate();
        Should.Throw<NullReferenceException>(() => update.Serialize(new ArrayBufferWriter<byte>()));

        var normalized = StoredUpdateNormalizer.Normalize([StoredPollUpdate()]).Single();

        Should.NotThrow(() => normalized.Serialize(new ArrayBufferWriter<byte>()));
        ((TPollResults)((TUpdateMessagePoll)normalized).Results).Results!
            .Cast<TPollAnswerVoters>().ShouldAllBe(x => x.RecentVoters != null && x.RecentVoters.Count == 0);
    }

    [Fact]
    public void Answers_without_a_tally_keep_the_flag_clear()
    {
        var update = new TUpdateMessagePoll
        {
            PollId = 1,
            Results = new TPollResults
            {
                Results = new TVector<IPollAnswerVoters>(new TPollAnswerVoters { Option = "0"u8.ToArray() })
            }
        };

        var answer = (TPollAnswerVoters)((TPollResults)((TUpdateMessagePoll)StoredUpdateNormalizer.Normalize([update]).Single())
            .Results).Results!.Single();

        answer.RecentVoters.ShouldBeNull();
    }
}
