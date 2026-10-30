using EventFlow.Queries;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MyTelegram.Core;
using MyTelegram.Converters.Responses;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Handlers.LatestLayer.Updates;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Pts;
using MyTelegram.Messenger.Tests.SecretChat;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Updates;

namespace MyTelegram.Messenger.Tests.AntiSpam;

/// <summary>Exercises the RPC and real cursor converter against deterministic publication/read interleavings.</summary>
public sealed class ChannelDifferencePublicationTests
{
    [Theory]
    [InlineData(90)]
    [InlineData(110)]
    public async Task Active_publication_returns_no_payload_and_preserves_even_cursor_above_ceiling(int pts)
    {
        var h = new Harness { Ceiling = 99 };
        h.AddDeletion();
        h.AddMessage(101);

        var result = Assert.IsType<TChannelDifferenceEmpty>(await h.Invoke(pts));

        Assert.Equal(pts, result.Pts);
        Assert.False(result.Final);
        Assert.Equal(1, result.Timeout);
        Assert.Equal(0, h.UpdateReads);
        Assert.Equal(0, h.MessageReads);
        Assert.Empty(h.Acks.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_started_during_reads_blocks_updates_messages_cursor_and_ack(bool duringMessageRead)
    {
        var h = new Harness();
        h.AddDeletion();
        h.AddMessage(101);
        if (duringMessageRead) h.DuringMessages = () => h.Ceiling = 99;
        else h.DuringUpdates = () => h.Ceiling = 99;

        var result = Assert.IsType<TChannelDifferenceEmpty>(await h.Invoke(90));

        Assert.Equal(90, result.Pts);
        Assert.False(result.Final);
        Assert.Equal(1, result.Timeout);
        Assert.Equal(1, h.UpdateReads);
        Assert.Equal(1, h.MessageReads);
        Assert.Empty(h.Acks.Calls);
    }

    [Fact]
    public async Task Publication_cleared_during_empty_read_does_not_skip_delete_and_next_offline_request_recovers_it()
    {
        var h = new Harness();
        // The update query takes its snapshot before the completion becomes durable.
        h.DuringUpdates = () => h.Ceiling = 99;
        h.DuringMessages = () =>
        {
            h.AddDeletion();
            h.Ceiling = null; // publication acknowledgement follows the durable row
        };

        var stale = Assert.IsType<TChannelDifferenceEmpty>(await h.Invoke(90));
        Assert.Equal(90, stale.Pts); // dto.Pts is already 101, but nothing was delivered
        Assert.Empty(h.Acks.Calls);

        h.DuringUpdates = null;
        h.DuringMessages = null;
        var recovered = Assert.IsType<TChannelDifference>(await h.Invoke(stale.Pts));
        var deletion = Assert.IsType<TUpdateDeleteChannelMessages>(Assert.Single(recovered.OtherUpdates));
        Assert.Equal(new[] { 42 }, deletion.Messages.ToArray());
        Assert.Equal(Harness.ChannelId, deletion.ChannelId);
        Assert.Equal(100, deletion.Pts);
        Assert.Equal(1, deletion.PtsCount);
        Assert.Equal(100, recovered.Pts);
        Assert.False(recovered.Final);
        Assert.Empty(recovered.NewMessages);
        Assert.Equal(Harness.GlobalSeq, Assert.Single(h.Acks.Calls).GlobalSeqNo);

        // A different/offline session quoting its old cursor must still recover the durable delete.
        var offline = Assert.IsType<TChannelDifference>(await h.Invoke(90, 777));
        Assert.IsType<TUpdateDeleteChannelMessages>(Assert.Single(offline.OtherUpdates));
        Assert.Equal(100, offline.Pts);
        var acknowledged = Assert.IsType<TChannelDifferenceEmpty>(await h.Invoke(recovered.Pts));
        Assert.Equal(100, acknowledged.Pts);
    }

    [Fact]
    public async Task Published_rows_pass_real_converter_with_matching_payload_pts_and_64_bit_ack()
    {
        var h = new Harness();
        h.AddDeletion();
        h.AddMessage(101);

        var result = Assert.IsType<TChannelDifference>(await h.Invoke(90));

        Assert.Equal(101, result.Pts);
        Assert.True(result.Final);
        Assert.Equal(43, Assert.IsType<TMessageEmpty>(Assert.Single(result.NewMessages)).Id);
        Assert.Equal(100, Assert.IsType<TUpdateDeleteChannelMessages>(Assert.Single(result.OtherUpdates)).Pts);
        Assert.Equal(Harness.GlobalSeq + 1, Assert.Single(h.Acks.Calls).GlobalSeqNo);
    }

    private sealed class Harness : IQueryProcessor
    {
        public const long ChannelId = 800_000_000_123;
        public const long GlobalSeq = 4_294_967_299L;
        private readonly List<IUpdatesReadModel> _rows = [];
        private readonly List<IMessageReadModel> _messages = [];
        private readonly GetChannelDifferenceHandler _handler;
        public RecordingAckCacheService Acks { get; } = new();
        public int? Ceiling { get; set; }
        public Action? DuringUpdates { get; set; }
        public Action? DuringMessages { get; set; }
        public int UpdateReads { get; private set; }
        public int MessageReads { get; private set; }

        public Harness()
        {
            var channel = Mock.Of<IChannelReadModel>(x => x.ChannelId == ChannelId);
            var channels = new Mock<IChannelAppService>(MockBehavior.Strict);
            channels.Setup(x => x.GetAsync(It.Is<long?>(id => id == ChannelId)))
                .ReturnsAsync(channel);
            channels.Setup(x => x.GetAsync(It.Is<long>(id => id == ChannelId)))
                .ReturnsAsync(channel);
            channels.Setup(x => x.SendRpcErrorIfNotChannelMemberAsync(
                    It.IsAny<IRequestInput>(), channel))
                .ReturnsAsync(false);
            var allocator = new Mock<IChannelPtsAllocator>(MockBehavior.Strict);
            allocator.Setup(x => x.GetReadablePtsCeilingAsync(ChannelId, It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(Ceiling));
            var messages = new Mock<IMessageAppService>(MockBehavior.Strict);
            messages.Setup(x => x.GetChannelDifferenceAsync(It.IsAny<GetDifferenceInput>()))
                .Returns((GetDifferenceInput _) =>
                {
                    MessageReads++;
                    DuringMessages?.Invoke();
                    return Task.FromResult(new GetMessageOutput
                    {
                        Pts = 101, SelfUserId = 1001, MessageList = _messages.ToArray()
                    });
                });
            var messageConverter = new Mock<IMessageConverterService>();
            messageConverter.Setup(x => x.ToMessageList(It.IsAny<long>(),
                    It.IsAny<IReadOnlyCollection<IMessageReadModel>>(),
                    It.IsAny<IReadOnlyCollection<IPollReadModel>>(),
                    It.IsAny<IReadOnlyCollection<IPollAnswerVoterReadModel>>(),
                    It.IsAny<IReadOnlyCollection<IUserReactionReadModel>>(), It.IsAny<int>()))
                .Returns((long _, IReadOnlyCollection<IMessageReadModel> rows,
                    IReadOnlyCollection<IPollReadModel>? polls,
                    IReadOnlyCollection<IPollAnswerVoterReadModel>? answers,
                    IReadOnlyCollection<IUserReactionReadModel>? reactions, int layer) =>
                    rows.Select(x => (IMessage)new TMessageEmpty { Id = x.MessageId }).ToList());
            var updates = new Mock<IUpdatesResponseService>();
            updates.Setup(x => x.ToLayeredData(It.IsAny<long>(), It.IsAny<long>(),
                    It.IsAny<IUpdate>(), It.IsAny<int>(), null))
                .Returns((long userId, long key, IUpdate value, int layer, LayeredResponseExtraData? extra) => value);
            var converter = new DifferenceConverterService(null!,
                Mock.Of<IChatConverterService>(), Mock.Of<IUserConverterService>(), messageConverter.Object,
                updates.Object, Mock.Of<IMinConstructorReducer>(), null!, null!);
            _handler = new GetChannelDifferenceHandler(messages.Object, this, Acks, converter,
                NullLogger<GetChannelDifferenceHandler>.Instance, channels.Object, allocator.Object);
        }

        public void AddDeletion()
        {
            var update = new TUpdateDeleteChannelMessages
            {
                ChannelId = ChannelId, Messages = new TVector<int> { 42 }, Pts = 100, PtsCount = 1
            };
            _rows.Add(Mock.Of<IUpdatesReadModel>(x => x.Pts == 100 && x.GlobalSeqNo == GlobalSeq &&
                x.UpdatesType == UpdatesType.Updates && x.Updates == new List<IUpdate> { update }));
        }

        public void AddMessage(int pts)
        {
            _messages.Add(Mock.Of<IMessageReadModel>(x => x.MessageId == 43 && x.Pts == pts));
            _rows.Add(Mock.Of<IUpdatesReadModel>(x => x.Pts == pts && x.GlobalSeqNo == GlobalSeq + 1 &&
                x.UpdatesType == UpdatesType.NewMessages && x.MessageId == 43));
        }

        public async Task<IChannelDifference> Invoke(int pts, long authKey = 111)
        {
            var rpc = Assert.IsType<TRpcResult>(await _handler.HandleAsync(
                SecretChatTestHarness.Input(1001, authKey, 222), new RequestGetChannelDifference
                {
                    Channel = new TInputChannel { ChannelId = ChannelId, AccessHash = 123 },
                    Filter = new TChannelMessagesFilterEmpty(), Pts = pts, Limit = 100
                }));
            return Assert.IsAssignableFrom<IChannelDifference>(rpc.Result);
        }

        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
        {
            object result;
            switch (query)
            {
                case GetChannelMemberByUserIdQuery:
                    result = Mock.Of<IChannelMemberReadModel>();
                    break;
                case GetUpdatesQuery q:
                    UpdateReads++;
                    result = (IReadOnlyCollection<IUpdatesReadModel>)_rows.Where(x => x.Pts > q.MinPts).ToArray();
                    DuringUpdates?.Invoke();
                    break;
                default:
                    throw new NotSupportedException($"Unexpected query {query.GetType().Name}");
            }
            return Task.FromResult((TResult)result);
        }
    }
}
