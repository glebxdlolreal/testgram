namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get more info about a Seamless Telegram Login authorization request, for more info <a href="https://corefork.telegram.org/api/url-authorization">click here »</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.requestUrlAuth"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class RequestUrlAuthHandler(MyTelegram.Messenger.Services.Bots.IUrlAuthService urlAuthService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestRequestUrlAuth, MyTelegram.Schema.IUrlAuthResult>
{
    protected override Task<MyTelegram.Schema.IUrlAuthResult> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestRequestUrlAuth obj)
    {
        return urlAuthService.RequestAsync(input, obj.Peer, obj.MsgId, obj.ButtonId, obj.Url);
    }
}