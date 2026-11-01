namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Associate (or remove) a personal <a href="https://corefork.telegram.org/api/channel">channel »</a>, that will be listed on our personal <a href="https://corefork.telegram.org/api/profile#personal-channel">profile page »</a>.Changing it will emit an <a href="https://corefork.telegram.org/constructor/updateUser">updateUser</a> update.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updatePersonalChannel"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdatePersonalChannelHandler(ICommandBus commandBus, IChannelAppService channelAppService, IAccessHashHelper2 accessHashHelper) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestUpdatePersonalChannel, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestUpdatePersonalChannel obj)
    {
        long? personalChannelId = null;
        switch (obj.Channel)
        {
            case TInputChannel inputChannel:
                await accessHashHelper.CheckAccessHashAsync(input, inputChannel);
                var channelReadModel = await channelAppService.GetAsync((long?)inputChannel.ChannelId);
                // Only a broadcast channel can be shown on a profile; a megagroup has no posts to preview.
                if (channelReadModel == null || channelReadModel.IsDeleted || !channelReadModel.Broadcast)
                {
                    RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
                }

                if (channelReadModel!.CreatorId != input.UserId)
                {
                    RpcErrors.RpcErrors400.ChatIdInvalid.ThrowRpcError();
                }

                personalChannelId = inputChannel.ChannelId;
                break;
        }

        var command = new UpdatePersonalChannelCommand(UserId.Create(input.UserId), input.ToRequestInfo(), personalChannelId);
        await commandBus.PublishAsync(command);
        return new TBoolTrue();
    }
}