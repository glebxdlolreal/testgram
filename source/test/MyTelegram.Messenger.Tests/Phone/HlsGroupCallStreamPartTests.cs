using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyTelegram.Messenger;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Messenger.Services.VideoProcessing;
using Shouldly;
using Xunit;

namespace MyTelegram.Messenger.Tests.Phone;

public class HlsGroupCallStreamPartTests
{
    [Fact]
    public void MediamtxsRootedCookieRedirect_StaysOnTheHlsServer()
    {
        // mediamtx answers the first manifest request with "Location: /live/<key>/index.m3u8?cookieCheck=1".
        // On Linux that rooted path also parses as file:///live/..., which HttpClient refuses.
        HlsGroupCallStreamService.ResolveHlsUrlForTests(
                "http://rtmp-server:8888/live/abc/index.m3u8",
                "/live/abc/index.m3u8?cookieCheck=1")
            .ShouldBe("http://rtmp-server:8888/live/abc/index.m3u8?cookieCheck=1");
        HlsGroupCallStreamService.ResolveHlsUrlForTests("http://rtmp-server:8888/live/abc/index.m3u8", "video1_stream.m3u8")
            .ShouldBe("http://rtmp-server:8888/live/abc/video1_stream.m3u8");
        HlsGroupCallStreamService.ResolveHlsUrlForTests("http://a/live/x.m3u8", "https://cdn.example/seg1.mp4")
            .ShouldBe("https://cdn.example/seg1.mp4");
    }

    [Fact]
    public void PartHeader_IsTelegramsByteForByte()
    {
        // The first 0x20 bytes of a part Telegram served for an RTMP stream (captured 2026-11-01).
        var telegram = Convert.FromHexString("0d812ea1036d7034000000000100000000000000000000000000000000000000");
        var media = new byte[] { 0, 0, 0, 0x1c, (byte)'f', (byte)'t', (byte)'y', (byte)'p' };

        var part = HlsGroupCallStreamService.WrapTelegramVideoStreamPartForTests(media);

        part.Take(0x20).ToArray().ShouldBe(telegram);
        part.Skip(0x20).ToArray().ShouldBe(media);
    }

    [Fact]
    public async Task Muxer_PutsTheVideoAndTheAudioOfAPartIntoOnePlainMp4()
    {
        var locator = new FfmpegLocator(Microsoft.Extensions.Options.Options.Create(new MyTelegramMessengerServerOptions()), NullLogger<FfmpegLocator>.Instance);
        locator.IsAvailable.ShouldBeTrue("ffmpeg and ffprobe are part of the messenger images and of this test host");
        var directory = Directory.CreateTempSubdirectory("stream-part-test").FullName;
        try
        {
            // Fragmented MP4 like an HLS rendition: an init section and one fragment of about a second.
            var video = await FragmentAsync(locator, directory, "video",
                "-f lavfi -i testsrc=size=320x180:rate=30 -t 1 -c:v libx264 -g 30 -pix_fmt yuv420p");
            var audio = await FragmentAsync(locator, directory, "audio",
                "-f lavfi -i sine=frequency=440:sample_rate=48000 -t 1 -c:a aac");
            var muxer = new GroupCallStreamPartMuxer(locator, NullLogger<GroupCallStreamPartMuxer>.Instance);

            var unified = await muxer.MuxAsync(video, audio);
            var audioOnly = await muxer.MuxAsync(null, audio);

            TopLevelBoxes(unified).ShouldBe(["ftyp", "free", "mdat", "moov"]);
            (await StreamTypesAsync(locator, directory, unified)).ShouldBe(["video", "audio"]);
            (await StreamTypesAsync(locator, directory, audioOnly)).ShouldBe(["audio"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<byte[]> FragmentAsync(IFfmpegLocator locator, string directory, string name, string input)
    {
        var path = Path.Combine(directory, name + ".mp4");
        await RunAsync(locator.FfmpegPath!, $"-v error -y {input} -movflags frag_keyframe+empty_moov+default_base_moof -f mp4 {path}");
        return await File.ReadAllBytesAsync(path);
    }

    private static async Task<string[]> StreamTypesAsync(IFfmpegLocator locator, string directory, byte[] mp4)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".mp4");
        await File.WriteAllBytesAsync(path, mp4);
        var output = await RunAsync(locator.FfprobePath!, $"-v error -show_entries stream=codec_type -of csv=p=0 {path}");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static List<string> TopLevelBoxes(byte[] mp4)
    {
        var boxes = new List<string>();
        for (var offset = 0; offset + 8 <= mp4.Length;)
        {
            var size = (mp4[offset] << 24) | (mp4[offset + 1] << 16) | (mp4[offset + 2] << 8) | mp4[offset + 3];
            boxes.Add(System.Text.Encoding.ASCII.GetString(mp4, offset + 4, 4));
            if (size < 8)
            {
                break;
            }

            offset += size;
        }

        return boxes;
    }

    private static async Task<string> RunAsync(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.ShouldBe(0, error);
        return output;
    }
}
