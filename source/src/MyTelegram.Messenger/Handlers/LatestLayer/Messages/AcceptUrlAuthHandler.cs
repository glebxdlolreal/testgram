namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Use this to accept a Seamless Telegram Login authorization request, for more info <a href="https://corefork.telegram.org/api/url-authorization">click here »</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.acceptUrlAuth"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class AcceptUrlAuthHandler(MyTelegram.Messenger.Services.Bots.IUrlAuthService urlAuthService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestAcceptUrlAuth, MyTelegram.Schema.IUrlAuthResult>
{
    protected override Task<MyTelegram.Schema.IUrlAuthResult> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestAcceptUrlAuth obj)
    {
        return urlAuthService.AcceptAsync(input, obj.Peer, obj.MsgId, obj.ButtonId, obj.Url, obj.WriteAllowed);
    }
}