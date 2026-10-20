namespace MyTelegram.Messenger.Handlers.LatestLayer.Updates;

/// <summary>
/// Repairs updates replayed from the updates read model that were stored by older builds.
/// pollAnswerVoters carries voters and recent_voters under the same flag bit, but polls stored
/// before recent voters were tracked keep only voters, so serializing them for
/// updates.getDifference throws and the whole difference is never sent.
/// </summary>
internal static class StoredUpdateNormalizer
{
    public static IEnumerable<IUpdate> Normalize(IEnumerable<IUpdate> updates)
    {
        foreach (var update in updates)
        {
            if (update is TUpdateMessagePoll { Results: TPollResults { Results: { } results } })
            {
                foreach (var answer in results.OfType<TPollAnswerVoters>())
                {
                    if (answer.Voters.HasValue)
                    {
                        answer.RecentVoters ??= [];
                    }
                }
            }

            yield return update;
        }
    }
}
