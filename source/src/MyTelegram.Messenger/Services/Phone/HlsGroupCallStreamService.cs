using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Phone;

public interface IHlsGroupCallStreamService
{
    Task<IReadOnlyList<HlsGroupCallStreamChannel>> GetChannelsAsync(GroupCallDocument groupCall);

    Task<byte[]> ReadPartAsync(GroupCallDocument groupCall, TInputGroupCallStream location);
}

public sealed class HlsGroupCallStreamService(
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    IGroupCallStreamPartMuxer muxer,
    ILogger<HlsGroupCallStreamService> logger)
    : IHlsGroupCallStreamService, ISingletonDependency
{
    private const int TelegramVideoStreamPartSignature = unchecked((int)0xa12e810d);
    private const int VideoChannel = 1;
    private const int AudioChannel = 0;
    private const int VideoScale = 0;
    private const string DefaultTelegramStreamContainer = "mp4";
    private const string MpegTsTelegramStreamContainer = "mpegts";
    private const int SegmentDurationMs = 1_000;
    // Clients start two seconds behind last_timestamp_ms, while HLS segment
    // timestamps can be offset from exact second boundaries.
    private const int InitialBackfillWindowMs = 3_000;
    private const int TimestampToleranceMs = 250;
    private static readonly TimeSpan ChildManifestCacheTtl = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MediaManifestCacheTtl = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan InitFragmentCacheTtl = TimeSpan.FromMinutes(5);
    // Every viewer asks for the same parts; building one runs ffmpeg.
    private static readonly TimeSpan PartCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HlsSessionCookieCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StartupManifestWaitTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StartupManifestRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(3) };

    private readonly ConcurrentDictionary<string, ChildManifestCacheEntry> _childManifestCache = new();
    private readonly ConcurrentDictionary<string, MediaManifestCacheEntry> _mediaManifestCache = new();
    private readonly ConcurrentDictionary<string, BytesCacheEntry> _initFragmentCache = new();
    private readonly ConcurrentDictionary<string, HlsSessionCookieCacheEntry> _hlsSessionCookieCache = new();
    private readonly ConcurrentDictionary<string, (Lazy<Task<byte[]>> Part, DateTimeOffset ExpiresAt)> _partCache = new();

    public async Task<IReadOnlyList<HlsGroupCallStreamChannel>> GetChannelsAsync(GroupCallDocument groupCall)
    {
        if (!CanReadRtmp(groupCall))
        {
            logger.LogWarning("Cannot read RTMP for group call {CallId}: Active={Active}, RtmpStream={RtmpStream}, RtmpStreamKey={HasKey}",
                groupCall.CallId, groupCall.Active, groupCall.RtmpStream, !string.IsNullOrWhiteSpace(groupCall.RtmpStreamKey));
            return [];
        }

        try
        {
            var manifest = await WaitForInitialBackfillWindowAsync(groupCall, video: true, VideoChannel);
            var lastTimestamp = MaxTimestamp(manifest.LatestSegmentTimestampMs, manifest.LatestPartTimestampMs);
            if (lastTimestamp is null)
            {
                logger.LogWarning(
                    "No stream data available for group call {CallId}: Segments={Segments}, Parts={Parts}, LatestSegmentTs={LatestSegmentTs}, LatestPartTs={LatestPartTs}",
                    groupCall.CallId,
                    manifest.Segments.Count,
                    manifest.Parts.Count,
                    manifest.LatestSegmentTimestampMs,
                    manifest.LatestPartTimestampMs);
                return [];
            }

            logger.LogInformation(
                "Returning group call stream channel: CallId={CallId}, Oldest={Oldest}, Latest={Latest}, Segments={Segments}, Parts={Parts}",
                groupCall.CallId,
                OldestTimestamp(manifest),
                lastTimestamp.Value,
                manifest.Segments.Count,
                manifest.Parts.Count);

            // Telegram lists the video channel and the audio channel (0); the audio one only exists when
            // the stream has an audio rendition of its own.
            var channels = new List<HlsGroupCallStreamChannel>
            {
                new(VideoChannel, VideoScale, lastTimestamp.Value)
            };
            var audio = await GetMediaManifestAsync(groupCall, video: false, AudioChannel, forceRefresh: false);
            var lastAudioTimestamp = MaxTimestamp(audio.LatestSegmentTimestampMs, audio.LatestPartTimestampMs);
            if (audio.ChildManifestUrl != manifest.ChildManifestUrl && lastAudioTimestamp is not null)
            {
                channels.Add(new HlsGroupCallStreamChannel(AudioChannel, VideoScale, lastAudioTimestamp.Value));
            }

            return channels;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read HLS stream channels for group call {CallId}", groupCall.CallId);
            return [];
        }
    }

    public async Task<byte[]> ReadPartAsync(GroupCallDocument groupCall, TInputGroupCallStream location)
    {
        if (!CanReadRtmp(groupCall))
        {
            RpcErrors.RpcErrors400.LocationInvalid.ThrowRpcError();
            return [];
        }

        var video = location.VideoChannel.GetValueOrDefault() > 0;
        var channel = video ? location.VideoChannel.GetValueOrDefault() : 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var manifest = await GetMediaManifestAsync(groupCall, video, channel, forceRefresh: attempt > 0);
            var fragments = SelectFragments(manifest, location.TimeMs, location.Scale);
            if (fragments.Count == 0)
            {
                ThrowTimeTooBig();
                return [];
            }

            try
            {
                var result = await ReadCachedPartAsync(groupCall, location, video, channel, manifest, fragments);

                logger.LogInformation(
                    "Returning group call HLS chunk: CallId={CallId}, TimeMs={TimeMs}, Scale={Scale}, VideoChannel={VideoChannel}, VideoQuality={VideoQuality}, Container={Container}, Manifest={Manifest}, Fragments={Fragments}, FragmentTimes={FragmentTimes}, Bytes={Bytes}",
                    groupCall.CallId,
                    location.TimeMs,
                    location.Scale,
                    location.VideoChannel,
                    location.VideoQuality,
                    manifest.Container,
                    manifest.ChildManifestUrl,
                    fragments.Count,
                    string.Join(',', fragments.Select(fragment => fragment.TimestampMs)),
                    result.Length);

                return result;
            }
            catch (HttpRequestException ex) when (attempt == 0 &&
                                                  ex.StatusCode is HttpStatusCode.NotFound or
                                                      HttpStatusCode.Unauthorized or
                                                      HttpStatusCode.Forbidden)
            {
                if (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    ClearHlsSession(groupCall);
                }

                logger.LogInformation(
                    ex,
                    "Refreshing HLS manifest after a failed fragment read: CallId={CallId}, TimeMs={TimeMs}, StatusCode={StatusCode}, Manifest={Manifest}",
                    groupCall.CallId,
                    location.TimeMs,
                    ex.StatusCode,
                    manifest.ChildManifestUrl);
            }
        }

        ThrowTimeTooBig();
        return [];
    }

    private Task<byte[]> ReadCachedPartAsync(
        GroupCallDocument groupCall,
        TInputGroupCallStream location,
        bool video,
        int channel,
        MediaManifest manifest,
        List<HlsFragment> fragments)
    {
        var now = DateTimeOffset.UtcNow;
        var key = $"{groupCall.CallId}:{video}:{channel}:{location.Scale}:{fragments[0].Uri}:{fragments.Count}";
        foreach (var expired in _partCache.Where(entry => entry.Value.ExpiresAt <= now).Select(entry => entry.Key).ToList())
        {
            _partCache.TryRemove(expired, out _);
        }

        var entry = _partCache.GetOrAdd(key, _ => (
            new Lazy<Task<byte[]>>(() => video
                ? ReadUnifiedVideoPartAsync(groupCall, location, manifest, fragments)
                : ReadAudioPartAsync(groupCall, manifest, fragments)),
            now.Add(PartCacheTtl)));
        var part = entry.Part.Value;
        if (part.IsFaulted || part.IsCanceled)
        {
            _partCache.TryRemove(key, out _);
        }

        return part;
    }

    /// <summary>
    /// The video channel's part, which tgcalls plays as a unified broadcast: the video fragments and the
    /// audio of the same time span in one MP4.
    /// </summary>
    private async Task<byte[]> ReadUnifiedVideoPartAsync(
        GroupCallDocument groupCall,
        TInputGroupCallStream location,
        MediaManifest videoManifest,
        List<HlsFragment> videoFragments)
    {
        var videoBytes = await ReadFragmentsAsync(groupCall, videoManifest, videoFragments);
        var audioBytes = await TryReadAudioForAsync(groupCall, location, videoManifest);
        return WrapTelegramVideoStreamPart(await muxer.MuxAsync(videoBytes, audioBytes), DefaultTelegramStreamContainer);
    }

    private async Task<byte[]> ReadAudioPartAsync(
        GroupCallDocument groupCall,
        MediaManifest audioManifest,
        List<HlsFragment> audioFragments)
    {
        var audioBytes = await ReadFragmentsAsync(groupCall, audioManifest, audioFragments);
        return WrapTelegramVideoStreamPart(await muxer.MuxAsync(null, audioBytes), DefaultTelegramStreamContainer);
    }

    private async Task<byte[]?> TryReadAudioForAsync(GroupCallDocument groupCall, TInputGroupCallStream location, MediaManifest videoManifest)
    {
        var audioManifest = await GetMediaManifestAsync(groupCall, video: false, AudioChannel, forceRefresh: false);
        if (audioManifest.ChildManifestUrl == videoManifest.ChildManifestUrl)
        {
            return null; // the stream has no separate audio rendition
        }

        List<HlsFragment> audioFragments;
        try
        {
            audioFragments = SelectFragments(audioManifest, location.TimeMs, location.Scale);
        }
        catch (RpcException)
        {
            return null;
        }

        return audioFragments.Count == 0 ? null : await ReadFragmentsAsync(groupCall, audioManifest, audioFragments);
    }

    private async Task<byte[]> ReadFragmentsAsync(
        GroupCallDocument groupCall,
        MediaManifest manifest,
        List<HlsFragment> fragments)
    {
        var bytes = new List<byte>();
        if (!string.IsNullOrWhiteSpace(manifest.InitUri))
        {
            var initBytes = await GetInitBytesAsync(groupCall, manifest.ChildManifestUrl, manifest.InitUri);
            bytes.AddRange(initBytes);
        }

        foreach (var fragment in fragments)
        {
            var fragmentUrl = ResolveHlsUrl(manifest.ChildManifestUrl, fragment.Uri);
            bytes.AddRange(await GetHlsBytesAsync(groupCall, fragmentUrl));
        }

        return bytes.ToArray();
    }

    private async Task<MediaManifest> GetMediaManifestAsync(
        GroupCallDocument groupCall,
        bool video,
        int channel,
        bool forceRefresh = false)
    {
        var streamKey = groupCall.RtmpStreamKey!;
        var key = $"{streamKey}:{video}:{channel}";
        var now = DateTimeOffset.UtcNow;
        if (!forceRefresh && _mediaManifestCache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Manifest;
        }

        var childUrl = await GetChildManifestUrlAsync(groupCall, video, channel, refresh: forceRefresh);
        string manifestText;
        try
        {
            manifestText = await GetHlsStringAsync(groupCall, childUrl);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            ClearHlsSession(groupCall);
            childUrl = await GetChildManifestUrlAsync(groupCall, video, channel, refresh: true);
            manifestText = await GetHlsStringAsync(groupCall, childUrl);
        }
        catch
        {
            childUrl = await GetChildManifestUrlAsync(groupCall, video, channel, refresh: true);
            manifestText = await GetHlsStringAsync(groupCall, childUrl);
        }

        var manifest = ParseMediaManifest(childUrl, manifestText);
        _mediaManifestCache[key] = new MediaManifestCacheEntry(manifest, now.Add(MediaManifestCacheTtl));
        return manifest;
    }

    private async Task<MediaManifest> WaitForInitialBackfillWindowAsync(GroupCallDocument groupCall, bool video, int channel)
    {
        var deadline = DateTimeOffset.UtcNow.Add(StartupManifestWaitTimeout);
        var manifest = await GetMediaManifestAsync(groupCall, video, channel);

        while (!HasInitialBackfillWindow(manifest) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(StartupManifestRetryDelay);
            manifest = await GetMediaManifestAsync(groupCall, video, channel);
        }

        if (!HasInitialBackfillWindow(manifest))
        {
            logger.LogInformation(
                "HLS startup window is short: CallId={CallId}, Segments={Segments}, Parts={Parts}, Oldest={Oldest}, Latest={Latest}",
                groupCall.CallId,
                manifest.Segments.Count,
                manifest.Parts.Count,
                OldestTimestamp(manifest),
                MaxTimestamp(manifest.LatestSegmentEndTimestampMs, manifest.LatestPartEndTimestampMs));
        }

        return manifest;
    }

    private async Task<string> GetChildManifestUrlAsync(GroupCallDocument groupCall, bool video, int channel, bool refresh)
    {
        var streamKey = groupCall.RtmpStreamKey!;
        var key = $"{streamKey}:{video}:{channel}";
        var now = DateTimeOffset.UtcNow;
        if (!refresh && _childManifestCache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            return cached.ChildManifestUrl;
        }

        var masterUrl = BuildHlsUrl(groupCall, "index.m3u8");
        var master = await GetHlsStringAsync(groupCall, masterUrl);
        if (IsMediaManifest(master))
        {
            _childManifestCache[key] = new ChildManifestCacheEntry(masterUrl, now.Add(ChildManifestCacheTtl));
            return masterUrl;
        }

        var childUri = SelectChildManifest(master, video, channel);
        if (childUri == null)
        {
            RpcErrors.RpcErrors400.LocationInvalid.ThrowRpcError();
            return string.Empty;
        }

        var childUrl = ResolveHlsUrl(masterUrl, childUri);
        _childManifestCache[key] = new ChildManifestCacheEntry(childUrl, now.Add(ChildManifestCacheTtl));
        return childUrl;
    }

    private async Task<byte[]> GetInitBytesAsync(GroupCallDocument groupCall, string childManifestUrl, string initUri)
    {
        var initUrl = ResolveHlsUrl(childManifestUrl, initUri);
        var now = DateTimeOffset.UtcNow;
        if (_initFragmentCache.TryGetValue(initUrl, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Bytes;
        }

        var bytes = await GetHlsBytesAsync(groupCall, initUrl);
        _initFragmentCache[initUrl] = new BytesCacheEntry(bytes, now.Add(InitFragmentCacheTtl));
        return bytes;
    }

    private async Task<string> GetHlsStringAsync(GroupCallDocument groupCall, string url)
    {
        using var response = await SendHlsRequestAsync(groupCall.RtmpStreamKey!, url);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<byte[]> GetHlsBytesAsync(GroupCallDocument groupCall, string url)
    {
        using var response = await SendHlsRequestAsync(groupCall.RtmpStreamKey!, url);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private async Task<HttpResponseMessage> SendHlsRequestAsync(string streamKey, string url)
    {
        var currentUrl = url;
        for (var redirect = 0; redirect < 5; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
            if (TryGetHlsCookieHeader(streamKey, out var cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            StoreHlsCookies(streamKey, response.Headers);

            if (IsRedirect(response.StatusCode) && response.Headers.Location != null)
            {
                currentUrl = ResolveHlsUrl(currentUrl, response.Headers.Location.ToString());
                response.Dispose();
                continue;
            }

            response.EnsureSuccessStatusCode();
            return response;
        }

        throw new HttpRequestException("Too many HLS redirects.");
    }

    private bool TryGetHlsCookieHeader(string streamKey, out string cookieHeader)
    {
        var now = DateTimeOffset.UtcNow;
        if (_hlsSessionCookieCache.TryGetValue(streamKey, out var cached) && cached.ExpiresAt > now)
        {
            cookieHeader = cached.CookieHeader;
            return true;
        }

        _hlsSessionCookieCache.TryRemove(streamKey, out _);
        cookieHeader = string.Empty;
        return false;
    }

    private void StoreHlsCookies(string streamKey, HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
        {
            return;
        }

        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TryGetHlsCookieHeader(streamKey, out var existingCookieHeader))
        {
            foreach (var existingCookie in existingCookieHeader.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                AddCookie(cookies, existingCookie);
            }
        }

        foreach (var setCookieHeader in setCookieHeaders)
        {
            var cookie = setCookieHeader.Split(';', 2)[0];
            AddCookie(cookies, cookie);
        }

        if (cookies.Count == 0)
        {
            return;
        }

        var cookieHeader = string.Join("; ", cookies.Select(cookie => $"{cookie.Key}={cookie.Value}"));
        _hlsSessionCookieCache[streamKey] = new HlsSessionCookieCacheEntry(
            cookieHeader,
            DateTimeOffset.UtcNow.Add(HlsSessionCookieCacheTtl));
    }

    private static void AddCookie(IDictionary<string, string> cookies, string cookie)
    {
        var separator = cookie.IndexOf('=');
        if (separator <= 0)
        {
            return;
        }

        var name = cookie[..separator].Trim();
        var value = cookie[(separator + 1)..].Trim();
        if (name.Length > 0)
        {
            cookies[name] = value;
        }
    }

    private void ClearHlsSession(GroupCallDocument groupCall)
    {
        if (!string.IsNullOrWhiteSpace(groupCall.RtmpStreamKey))
        {
            _hlsSessionCookieCache.TryRemove(groupCall.RtmpStreamKey, out _);
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is >= 300 and < 400;
    }

    private List<HlsFragment> SelectFragments(MediaManifest manifest, long requestedTimeMs, int scale)
    {
        var durationMs = SegmentDurationMs >> Math.Clamp(scale, 0, 10);
        var candidates = manifest.Parts.Count > 0
            ? manifest.Parts
            : manifest.Segments;
        if (ShouldUseFullSegmentsForOlderRequest(manifest, requestedTimeMs, durationMs))
        {
            candidates = manifest.Segments;
        }

        if (candidates.Count == 0 && manifest.Parts.Count > 0)
        {
            candidates = manifest.Parts;
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var oldest = candidates[0];
        var start = requestedTimeMs <= 0
            ? candidates[^1].TimestampMs
            : AlignRequestedTimeToFragmentGrid(requestedTimeMs, durationMs, candidates);
        var end = start + durationMs;
        if (requestedTimeMs > 0 && start < oldest.TimestampMs - TimestampToleranceMs)
        {
            var deltaMs = oldest.TimestampMs - start;
            logger.LogInformation(
                "Rejecting stale group call HLS request: RequestedTimeMs={RequestedTimeMs}, AlignedStartMs={AlignedStartMs}, OldestTimeMs={OldestTimeMs}, DeltaMs={DeltaMs}",
                requestedTimeMs,
                start,
                oldest.TimestampMs,
                deltaMs);
            ThrowTimeTooSmall();
            return [];
        }

        var newest = candidates[^1];
        if (requestedTimeMs > 0 && start > newest.TimestampMs + TimestampToleranceMs)
        {
            return [];
        }

        if (candidates != manifest.Parts)
        {
            var selected = candidates
                .OrderBy(fragment => Math.Abs(fragment.TimestampMs - start))
                .FirstOrDefault();

            return selected is null ? [] : [selected];
        }

        var fragments = candidates
            .Where(fragment =>
                fragment.TimestampMs < end + TimestampToleranceMs &&
                fragment.TimestampMs + fragment.DurationMs > start - TimestampToleranceMs)
            .ToList();

        return fragments.Count == 0
            ? [candidates.LastOrDefault(fragment => fragment.TimestampMs <= end + TimestampToleranceMs) ?? candidates[0]]
            : fragments;
    }

    private static long OverlapMs(long firstStart, long firstEnd, long secondStart, long secondEnd)
    {
        return Math.Max(0, Math.Min(firstEnd, secondEnd) - Math.Max(firstStart, secondStart));
    }

    private static long AlignRequestedTimeToFragmentGrid(long requestedTimeMs, int durationMs, List<HlsFragment> candidates)
    {
        if (durationMs <= 0 || candidates.Count == 0)
        {
            return requestedTimeMs;
        }

        var offset = PositiveModulo(candidates[^1].TimestampMs, durationMs);
        var aligned = requestedTimeMs - PositiveModulo(requestedTimeMs - offset, durationMs);

        return aligned;
    }

    private static long PositiveModulo(long value, long modulo)
    {
        var result = value % modulo;
        return result < 0 ? result + modulo : result;
    }

    private static bool ShouldUseFullSegmentsForOlderRequest(MediaManifest manifest, long requestedTimeMs, int durationMs)
    {
        if (manifest.Parts.Count == 0 ||
            manifest.Segments.Count == 0 ||
            requestedTimeMs <= 0 ||
            requestedTimeMs >= manifest.Parts[0].TimestampMs - TimestampToleranceMs)
        {
            return false;
        }

        var segment = manifest.Segments.LastOrDefault(fragment => fragment.TimestampMs <= requestedTimeMs + TimestampToleranceMs) ??
                      manifest.Segments[0];

        return segment.DurationMs <= durationMs + TimestampToleranceMs;
    }

    private static bool HasInitialBackfillWindow(MediaManifest manifest)
    {
        var oldest = OldestTimestamp(manifest);
        var latest = MaxTimestamp(manifest.LatestSegmentTimestampMs, manifest.LatestPartTimestampMs);
        return oldest is not null &&
               latest is not null &&
               latest.Value - oldest.Value >= InitialBackfillWindowMs;
    }

    private static long? OldestTimestamp(MediaManifest manifest)
    {
        return (manifest.Segments.Count > 0, manifest.Parts.Count > 0) switch
        {
            (true, true) => Math.Min(manifest.Segments[0].TimestampMs, manifest.Parts[0].TimestampMs),
            (true, false) => manifest.Segments[0].TimestampMs,
            (false, true) => manifest.Parts[0].TimestampMs,
            _ => null
        };
    }

    private MediaManifest ParseMediaManifest(string childManifestUrl, string manifestText)
    {
        var lines = GetManifestLines(manifestText);
        var segments = new List<ParsedFragment>();
        var parts = new List<HlsFragment>();
        string? initUri = null;
        long? currentProgramDateTimeMs = null;
        long? partCursorMs = null;
        int pendingSegmentDurationMs = 1000;

        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                initUri = ExtractUriAttribute(line);
                continue;
            }

            if (line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.OrdinalIgnoreCase))
            {
                currentProgramDateTimeMs = ParseProgramDateTime(line);
                partCursorMs = currentProgramDateTimeMs;
                continue;
            }

            if (line.StartsWith("#EXT-X-PART:", StringComparison.OrdinalIgnoreCase))
            {
                var uri = ExtractUriAttribute(line);
                if (uri == null || partCursorMs == null)
                {
                    continue;
                }

                var durationMs = ParseDurationAttributeMs(line, "DURATION") ?? 200;
                parts.Add(new HlsFragment(uri, partCursorMs.Value, durationMs));
                partCursorMs += durationMs;
                continue;
            }

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                pendingSegmentDurationMs = ParseExtInfDurationMs(line) ?? 1000;
                continue;
            }

            if (IsMediaSegmentUri(line))
            {
                segments.Add(new ParsedFragment(
                    line,
                    currentProgramDateTimeMs,
                    pendingSegmentDurationMs,
                    ParseSegmentNumber(line)));
                if (currentProgramDateTimeMs != null)
                {
                    currentProgramDateTimeMs += pendingSegmentDurationMs;
                    partCursorMs = currentProgramDateTimeMs;
                }
            }
        }

        var fixedSegments = FillMissingSegmentTimestamps(segments)
            .Where(fragment => fragment.TimestampMs != null)
            .Select(fragment => new HlsFragment(fragment.Uri, fragment.TimestampMs!.Value, fragment.DurationMs))
            .ToList();

        return new MediaManifest(childManifestUrl, InferContainer(lines), initUri, fixedSegments, parts);
    }

    private static List<ParsedFragment> FillMissingSegmentTimestamps(List<ParsedFragment> segments)
    {
        if (segments.Count == 0)
        {
            return segments;
        }

        for (var i = 1; i < segments.Count; i++)
        {
            if (segments[i].TimestampMs == null && segments[i - 1].TimestampMs != null)
            {
                segments[i] = segments[i] with
                {
                    TimestampMs = segments[i - 1].TimestampMs + segments[i - 1].DurationMs
                };
            }
        }

        for (var i = segments.Count - 2; i >= 0; i--)
        {
            if (segments[i].TimestampMs == null && segments[i + 1].TimestampMs != null)
            {
                segments[i] = segments[i] with
                {
                    TimestampMs = segments[i + 1].TimestampMs - segments[i].DurationMs
                };
            }
        }

        if (segments.All(fragment => fragment.TimestampMs == null) &&
            segments.All(fragment => fragment.Sequence != null))
        {
            var timestamp = (long)segments[0].Sequence!.Value * segments[0].DurationMs;
            for (var i = 0; i < segments.Count; i++)
            {
                segments[i] = segments[i] with
                {
                    TimestampMs = timestamp
                };
                timestamp += segments[i].DurationMs;
            }
        }

        return segments;
    }

    private string BuildHlsUrl(GroupCallDocument groupCall, string path)
    {
        var baseUrl = options.CurrentValue.RtmpHlsUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = DeriveHlsBaseUrl(groupCall.RtmpUrl);
        }

        return $"{baseUrl.TrimEnd('/')}/{Uri.EscapeDataString(groupCall.RtmpStreamKey!)}/{path}";
    }

    private static string? SelectChildManifest(string masterManifest, bool video, int channel)
    {
        var lines = GetManifestLines(masterManifest);
        if (!video)
        {
            var audio = lines
                .Select(ExtractUriAttribute)
                .FirstOrDefault(uri => uri?.Contains("audio", StringComparison.OrdinalIgnoreCase) == true);
            if (!string.IsNullOrWhiteSpace(audio))
            {
                return audio;
            }
        }

        if (channel > 0)
        {
            var channelPrefix = $"video{channel}_";
            var match = lines.FirstOrDefault(line =>
                !line.StartsWith('#') &&
                line.Contains(channelPrefix, StringComparison.OrdinalIgnoreCase) &&
                line.Contains("stream.m3u8", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return lines.FirstOrDefault(line =>
            !line.StartsWith('#') &&
            line.Contains("stream.m3u8", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMediaManifest(string manifestText)
    {
        var lines = GetManifestLines(manifestText);
        return lines.Any(line =>
            line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("#EXT-X-PART:", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> GetManifestLines(string manifest)
    {
        return manifest
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToList();
    }

    private static string? ExtractUriAttribute(string line)
    {
        const string marker = "URI=\"";
        var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = line.IndexOf('"', start);
        return end > start ? line[start..end] : null;
    }

    private static bool IsMediaSegmentUri(string line)
    {
        return !line.StartsWith('#') &&
               !line.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    private static string InferContainer(List<string> lines)
    {
        foreach (var line in lines)
        {
            string? uri = null;
            if (line.StartsWith("#EXT-X-PART:", StringComparison.OrdinalIgnoreCase))
            {
                uri = ExtractUriAttribute(line);
            }
            else if (IsMediaSegmentUri(line))
            {
                uri = line;
            }

            if (string.IsNullOrWhiteSpace(uri))
            {
                continue;
            }

            var normalized = StripQueryAndFragment(uri).ToLowerInvariant();
            if (normalized.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            {
                return MpegTsTelegramStreamContainer;
            }

            if (normalized.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                return DefaultTelegramStreamContainer;
            }
        }

        return DefaultTelegramStreamContainer;
    }

    private static string StripQueryAndFragment(string uri)
    {
        var end = uri.Length;
        var query = uri.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            end = Math.Min(end, query);
        }

        var fragment = uri.IndexOf('#', StringComparison.Ordinal);
        if (fragment >= 0)
        {
            end = Math.Min(end, fragment);
        }

        return uri[..end];
    }

    private static long? ParseProgramDateTime(string line)
    {
        var value = line["#EXT-X-PROGRAM-DATE-TIME:".Length..];
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToUnixTimeMilliseconds()
            : null;
    }

    private static long? MaxTimestamp(long? first, long? second)
    {
        return (first, second) switch
        {
            (null, null) => null,
            ({ } value, null) => value,
            (null, { } value) => value,
            ({ } a, { } b) => Math.Max(a, b)
        };
    }

    private static int? ParseExtInfDurationMs(string line)
    {
        var start = "#EXTINF:".Length;
        var end = line.IndexOf(',', start);
        var value = end > start ? line[start..end] : line[start..];
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? Math.Max(1, (int)Math.Round(seconds * 1000))
            : null;
    }

    private static int? ParseDurationAttributeMs(string line, string attribute)
    {
        var marker = $"{attribute}=";
        var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = line.IndexOf(',', start);
        var value = end > start ? line[start..end] : line[start..];
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? Math.Max(1, (int)Math.Round(seconds * 1000))
            : null;
    }

    private static int? ParseSegmentNumber(string uri)
    {
        var marker = "_seg";
        var start = uri.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = start;
        while (end < uri.Length && char.IsDigit(uri[end]))
        {
            end++;
        }

        return end > start && int.TryParse(uri[start..end], out var value) ? value : null;
    }

    private static string ResolveHlsUrl(string parentUrl, string childUri)
    {
        // On Linux a rooted path such as mediamtx's "/live/<key>/index.m3u8?cookieCheck=1" redirect also
        // parses as an absolute (file://) URI, so only http(s) counts as absolute here.
        return Uri.TryCreate(childUri, UriKind.Absolute, out var absolute) &&
               (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            ? absolute.ToString()
            : new Uri(new Uri(parentUrl), childUri).ToString();
    }

    private static string DeriveHlsBaseUrl(string? rtmpUrl)
    {
        if (string.IsNullOrWhiteSpace(rtmpUrl) || !Uri.TryCreate(rtmpUrl, UriKind.Absolute, out var uri))
        {
            return "http://rtmp-server:8888/live";
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttp,
            Port = 8888
        };

        return builder.Uri.ToString().TrimEnd('/');
    }

    private static bool CanReadRtmp(GroupCallDocument groupCall)
    {
        return groupCall is { Active: true, RtmpStream: true } &&
               !string.IsNullOrWhiteSpace(groupCall.RtmpStreamKey);
    }

    internal static string ResolveHlsUrlForTests(string parentUrl, string childUri) => ResolveHlsUrl(parentUrl, childUri);

    internal static byte[] WrapTelegramVideoStreamPartForTests(byte[] media) => WrapTelegramVideoStreamPart(media, DefaultTelegramStreamContainer);

    private static byte[] WrapTelegramVideoStreamPart(IReadOnlyCollection<byte> mediaBytes, string container)
    {
        var result = new List<byte>(mediaBytes.Count + 64);
        WriteInt32(result, TelegramVideoStreamPartSignature);
        WriteSerializedString(result, container);
        // The header as Telegram writes it for an RTMP stream (captured 2026-11-01): activeMask 0, one event
        // at media offset 0 with an empty endpoint id, rotation 0, extra 0.
        WriteInt32(result, 0); // activeMask
        WriteInt32(result, 1); // event count
        WriteInt32(result, 0); // media offset after the stream-info header is consumed
        WriteSerializedString(result, string.Empty);
        WriteInt32(result, 0); // rotation
        WriteInt32(result, 0); // extra
        result.AddRange(mediaBytes);

        return result.ToArray();
    }

    private static void WriteInt32(List<byte> bytes, int value)
    {
        bytes.AddRange(BitConverter.GetBytes(value));
    }

    private static void WriteSerializedString(List<byte> bytes, string value)
    {
        var valueBytes = System.Text.Encoding.UTF8.GetBytes(value);
        if (valueBytes.Length >= 254)
        {
            bytes.Add(254);
            bytes.Add((byte)(valueBytes.Length & 0xff));
            bytes.Add((byte)((valueBytes.Length >> 8) & 0xff));
            bytes.Add((byte)((valueBytes.Length >> 16) & 0xff));
            bytes.AddRange(valueBytes);
            AddPadding(bytes, valueBytes.Length);
            return;
        }

        bytes.Add((byte)valueBytes.Length);
        bytes.AddRange(valueBytes);
        AddPadding(bytes, valueBytes.Length + 1);
    }

    private static void AddPadding(List<byte> bytes, int lengthWithPrefix)
    {
        var padding = (4 - lengthWithPrefix % 4) % 4;
        for (var i = 0; i < padding; i++)
        {
            bytes.Add(0);
        }
    }

    private static void ThrowTimeTooBig()
    {
        throw new RpcException(new RpcError(400, "TIME_TOO_BIG"));
    }

    private static void ThrowTimeTooSmall()
    {
        throw new RpcException(new RpcError(400, "TIME_TOO_SMALL"));
    }

    private sealed record ChildManifestCacheEntry(string ChildManifestUrl, DateTimeOffset ExpiresAt);

    private sealed record MediaManifestCacheEntry(MediaManifest Manifest, DateTimeOffset ExpiresAt);

    private sealed record BytesCacheEntry(byte[] Bytes, DateTimeOffset ExpiresAt);

    private sealed record HlsSessionCookieCacheEntry(string CookieHeader, DateTimeOffset ExpiresAt);

    private sealed record ParsedFragment(string Uri, long? TimestampMs, int DurationMs, int? Sequence);
}

public sealed record HlsGroupCallStreamChannel(int Channel, int Scale, long LastTimestampMs);

internal sealed record MediaManifest(
    string ChildManifestUrl,
    string Container,
    string? InitUri,
    List<HlsFragment> Segments,
    List<HlsFragment> Parts)
{
    public long? LatestSegmentTimestampMs => Segments.Count == 0 ? null : Segments[^1].TimestampMs;

    public long? LatestPartTimestampMs => Parts.Count == 0 ? null : Parts[^1].TimestampMs;

    public long? LatestSegmentEndTimestampMs => Segments.Count == 0 ? null : Segments[^1].TimestampMs + Segments[^1].DurationMs;

    public long? LatestPartEndTimestampMs => Parts.Count == 0 ? null : Parts[^1].TimestampMs + Parts[^1].DurationMs;
}

internal sealed record HlsFragment(string Uri, long TimestampMs, int DurationMs);
