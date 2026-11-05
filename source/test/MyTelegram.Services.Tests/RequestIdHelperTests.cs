using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests;

/// <summary>
/// Feature: one RequestId per request, not per frame.
///
/// <para>
/// The gateway stamps one RequestId per encrypted frame and the session-server gives it to every message of
/// a msg_container. Sagas are keyed by it, so 30 sendMessage calls packed into one container became one
/// message and 29 calls that never got an answer (seen 2026-11-05 with a Telethon bot).
/// </para>
/// </summary>
public class RequestIdHelperTests
{
    private static readonly Guid Frame = Guid.Parse("abc72a3f-04f6-400e-be70-0e99857289eb");

    [Fact]
    public void Messages_of_one_frame_get_different_ids()
    {
        var ids = Enumerable.Range(0, 40).Select(i => RequestIdHelper.ForMessage(Frame, 7704720838742215332 + i * 4)).ToList();

        ids.Distinct().Count().ShouldBe(40);
    }

    [Fact]
    public void A_message_gets_the_same_id_every_time()
    {
        RequestIdHelper.ForMessage(Frame, 7704720838742215332).ShouldBe(RequestIdHelper.ForMessage(Frame, 7704720838742215332));
    }

    [Fact]
    public void Other_frames_do_not_collide()
    {
        RequestIdHelper.ForMessage(Frame, 1).ShouldNotBe(RequestIdHelper.ForMessage(Guid.NewGuid(), 1));
    }

    [Fact]
    public void A_request_without_msg_id_keeps_the_frame_id()
    {
        RequestIdHelper.ForMessage(Frame, 0).ShouldBe(Frame);
    }
}
