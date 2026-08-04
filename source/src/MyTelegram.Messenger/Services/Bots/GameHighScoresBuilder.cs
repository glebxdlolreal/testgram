using MyTelegram.Messenger.Converters.ConverterServices;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// Builds the messages.highScores response shared by the chat and inline leaderboard methods.
/// </summary>
public static class GameHighScoresBuilder
{
    /// <summary>
    /// Assigns 1-based positions to an already-sorted score list and attaches the matching users,
    /// loaded in a single batch query.
    /// </summary>
    public static async Task<MyTelegram.Schema.Messages.IHighScores> BuildAsync(
        IRequestInput input,
        List<(long UserId, int Score)> scores,
        IQueryProcessor queryProcessor,
        IUserConverterService userConverterService)
    {
        var highScores = new TVector<IHighScore>();
        var position = 1;
        foreach (var (userId, score) in scores)
        {
            highScores.Add(new THighScore
            {
                Pos = position++,
                UserId = userId,
                Score = score
            });
        }

        var users = new TVector<IUser>();
        if (scores.Count > 0)
        {
            var userReadModels = await queryProcessor.ProcessAsync(
                new GetUsersByUserIdListQuery(scores.Select(s => s.UserId).Distinct().ToList()));

            foreach (var userReadModel in userReadModels)
            {
                users.Add(userConverterService.ToUser(input, userReadModel, layer: input.Layer));
            }
        }

        return new MyTelegram.Schema.Messages.THighScores
        {
            Scores = highScores,
            Users = users
        };
    }
}
