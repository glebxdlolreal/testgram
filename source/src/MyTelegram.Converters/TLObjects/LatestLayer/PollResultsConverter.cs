namespace MyTelegram.Converters.TLObjects.LatestLayer;

internal sealed class PollResultsConverter(IObjectMapper objectMapper) : IPollResultsConverter, ITransientDependency
{

    public int Layer => Layers.LayerLatest;

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
