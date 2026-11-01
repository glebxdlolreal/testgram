using MyTelegram.Messenger.Services.Business;
using MyTelegram.Messenger.Services.Stickers;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Set or remove the <a href="https://corefork.telegram.org/api/business#business-introduction">Telegram Business introduction »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updateBusinessIntro"/> </c></para>
/// </summary>
internal sealed class UpdateBusinessIntroHandler(
    ICommandBus commandBus,
    IUserAppService userAppService,
    IAccessHashHelper2 accessHashHelper,
    IStickerDocumentValidator stickerDocumentValidator,
    IBusinessProfileService businessProfileService)
    : RpcResultObjectHandler<RequestUpdateBusinessIntro, IBool>
{
    /// <summary>appConfig <c>intro_title_length_limit</c>.</summary>
    private const int TitleLengthLimit = 32;

    /// <summary>appConfig <c>intro_description_length_limit</c>.</summary>
    private const int DescriptionLengthLimit = 70;

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestUpdateBusinessIntro obj)
    {
        BusinessIntroItem? item = null;
        if (obj.Intro is TInputBusinessIntro intro)
        {
            await userAppService.CheckAccountPremiumStatusAsync(input.UserId);

            long? stickerId = null;
            if (intro.Sticker is TInputDocument inputDocument)
            {
                await accessHashHelper.CheckAccessHashAsync(input, inputDocument.Id, inputDocument.AccessHash,
                    AccessHashType.Document);
                if (!await stickerDocumentValidator.IsStickerAsync(inputDocument.Id))
                {
                    RpcErrors.RpcErrors400.StickerDocumentInvalid.ThrowRpcError();
                }

                stickerId = inputDocument.Id;
            }

            // Clients enforce the advertised limits; anything longer is cut rather than refused, as the
            // method documents no error for it.
            item = new BusinessIntroItem(Truncate(intro.Title, TitleLengthLimit),
                Truncate(intro.Description, DescriptionLengthLimit), stickerId);
        }

        await commandBus.PublishAsync(new UpdateBusinessIntroCommand(UserId.Create(input.UserId), item));
        await businessProfileService.NotifyUserUpdatedAsync(input);

        return new TBoolTrue();
    }

    private static string Truncate(string? value, int limit)
    {
        value ??= string.Empty;
        var info = new System.Globalization.StringInfo(value);
        return info.LengthInTextElements <= limit ? value : info.SubstringByTextElements(0, limit);
    }
}
