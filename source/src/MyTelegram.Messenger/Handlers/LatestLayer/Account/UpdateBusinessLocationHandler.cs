using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// <a href="https://corefork.telegram.org/api/business#location">Businesses »</a> may advertise their location using this method, see <a href="https://corefork.telegram.org/api/business#location">here »</a> for more info.<br/>
/// To remove business location information invoke the method without setting any of the parameters.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updateBusinessLocation"/> </c></para>
/// </summary>
internal sealed class UpdateBusinessLocationHandler(
    ICommandBus commandBus,
    IUserAppService userAppService,
    IBusinessProfileService businessProfileService)
    : RpcResultObjectHandler<RequestUpdateBusinessLocation, IBool>
{
    /// <summary>"a textual description of the address (max 96 UTF-8 chars)".</summary>
    private const int MaxAddressLength = 96;

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestUpdateBusinessLocation obj)
    {
        BusinessLocationItem? item = null;
        if (obj.Address != null || obj.GeoPoint != null)
        {
            // The address is mandatory whenever a location is set; a point alone is not a location.
            var address = obj.Address?.Trim();
            if (string.IsNullOrEmpty(address) || address.Length > MaxAddressLength)
            {
                RpcErrors.RpcErrors400.AddressInvalid.ThrowRpcError();
            }

            await userAppService.CheckAccountPremiumStatusAsync(input.UserId);
            var point = obj.GeoPoint as TInputGeoPoint;
            item = new BusinessLocationItem(address!, point?.Lat, point?.Long, point?.AccuracyRadius);
        }

        await commandBus.PublishAsync(new UpdateBusinessLocationCommand(UserId.Create(input.UserId), item));
        await businessProfileService.NotifyUserUpdatedAsync(input);

        return new TBoolTrue();
    }
}
