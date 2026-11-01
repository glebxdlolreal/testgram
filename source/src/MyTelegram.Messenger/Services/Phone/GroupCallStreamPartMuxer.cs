using System.Diagnostics;
using System.Globalization;
using MyTelegram.Messenger.Services.VideoProcessing;

namespace MyTelegram.Messenger.Services.Phone;

/// <summary>
/// Builds the media inside a livestream part the way Telegram serves it (captured 2026-11-01): one plain
/// MP4 (<c>ftyp</c>, <c>free</c>, <c>mdat</c>, <c>moov</c>) holding exactly the part's time span. tgcalls
/// decodes a unified part twice, once for its video and once for its audio, so the video channel's part
/// carries both tracks; the audio channel's part carries the audio alone.
/// </summary>
/// <remarks>
/// tgcalls opens a fresh decoder for every part and plays its frames and samples back to back, so a part
/// has to start on a keyframe, must not overlap its neighbours, and has to fit the single 128 KB
/// <c>upload.getFile</c> Android asks for (<c>VoIPService.onRequestBroadcastPart</c>): a cut-off MP4 has no
/// <c>moov</c> and the whole second is lost. The video is therefore re-encoded from the preceding keyframe;
/// audio frames are copied. A stream without a separate audio rendition gets video-only parts.
/// </remarks>
public interface IGroupCallStreamPartMuxer
{
    /// <summary>
    /// Cuts <c>[fromMs, fromMs + durationMs)</c> out of the given HLS media (each an init section plus
    /// fragments, starting at <see cref="StreamPartInput.StartMs"/> on the stream's clock) into one MP4 of at
    /// most <paramref name="maxBytes"/> bytes.
    /// </summary>
    Task<byte[]> BuildAsync(StreamPartInput? video, StreamPartInput? audio, long fromMs, int durationMs, int maxBytes,
        CancellationToken cancellationToken = default);
}

public sealed record StreamPartInput(byte[] Media, long StartMs);

public sealed class GroupCallStreamPartMuxer(
    IFfmpegLocator ffmpegLocator,
    ILogger<GroupCallStreamPartMuxer> logger)
    : IGroupCallStreamPartMuxer, ISingletonDependency
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Video bitrates to try, highest first: with a VBV buffer of a third of a second a one-second part stays
    // near the cap, which leaves room for the audio inside 128 KB.
    private static readonly int[] VideoBitratesKbps = [700, 400, 200];
    private const int MaxVideoHeight = 720;

    public async Task<byte[]> BuildAsync(StreamPartInput? video, StreamPartInput? audio, long fromMs, int durationMs,
        int maxBytes, CancellationToken cancellationToken = default)
    {
        if (video == null && audio == null)
        {
            throw new ArgumentException("A stream part needs at least one track.");
        }

        var ffmpeg = ffmpegLocator.FfmpegPath;
        if (ffmpeg == null)
        {
            logger.LogError("ffmpeg is not installed, so livestream parts cannot be built");
            new RpcError(500, "INTERNAL").ThrowRpcError();
            return [];
        }

        var directory = Path.Combine(Path.GetTempPath(), "tg-stream-part-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var origin = Math.Min(video?.StartMs ?? long.MaxValue, audio?.StartMs ?? long.MaxValue);
            var from = Seconds(Math.Max(0, fromMs - origin));
            var to = Seconds(Math.Max(0, fromMs - origin) + durationMs);
            var output = Path.Combine(directory, "part.mp4");

            // Audio frames are copied: output-side -ss/-t keeps exactly the packets whose timestamps fall in the
            // window, so consecutive parts tile the stream.
            string? audioPath = null;
            if (audio != null)
            {
                var source = await WriteInputAsync(directory, "a.mp4", audio, origin, cancellationToken);
                audioPath = video == null ? output : Path.Combine(directory, "audio-part.mp4");
                await RunFfmpegAsync(ffmpeg, [
                    "-v", "error", "-y", .. source, "-map", "0:a:0", "-ss", from, "-t", Seconds(durationMs),
                    "-c:a", "copy", "-f", "mp4", audioPath
                ], cancellationToken);
                if (video == null)
                {
                    return await ReadWithinAsync(output, maxBytes, cancellationToken);
                }
            }

            // Video is re-encoded so the part opens on a keyframe. The trim filter keeps the frames whose
            // timestamps fall in the window; output-side -t would also drop the last frame, whose duration
            // crosses the end, and the part would lose a frame every second.
            var videoSource = await WriteInputAsync(directory, "v.mp4", video!, origin, cancellationToken);
            byte[]? smallest = null;
            foreach (var bitrate in VideoBitratesKbps)
            {
                var arguments = new List<string> { "-v", "error", "-y" };
                arguments.AddRange(videoSource);
                if (audioPath != null)
                {
                    arguments.AddRange(["-i", audioPath, "-map", "0:v:0", "-map", "1:a:0", "-c:a", "copy"]);
                }
                else
                {
                    arguments.AddRange(["-map", "0:v:0"]);
                }

                arguments.AddRange([
                    "-filter:v", $"trim=start={from}:end={to},setpts=PTS-{from}/TB,scale=-2:'min({MaxVideoHeight},ih)'",
                    "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-pix_fmt", "yuv420p", "-g", "600",
                    "-b:v", $"{bitrate}k", "-maxrate", $"{bitrate}k", "-bufsize", $"{bitrate / 3}k",
                    "-f", "mp4", output
                ]);
                await RunFfmpegAsync(ffmpeg, arguments, cancellationToken);

                var part = await File.ReadAllBytesAsync(output, cancellationToken);
                if (part.Length <= maxBytes)
                {
                    return part;
                }

                smallest = part;
                logger.LogInformation("Livestream part of {Bytes} bytes at {Bitrate} kbit/s exceeds {MaxBytes}; re-encoding smaller",
                    part.Length, bitrate, maxBytes);
            }

            logger.LogWarning("Livestream part stays at {Bytes} bytes, over the {MaxBytes} a client downloads", smallest?.Length, maxBytes);
            new RpcError(500, "INTERNAL").ThrowRpcError();
            return [];
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException ex)
            {
                logger.LogDebug(ex, "Could not delete {Directory}", directory);
            }
        }
    }

    /// <summary>Writes the input and returns its ffmpeg arguments, placed where it sits on the stream's clock.</summary>
    private static async Task<string[]> WriteInputAsync(string directory, string name, StreamPartInput input, long origin,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, name);
        await File.WriteAllBytesAsync(path, input.Media, cancellationToken);
        // ffmpeg moves each input to start at 0; the offset puts it back relative to the other one.
        return ["-itsoffset", Seconds(input.StartMs - origin), "-i", path];
    }

    private async Task RunFfmpegAsync(string ffmpeg, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var (exitCode, error) = await RunAsync(ffmpeg, arguments, cancellationToken);
        if (exitCode != 0 || !File.Exists(arguments[^1]))
        {
            logger.LogWarning("ffmpeg could not build a livestream part (exit {ExitCode}): {Error}", exitCode, error);
            new RpcError(500, "INTERNAL").ThrowRpcError();
        }
    }

    private async Task<byte[]> ReadWithinAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        var part = await File.ReadAllBytesAsync(path, cancellationToken);
        if (part.Length > maxBytes)
        {
            logger.LogWarning("Livestream audio part of {Bytes} bytes is over the {MaxBytes} a client downloads", part.Length, maxBytes);
            new RpcError(500, "INTERNAL").ThrowRpcError();
        }

        return part;
    }

    private static string Seconds(long milliseconds) =>
        (milliseconds / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);

    private static async Task<(int ExitCode, string Error)> RunAsync(string fileName, IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        await stdout;
        return (process.ExitCode, await error);
    }
}
