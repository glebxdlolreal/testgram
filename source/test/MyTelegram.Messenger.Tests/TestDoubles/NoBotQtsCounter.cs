using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Tests.TestDoubles;

/// <summary>
/// An <see cref="IBotQtsCounter"/> for an account that never received a qts update from a bot, so
/// <c>updates.getDifference</c> keeps reporting the secret-chat qts alone.
/// </summary>
public sealed class NoBotQtsCounter : IBotQtsCounter
{
    public Task<int> NextAsync(long botId) =>
        throw new InvalidOperationException("No bot qts is allocated in this test.");

    public Task<int> GetAsync(long userId) => Task.FromResult(0);
}
