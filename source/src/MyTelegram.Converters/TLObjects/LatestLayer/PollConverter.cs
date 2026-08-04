namespace MyTelegram.Converters.TLObjects.LatestLayer;

internal sealed class PollConverter(IObjectMapper objectMapper) : IPollConverter, ITransientDependency
{

    public int Layer => Layers.LayerLatest;

    public IPoll ToPoll(IPollReadModel readModel, long selfUserId = 0)
    {
        var poll = objectMapper.Map<IPollReadModel, TPoll>(readModel);

        // creator is "am I the one who made this poll", so it can only be decided per request.
        poll.Creator = selfUserId != 0 && readModel.CreatorUserId == selfUserId;

        return poll;
    }

    public IPollResults ToPollResults(IPollReadModel pollReadModel,
        IList<string>? chosenOptions,
        IReadOnlyCollection<long>? recentVoterPeerIds = null,
        long selfUserId = 0)
    {
        return PollResultsBuilder.Build(
            objectMapper.Map<IPollReadModel, TPollResults>(pollReadModel),
            pollReadModel,
            chosenOptions,
            recentVoterPeerIds,
            selfUserId);
    }
}
