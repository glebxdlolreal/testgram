namespace MyTelegram.Messenger;

/// <summary>
/// Where the group-call media server (source/sfu) is reached: <c>App__GroupCallSfu__Url</c>, for example
/// <c>http://sfu:8090</c>. Without it, ordinary group calls get no connection parameters.
/// </summary>
public class GroupCallSfuConfig
{
    public string? Url { get; set; }

    public int TimeoutSeconds { get; set; } = 5;
}
