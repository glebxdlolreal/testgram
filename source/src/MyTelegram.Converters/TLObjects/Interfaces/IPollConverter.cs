namespace MyTelegram.Converters.TLObjects.Interfaces;

public interface IPollConverter : ILayeredConverter
{
    /// <param name="selfUserId">Requesting user, used to set the <c>creator</c> flag.</param>
    IPoll ToPoll(IPollReadModel readModel, long selfUserId = 0);

    IPollResults ToPollResults(IPollReadModel pollReadModel,
        IList<string>? chosenOptions,
        IReadOnlyCollection<long>? recentVoterPeerIds = null,
        long selfUserId = 0);
}
