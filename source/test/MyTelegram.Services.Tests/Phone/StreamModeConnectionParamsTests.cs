using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Verifies the <c>updateGroupCallConnection</c> stream-mode <c>params</c> emitted by
/// <c>JoinGroupCallHandler</c>, matching the documented shapes at
/// https://corefork.telegram.org/api/group-calls#detecting-stream-mode:
///   * automatically-scaled stream mode (normal video chat/livestream) -> <c>{"stream": true}</c>
///   * RTMP mode (single external publisher)                            -> <c>{"stream": true, "rtmp": true}</c>
/// </summary>
public class StreamModeConnectionParamsTests
{
    private const long CreatorId = 1;
    private const long JoinerId = 2;
    private const long ChannelId = 555;
    private const long CallId = 900;
    private const long AccessHash = 24680;

    [Fact]
    public async Task Join_NonRtmpCall_IsNotPutIntoStreamMode()
    {
        // An ordinary video chat is a live WebRTC call, not a stream. Its transport comes from the media
        // server; with none deployed the join is recorded but no connection parameters are invented.
        var updates = await JoinAsync(rtmpStream: false);

        var tUpdates = updates.ShouldBeOfType<TUpdates>();
        tUpdates.Updates.OfType<TUpdateGroupCallConnection>().ShouldBeEmpty();
        tUpdates.Updates.OfType<TUpdateGroupCallParticipants>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Join_RtmpCall_ReturnsRtmpStreamParams()
    {
        var updates = await JoinAsync(rtmpStream: true);
        var connection = SingleConnection(updates);

        using var doc = JsonDocument.Parse(connection.Params.Data);
        doc.RootElement.GetProperty("stream").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("rtmp").GetBoolean().ShouldBeTrue(
            "an RTMP-mode call must carry {\"stream\": true, \"rtmp\": true}");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static TUpdateGroupCallConnection SingleConnection(IUpdates updates)
        => updates.ShouldBeOfType<TUpdates>().Updates.OfType<TUpdateGroupCallConnection>().ShouldHaveSingleItem();

    private static async Task<IUpdates> JoinAsync(bool rtmpStream)
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        var collection = database.GetCollection<GroupCallDocument>(PhoneTestFixtures.GroupCallsCollectionName);
        await collection.InsertOneAsync(new GroupCallDocument
        {
            Id = CallId,
            CallId = CallId,
            AccessHash = AccessHash,
            CreatorId = CreatorId,
            PeerId = ChannelId,
            PeerType = (int)PeerType.Channel,
            Active = true,
            RtmpStream = rtmpStream,
            Version = 1
        });

        var sender = new CapturingObjectMessageSender();

        var optionsMonitor = new Mock<IOptionsMonitor<MyTelegramMessengerServerOptions>>();
        optionsMonitor.Setup(x => x.CurrentValue).Returns(new MyTelegramMessengerServerOptions());

        // The joining user is a member of the channel (not the creator), i.e. a listener.
        var channelAppService = new Mock<IChannelAppService>();
        channelAppService
            .Setup(x => x.SendRpcErrorIfNotChannelMemberAsync(It.IsAny<IRequestInput>(), It.IsAny<long>()))
            .ReturnsAsync(false);

        var assembly = typeof(GroupCallDocument).Assembly;
        var type = assembly.GetType(
            "MyTelegram.Messenger.Handlers.LatestLayer.Phone.JoinGroupCallHandler",
            throwOnError: true)!;
        var handler = PhoneTestFixtures.CreateGroupCallHandler(
            type, database, new PeerHelper(), sender, optionsMonitor.Object, channelAppService.Object)!;
        var method = type.GetMethod("HandleAsync", new[] { typeof(IRequestInput), typeof(IObject) })!;

        var input = PhoneTestFixtures.RequestInput(JoinerId).Build();
        var request = new RequestJoinGroupCall
        {
            Call = new TInputGroupCall { Id = CallId, AccessHash = AccessHash },
            JoinAs = new TInputPeerSelf(),
            Muted = true,
            VideoStopped = true,
            Params = new TDataJSON { Data = "{\"ssrc\":12345}" }
        };

        object taskObj;
        try
        {
            taskObj = method.Invoke(handler, new object[] { input, request })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }

        var result = await (Task<IObject>)taskObj;
        return (IUpdates)((TRpcResult)result).Result;
    }
}
