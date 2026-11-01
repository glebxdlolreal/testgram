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
    public async Task Builder_CutsExactlyOneSecondStartingOnAKeyframeAndUnderTheDownloadLimit()
    {
        var locator = new FfmpegLocator(Microsoft.Extensions.Options.Options.Create(new MyTelegramMessengerServerOptions()), NullLogger<FfmpegLocator>.Instance);
        locator.IsAvailable.ShouldBeTrue("ffmpeg and ffprobe are part of the messenger images and of this test host");
        var directory = Directory.CreateTempSubdirectory("stream-part-test").FullName;
        try
        {
            // Fragmented MP4 like an LL-HLS rendition: an init section and 200 ms fragments, a keyframe every
            // 2 s, at a bitrate whose one-second slices would not fit a single 128 KB download.
            var video = await FragmentAsync(locator, directory, "video",
                "-f lavfi -i testsrc2=size=1280x720:rate=30 -t 4 -c:v libx264 -g 60 -b:v 3M -pix_fmt yuv420p");
            var audio = await FragmentAsync(locator, directory, "audio",
                "-f lavfi -i sine=frequency=440:sample_rate=48000 -t 4 -c:a aac");
            var builder = new GroupCallStreamPartMuxer(locator, NullLogger<GroupCallStreamPartMuxer>.Instance);
            const int limit = 128 * 1024 - 0x20;

            // [1 s, 2 s) begins a second after the only keyframe before it.
            var unified = await builder.BuildAsync(new StreamPartInput(video, 0), new StreamPartInput(audio, 0), 1000, 1000, limit);
            var audioOnly = await builder.BuildAsync(null, new StreamPartInput(audio, 0), 1000, 1000, limit);

            unified.Length.ShouldBeLessThanOrEqualTo(limit);
            TopLevelBoxes(unified).ShouldBe(["ftyp", "free", "mdat", "moov"]);
            (await StreamTypesAsync(locator, directory, unified)).ShouldBe(["video", "audio"]);
            (await ProbeAsync(locator, directory, unified, "-select_streams v -count_frames -show_entries stream=nb_read_frames"))
                .ShouldBe(["30"]);
            (await ProbeAsync(locator, directory, unified, "-select_streams v -show_entries frame=pict_type")).First().ShouldBe("I");
            // 1024-sample AAC frames at 48 kHz: the 47 whose timestamps fall in the second, as Telegram serves.
            (await ProbeAsync(locator, directory, unified, "-select_streams a -count_packets -show_entries stream=nb_read_packets"))
                .ShouldBe(["47"]);
            (await StreamTypesAsync(locator, directory, audioOnly)).ShouldBe(["audio"]);

            // Off the frame grid every frame still lands in exactly one part: none is lost at the boundary.
            var offGrid = await builder.BuildAsync(new StreamPartInput(video, 0), new StreamPartInput(audio, 0), 1010, 1000, limit);
            (await ProbeAsync(locator, directory, offGrid, "-select_streams v -count_frames -show_entries stream=nb_read_frames"))
                .ShouldBe(["30"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Window_StartsOnTheKeyframeBeforeItAndWaitsForTheWholeSecond()
    {
        var service = new HlsGroupCallStreamService(null!, null!, NullLogger<HlsGroupCallStreamService>.Instance);
        // The shape mediamtx serves: complete segments, then the LL-HLS parts of the newest ones, of which
        // only those opening a GOP are INDEPENDENT.
        var manifest = service.ParseMediaManifestForTests("http://rtmp-server:8888/live/k/video1_stream.m3u8", """
            #EXTM3U
            #EXT-X-MAP:URI="k_video1_init.mp4"
            #EXT-X-PROGRAM-DATE-TIME:2026-11-01T15:00:00.000Z
            #EXTINF:2.00000,
            k_video1_seg1.mp4
            #EXT-X-PART:DURATION=0.50000,URI="k_video1_part1.mp4",INDEPENDENT=YES
            #EXT-X-PART:DURATION=0.50000,URI="k_video1_part2.mp4"
            #EXT-X-PART:DURATION=0.50000,URI="k_video1_part3.mp4"
            #EXT-X-PART:DURATION=0.50000,URI="k_video1_part4.mp4"
            #EXTINF:2.00000,
            k_video1_seg2.mp4
            #EXT-X-PART:DURATION=0.50000,URI="k_video1_part5.mp4",INDEPENDENT=YES
            #EXT-X-PART:DURATION=0.50000,URI="k_video1_part6.mp4"
            #EXT-X-PART:DURATION=0.50000,URI="k_video1_part7.mp4"
            #EXT-X-PRELOAD-HINT:TYPE=PART,URI="k_video1_part8.mp4"
            """);
        var t0 = DateTimeOffset.Parse("2026-11-01T15:00:00Z").ToUnixTimeMilliseconds();

        // [3 s, 4 s) is in seg2's parts; the keyframe before it opens part1 at 2 s.
        HlsGroupCallStreamService.SelectWindow(manifest, t0 + 3000, t0 + 4000, startOnKeyframe: true)
            .Select(fragment => fragment.Uri).ShouldBe(["k_video1_part1.mp4", "k_video1_part2.mp4", "k_video1_part3.mp4", "k_video1_part4.mp4"]);
        // Audio needs no keyframe, so only the parts in the second.
        HlsGroupCallStreamService.SelectWindow(manifest, t0 + 3000, t0 + 4000, startOnKeyframe: false)
            .Select(fragment => fragment.Uri).ShouldBe(["k_video1_part3.mp4", "k_video1_part4.mp4"]);
        // Older than the parts: whole segments.
        HlsGroupCallStreamService.SelectWindow(manifest, t0 + 1000, t0 + 2000, startOnKeyframe: true)
            .Select(fragment => fragment.Uri).ShouldBe(["k_video1_seg1.mp4"]);
        // [5 s, 6 s) is still being written (parts run to 5.5 s): not yet, rather than a short part.
        Should.Throw<RpcException>(() => HlsGroupCallStreamService.SelectWindow(manifest, t0 + 5000, t0 + 6000, startOnKeyframe: true))
            .RpcError.Message.ShouldBe("TIME_TOO_BIG");
        Should.Throw<RpcException>(() => HlsGroupCallStreamService.SelectWindow(manifest, t0 - 1000, t0, startOnKeyframe: true))
            .RpcError.Message.ShouldBe("TIME_TOO_SMALL");
        // The newest complete second starts at 4 s.
        HlsGroupCallStreamService.LastCompletePartTimestamp(t0 + 5500).ShouldBe(t0 + 4000);
    }

    private static async Task<byte[]> FragmentAsync(IFfmpegLocator locator, string directory, string name, string input)
    {
        var path = Path.Combine(directory, name + ".mp4");
        await RunAsync(locator.FfmpegPath!, $"-v error -y {input} -movflags frag_keyframe+empty_moov+default_base_moof -frag_duration 200000 -f mp4 {path}");
        return await File.ReadAllBytesAsync(path);
    }

    private static Task<string[]> StreamTypesAsync(IFfmpegLocator locator, string directory, byte[] mp4) =>
        ProbeAsync(locator, directory, mp4, "-show_entries stream=codec_type");

    private static async Task<string[]> ProbeAsync(IFfmpegLocator locator, string directory, byte[] mp4, string query)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".mp4");
        await File.WriteAllBytesAsync(path, mp4);
        var output = await RunAsync(locator.FfprobePath!, $"-v error {query} -of csv=p=0 {path}");
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
