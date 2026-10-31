using MyTelegram.Schema.Help;

namespace MyTelegram.Converters.TLObjects.LatestLayer;

public class PremiumPromoConverter : IPremiumPromoConverter, ITransientDependency
{

    public virtual int Layer => Layers.LayerLatest;

    public virtual IPremiumPromo ToPremiumPromo()
    {
        return new TPremiumPromo
        {
            StatusText =
                "By subscribing to Testgram Premium you agree to the Testgram Terms of Service and Privacy Policy.",
            StatusEntities = new TVector<IMessageEntity>(),
            Users = new TVector<IUser>(),
            VideoSections = new TVector<string>
            {
                "more_upload", "faster_download", "voice_to_text",
                "no_ads", "unique_reactions", "premium_stickers",
                "advanced_chat_management", "profile_badge",
                "animated_userpics", "app_icons"
            },
            Videos = new TVector<IDocument>(),
            // Filled per caller by GetPremiumPromoHandler from the configured price table: the terms
            // on offer depend on whether Premium is on sale to that account.
            PeriodOptions = new TVector<IPremiumSubscriptionOption>()
        };
    }
}