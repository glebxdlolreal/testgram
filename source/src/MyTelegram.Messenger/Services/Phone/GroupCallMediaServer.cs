using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace MyTelegram.Messenger.Services.Phone;

/// <summary>The group-call media server (SFU) that carries a call's audio.</summary>
public interface IGroupCallMediaServer
{
    bool IsConfigured { get; }

    /// <summary>
    /// Allocates a connection from the client's join <paramref name="paramsJson"/>. <paramref name="key"/> names
    /// it within the call: the user id for the call itself, <c>{userId}:presentation</c> for a screen share.
    /// </summary>
    Task<GroupCallMediaJoinResult> JoinAsync(long callId, string key, string paramsJson, CancellationToken cancellationToken = default);

    Task LeaveAsync(long callId, string key, CancellationToken cancellationToken = default);

    Task EndAsync(long callId, CancellationToken cancellationToken = default);

    /// <summary>The SSRCs the SFU is forwarding in the call, or null when it cannot be asked.</summary>
    Task<IReadOnlySet<int>?> GetSourcesAsync(long callId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The SFU's answer to a join: the Jingle <c>transport</c> to connect to, the <c>video</c> section of the
/// connection params, and the video endpoint id other clients key this participant's video on.
/// </summary>
public sealed record GroupCallMediaJoinResult(JsonObject Transport, JsonObject? Video, string? Endpoint);

/// <summary>The client's join parameters cannot form a connection (the SFU answered 400).</summary>
public sealed class GroupCallJoinParamsInvalidException(string message) : Exception(message);

public sealed class GroupCallMediaServer(
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<GroupCallMediaServer> logger)
    : IGroupCallMediaServer, ISingletonDependency
{
    private static readonly HttpClient Http = new();

    private string? BaseUrl => options.CurrentValue.GroupCallSfu?.Url?.TrimEnd('/');

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);

    public async Task<GroupCallMediaJoinResult> JoinAsync(long callId, string key, string paramsJson, CancellationToken cancellationToken = default)
    {
        using var timeout = Deadline(cancellationToken);
        using var response = await Http.PostAsJsonAsync(
            $"{BaseUrl}/v1/calls/{callId}/participants/{Uri.EscapeDataString(key)}",
            new { @params = paramsJson },
            timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        if ((int)response.StatusCode == 400)
        {
            throw new GroupCallJoinParamsInvalidException(body);
        }

        response.EnsureSuccessStatusCode();
        var answer = JsonNode.Parse(body) as JsonObject;
        if (answer?["transport"] is not JsonObject transport)
        {
            throw new InvalidOperationException($"SFU join answer has no transport: {body}");
        }

        return new GroupCallMediaJoinResult(
            (JsonObject)transport.DeepClone(),
            answer["video"]?.DeepClone() as JsonObject,
            answer["endpoint"]?.GetValue<string>());
    }

    public async Task LeaveAsync(long callId, string key, CancellationToken cancellationToken = default)
    {
        await SendDeleteAsync($"{BaseUrl}/v1/calls/{callId}/participants/{Uri.EscapeDataString(key)}", cancellationToken);
    }

    public async Task EndAsync(long callId, CancellationToken cancellationToken = default)
    {
        await SendDeleteAsync($"{BaseUrl}/v1/calls/{callId}", cancellationToken);
    }

    public async Task<IReadOnlySet<int>?> GetSourcesAsync(long callId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        try
        {
            using var timeout = Deadline(cancellationToken);
            var answer = await Http.GetFromJsonAsync<JsonElement>($"{BaseUrl}/v1/calls/{callId}/sources", timeout.Token);
            return answer.GetProperty("sources").EnumerateArray().Select(source => source.GetInt32()).ToHashSet();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            logger.LogWarning(ex, "Could not ask the SFU which sources call {CallId} forwards", callId);
            return null;
        }
    }

    /// <summary>
    /// Releasing is best effort: the participant is gone from the call either way, and the SFU drops a
    /// connection that stops answering ICE on its own.
    /// </summary>
    private async Task SendDeleteAsync(string url, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return;
        }

        try
        {
            using var timeout = Deadline(cancellationToken);
            using var response = await Http.DeleteAsync(url, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "SFU did not take {Url}", url);
        }
    }

    private CancellationTokenSource Deadline(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.GroupCallSfu?.TimeoutSeconds ?? 5)));
        return cts;
    }
}
