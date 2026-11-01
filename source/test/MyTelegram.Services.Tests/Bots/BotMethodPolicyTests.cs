using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Bots;

public class BotMethodPolicyTests
{
    private const long BotId = MyTelegramConsts.BotUserInitId + 5;
    private const long UserId = 2010001;

    public static TheoryData<IObject> UserOnlyMethods() => new()
    {
        new MyTelegram.Schema.Messages.RequestGetHistory(),
        new MyTelegram.Schema.Messages.LayerN.RequestGetHistory(),
        new MyTelegram.Schema.Messages.RequestGetDialogs(),
        new MyTelegram.Schema.Messages.RequestSearch(),
        new MyTelegram.Schema.Channels.RequestInviteToChannel(),
        new MyTelegram.Schema.Channels.RequestToggleSlowMode(),
        new MyTelegram.Schema.Channels.RequestGetAdminLog(),
        new MyTelegram.Schema.Channels.RequestDeleteParticipantHistory(),
        new MyTelegram.Schema.Channels.RequestToggleAntiSpam(),
        new MyTelegram.Schema.Channels.RequestToggleSignatures()
    };

    [Theory]
    [MemberData(nameof(UserOnlyMethods))]
    public void UserOnlyMethods_AreRefusedToBots_ButNotToUsers(IObject request)
    {
        BotMethodPolicy.IsForbiddenForBot(BotId, request).ShouldBeTrue();
        BotMethodPolicy.IsForbiddenForBot(UserId, request).ShouldBeFalse();
    }

    [Fact]
    public void AWrappedRequest_IsJudgedByItsInnermostQuery()
    {
        var wrapped = new RequestInvokeWithLayer
        {
            Layer = 222,
            Query = new RequestInvokeWithoutUpdates { Query = new MyTelegram.Schema.Messages.RequestGetDialogs() }
        };

        BotMethodPolicy.IsForbiddenForBot(BotId, wrapped).ShouldBeTrue();
    }

    [Fact]
    public void OrdinaryBotMethods_AreAllowed()
    {
        BotMethodPolicy.IsForbiddenForBot(BotId, new MyTelegram.Schema.Messages.RequestSendMessage()).ShouldBeFalse();
        BotMethodPolicy.IsForbiddenForBot(BotId, new MyTelegram.Schema.Channels.RequestEditBanned()).ShouldBeFalse();
    }
}
