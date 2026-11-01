namespace MyTelegram.Services.Extensions;

/// <summary>
/// Marks the code running inside <c>invokeWithBusinessConnection</c>: the inner request executes as the
/// business account, and everything it publishes carries the bot in <see cref="RequestInfo.BusinessBotId"/>.
/// See https://corefork.telegram.org/api/bots/connected-business-bots
/// </summary>
public static class BusinessConnectionScope
{
    private static readonly AsyncLocal<long?> Current = new();

    public static long? CurrentBotId => Current.Value;

    public static IDisposable Enter(long botId)
    {
        var previous = Current.Value;
        Current.Value = botId;
        return new Restore(previous);
    }

    private sealed class Restore(long? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
