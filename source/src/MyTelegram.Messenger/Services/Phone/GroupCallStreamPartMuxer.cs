using System.Diagnostics;
using MyTelegram.Messenger.Services.VideoProcessing;

namespace MyTelegram.Messenger.Services.Phone;

/// <summary>
/// Builds the media inside a livestream part the way Telegram serves it (captured 2026-11-01): one plain
/// MP4 (<c>ftyp</c>, <c>free</c>, <c>mdat</c>, <c>moov</c>) per part. tgcalls decodes a unified part twice,
/// once for its video and once for its audio, so the video channel's part has to carry both tracks; the
/// audio channel's part carries the audio alone.
/// </summary>
public interface IGroupCallStreamPartMuxer
{
    /// <summary>Copies the tracks of the given HLS fragments (each an init section plus fragments) into one MP4.</summary>
    Task<byte[]> MuxAsync(byte[]? video, byte[]? audio, CancellationToken cancellationToken = default);
}

public sealed class GroupCallStreamPartMuxer(
    IFfmpegLocator ffmpegLocator,
    ILogger<GroupCallStreamPartMuxer> logger)
    : IGroupCallStreamPartMuxer, ISingletonDependency
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<byte[]> MuxAsync(byte[]? video, byte[]? audio, CancellationToken cancellationToken = default)
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
            var arguments = new List<string> { "-v", "error", "-y" };
            var maps = new List<string>();
            var input = 0;
            if (video != null)
            {
                var path = Path.Combine(directory, "video.mp4");
                await File.WriteAllBytesAsync(path, video, cancellationToken);
                arguments.AddRange(["-i", path]);
                maps.AddRange(["-map", $"{input++}:v:0"]);
            }

            if (audio != null)
            {
                var path = Path.Combine(directory, "audio.mp4");
                await File.WriteAllBytesAsync(path, audio, cancellationToken);
                arguments.AddRange(["-i", path]);
                maps.AddRange(["-map", $"{input}:a:0"]);
            }

            var output = Path.Combine(directory, "part.mp4");
            arguments.AddRange(maps);
            arguments.AddRange(["-c", "copy", "-f", "mp4", output]);

            var (exitCode, error) = await RunAsync(ffmpeg, arguments, cancellationToken);
            if (exitCode != 0 || !File.Exists(output))
            {
                logger.LogWarning("ffmpeg could not build a livestream part (exit {ExitCode}): {Error}", exitCode, error);
                new RpcError(500, "INTERNAL").ThrowRpcError();
                return [];
            }

            return await File.ReadAllBytesAsync(output, cancellationToken);
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
