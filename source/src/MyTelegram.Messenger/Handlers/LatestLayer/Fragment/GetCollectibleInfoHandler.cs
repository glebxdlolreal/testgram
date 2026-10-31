using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Messenger.Services.Fragment;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Fragment;

/// <summary>
/// Fetch information about a fragment collectible (username or phone number purchased on Fragment.com).
/// It only answers for collectibles the caller can see: their own, a username its holder has activated,
/// or a phone number the holder's privacy settings show to the caller.
/// Possible errors
/// Code Type Description
/// 400 COLLECTIBLE_INVALID The specified collectible is invalid.
/// 400 COLLECTIBLE_NOT_FOUND The specified collectible could not be found.
/// See https://core.telegram.org/method/fragment.getCollectibleInfo and
/// https://corefork.telegram.org/api/fragment
/// </summary>
internal sealed class GetCollectibleInfoHandler(
    IFragmentCollectibleCatalog collectibleCatalog,
    IQueryProcessor queryProcessor,
    IUserAppService userAppService,
    IChannelAppService channelAppService,
    IUserConverterService userConverterService,
    IBotOwnershipChecker botOwnershipChecker)
    : RpcResultObjectHandler<MyTelegram.Schema.Fragment.RequestGetCollectibleInfo, MyTelegram.Schema.Fragment.ICollectibleInfo>
{
    protected override async Task<MyTelegram.Schema.Fragment.ICollectibleInfo> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Fragment.RequestGetCollectibleInfo obj)
    {
        FragmentCollectible? collectible;
        bool visible;
        switch (obj.Collectible)
        {
            case TInputCollectibleUsername { Username: { Length: > 0 } username }:
                collectible = await collectibleCatalog.FindUsernameAsync(username);
                visible = collectible != null && await IsUsernameVisibleAsync(input, username);
                break;
            case TInputCollectiblePhone { Phone: { Length: > 0 } phone }
                when FragmentCollectibleCatalog.NormalizePhone(phone).Length > 0:
                collectible = await collectibleCatalog.FindPhoneAsync(phone);
                visible = collectible != null && await IsPhoneVisibleAsync(input, collectible.Phone!);
                break;
            default:
                RpcErrors.RpcErrors400.CollectibleInvalid.ThrowRpcError();
                return null!;
        }

        if (!visible)
        {
            RpcErrors.RpcErrors400.CollectibleNotFound.ThrowRpcError();
        }

        return new MyTelegram.Schema.Fragment.TCollectibleInfo
        {
            PurchaseDate = collectible!.PurchaseDate,
            Currency = collectible.Currency,
            Amount = collectible.Amount,
            CryptoCurrency = collectible.CryptoCurrency,
            CryptoAmount = collectible.CryptoAmount,
            Url = collectible.Url
        };
    }

    private async Task<bool> IsUsernameVisibleAsync(IRequestInput input, string username)
    {
        var holder = await queryProcessor.ProcessAsync(new GetUserNameByNameQuery(username.ToLowerInvariant()));
        switch (holder?.PeerType)
        {
            case PeerType.User:
                if (holder.PeerId == input.UserId)
                {
                    return true;
                }

                var user = await userAppService.GetAsync((long?)holder.PeerId);
                if (user == null || !IsAttached(user.Usernames, username, out var userActive))
                {
                    return false;
                }

                return userActive || (user.Bot && await botOwnershipChecker.IsOwnerAsync(user.UserId, input.UserId));
            case PeerType.Channel:
                var channel = await channelAppService.GetAsync((long?)holder.PeerId);
                if (channel == null || !IsAttached(channel.Usernames, username, out var channelActive))
                {
                    return false;
                }

                return channelActive || channel.CreatorId == input.UserId;
            default:
                // Listed on Fragment but held by nobody.
                return false;
        }
    }

    private async Task<bool> IsPhoneVisibleAsync(IRequestInput input, string phone)
    {
        var holder = await queryProcessor.ProcessAsync(new GetUserByPhoneNumberQuery(phone));
        if (holder == null)
        {
            return false;
        }

        if (holder.UserId == input.UserId)
        {
            return true;
        }

        // The converter applies the holder's phone number privacy rules for this caller.
        var user = await userConverterService.GetUserAsync(input, holder.UserId, skipSetContactProperties: false,
            layer: input.Layer);

        return user is TUser { Phone.Length: > 0 };
    }

    private static bool IsAttached(IEnumerable<UsernameInfo>? usernames, string username, out bool active)
    {
        var item = usernames?.FirstOrDefault(p =>
            !p.Editable && string.Equals(p.Username, username, StringComparison.OrdinalIgnoreCase));
        active = item?.Active == true;

        return item != null;
    }
}
