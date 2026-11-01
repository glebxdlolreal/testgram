using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Logging;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get peer settings
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_MONOFORUM_UNSUPPORTED <a href="https://corefork.telegram.org/api/channel#monoforums">Monoforums</a> do not support this feature.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getPeerSettings"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetPeerSettingsHandler(
    MyTelegram.Messenger.Services.Business.IBusinessBotNotifier businessBotNotifier,
    IPeerSettingsAppService peerSettingsAppService,
    IPeerHelper peerHelper,
    IObjectMapper objectMapper,
    IQueryProcessor queryProcessor,
    IContactAppService contactAppService,
    IChannelAppService channelAppService,
    ILayeredService<IPeerSettingsConverter> layeredService,
    IMongoDatabase database,
    ILogger<GetPeerSettingsHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetPeerSettings, MyTelegram.Schema.Messages.IPeerSettings>
{
    protected override async Task<MyTelegram.Schema.Messages.IPeerSettings> HandleCoreAsync(IRequestInput input, RequestGetPeerSettings obj)
    {
        var userId = input.UserId;
        var peer = peerHelper.GetPeer(obj.Peer, userId);
        if (peer.PeerType == PeerType.Channel)
        {
            if (await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, peer.PeerId))
            {
                return null !;
            }
        }

        if (peer.PeerId == MyTelegramConsts.NotificationServiceUserId || peer.PeerType == PeerType.Self)
        {
            return new MyTelegram.Schema.Messages.TPeerSettings
            {
                Chats = new(),
                Users = new(),
                Settings = new Schema.TPeerSettings()
            };
        }

        //IContactReadModel? contactReadModel = null;
        ContactType? contactType = null;
        if (peer.PeerType == PeerType.User)
        {
            //contactReadModel = await _queryProcessor.ProcessAsync(new GetContactQuery(userId, peer.PeerId));
            var contactReadModels = await queryProcessor.ProcessAsync(new GetContactListBySelfIdAndTargetUserIdQuery(input.UserId, peer.PeerId));
            contactType = contactAppService.GetContactType(input.UserId, peer.PeerId, contactReadModels);
        }

        var r = await peerSettingsAppService.GetPeerSettingsAsync(userId, peer.PeerId);
        var settings = layeredService.GetConverter(input.Layer).ToPeerSettings(input.UserId, peer.PeerId, r, contactType);
        if (r == null && peer.PeerType == PeerType.Channel)
        {
            settings = new MyTelegram.Schema.TPeerSettings();
        }

        if (peer.PeerType == PeerType.User)
        {
            var targetGps = await queryProcessor.ProcessAsync(new GetGlobalPrivacySettingsQuery(peer.PeerId));
            if (targetGps?.NoncontactPeersPaidStars > 0)
                settings.ChargePaidMessageStars = targetGps.NoncontactPeersPaidStars;

            // Get user info for additional fields from MongoDB
            if (settings is Schema.TPeerSettings tSettings)
            {
                // Item 18: per Telegram's peerSettings docs the "doxxing" fields
                // (registration_month, phone_country, name_change_date, photo_change_date)
                // must only appear when the peer is suspicious — i.e. they wrote first
                // and have not yet been confirmed as a contact. Once we've replied (any
                // outbox message exists) or saved them as a contact, the fields must be
                // hidden, otherwise a known contact's profile would still leak when
                // opening peerSettings.
                var shouldExposeProfileMeta = contactType is null or ContactType.None;
                if (shouldExposeProfileMeta)
                {
                    var msgCol = database.GetCollection<BsonDocument>("eventflow-messagereadmodel");
                    var weReplied = await msgCol.Find(Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("OwnerPeerId", input.UserId),
                        Builders<BsonDocument>.Filter.Eq("ToPeerId", peer.PeerId),
                        Builders<BsonDocument>.Filter.Eq("ToPeerType", (int)PeerType.User),
                        Builders<BsonDocument>.Filter.Eq("Out", true)
                    )).Project(Builders<BsonDocument>.Projection.Include("_id")).Limit(1).AnyAsync();
                    if (weReplied) shouldExposeProfileMeta = false;
                }
                var userCol = database.GetCollection<BsonDocument>("eventflow-userreadmodel");
                var userDoc = await userCol.Find(Builders<BsonDocument>.Filter.Eq("UserId", peer.PeerId)).FirstOrDefaultAsync();
                if (userDoc != null && shouldExposeProfileMeta)
                {
                    // Phone country - extract from phone number
                    if (userDoc.Contains("PhoneNumber") && !userDoc["PhoneNumber"].IsBsonNull)
                    {
                        var phoneNumber = userDoc["PhoneNumber"].AsString;
                        if (!string.IsNullOrEmpty(phoneNumber) && phoneNumber.Length >= 1)
                        {
                            // Comprehensive country code mapping
                            tSettings.PhoneCountry = phoneNumber switch
                            {
                                // Special codes
                                var p when p.StartsWith("888") => "FRAGMENT", // Fragment.com anonymous numbers
                                var p when p.StartsWith("999") => "TEST", // Telegram Test DC
                                var p when p.StartsWith("881") => "SATELLITE", // Satellite networks
                                var p when p.StartsWith("882") => "INTL", // International networks
                                var p when p.StartsWith("883") => "INTL", // International networks
                                var p when p.StartsWith("878") => "VOIP", // VoIP services

                                // North America
                                var p when p.StartsWith("1") => "US", // USA/Canada

                                // Europe
                                var p when p.StartsWith("7") => "RU", // Russia/Kazakhstan
                                var p when p.StartsWith("20") => "EG", // Egypt
                                var p when p.StartsWith("27") => "ZA", // South Africa
                                var p when p.StartsWith("30") => "GR", // Greece
                                var p when p.StartsWith("31") => "NL", // Netherlands
                                var p when p.StartsWith("32") => "BE", // Belgium
                                var p when p.StartsWith("33") => "FR", // France
                                var p when p.StartsWith("34") => "ES", // Spain
                                var p when p.StartsWith("36") => "HU", // Hungary
                                var p when p.StartsWith("39") => "IT", // Italy
                                var p when p.StartsWith("40") => "RO", // Romania
                                var p when p.StartsWith("41") => "CH", // Switzerland
                                var p when p.StartsWith("43") => "AT", // Austria
                                var p when p.StartsWith("44") => "GB", // UK
                                var p when p.StartsWith("45") => "DK", // Denmark
                                var p when p.StartsWith("46") => "SE", // Sweden
                                var p when p.StartsWith("47") => "NO", // Norway
                                var p when p.StartsWith("48") => "PL", // Poland
                                var p when p.StartsWith("49") => "DE", // Germany

                                // Asia
                                var p when p.StartsWith("51") => "PE", // Peru
                                var p when p.StartsWith("52") => "MX", // Mexico
                                var p when p.StartsWith("53") => "CU", // Cuba
                                var p when p.StartsWith("54") => "AR", // Argentina
                                var p when p.StartsWith("55") => "BR", // Brazil
                                var p when p.StartsWith("56") => "CL", // Chile
                                var p when p.StartsWith("57") => "CO", // Colombia
                                var p when p.StartsWith("58") => "VE", // Venezuela
                                var p when p.StartsWith("60") => "MY", // Malaysia
                                var p when p.StartsWith("61") => "AU", // Australia
                                var p when p.StartsWith("62") => "ID", // Indonesia
                                var p when p.StartsWith("63") => "PH", // Philippines
                                var p when p.StartsWith("64") => "NZ", // New Zealand
                                var p when p.StartsWith("65") => "SG", // Singapore
                                var p when p.StartsWith("66") => "TH", // Thailand
                                var p when p.StartsWith("81") => "JP", // Japan
                                var p when p.StartsWith("82") => "KR", // South Korea
                                var p when p.StartsWith("84") => "VN", // Vietnam
                                var p when p.StartsWith("86") => "CN", // China
                                var p when p.StartsWith("90") => "TR", // Turkey
                                var p when p.StartsWith("91") => "IN", // India
                                var p when p.StartsWith("92") => "PK", // Pakistan
                                var p when p.StartsWith("93") => "AF", // Afghanistan
                                var p when p.StartsWith("94") => "LK", // Sri Lanka
                                var p when p.StartsWith("95") => "MM", // Myanmar
                                var p when p.StartsWith("98") => "IR", // Iran

                                // CIS & Eastern Europe
                                var p when p.StartsWith("370") => "LT", // Lithuania
                                var p when p.StartsWith("371") => "LV", // Latvia
                                var p when p.StartsWith("372") => "EE", // Estonia
                                var p when p.StartsWith("373") => "MD", // Moldova
                                var p when p.StartsWith("374") => "AM", // Armenia
                                var p when p.StartsWith("375") => "BY", // Belarus
                                var p when p.StartsWith("376") => "AD", // Andorra
                                var p when p.StartsWith("377") => "MC", // Monaco
                                var p when p.StartsWith("380") => "UA", // Ukraine
                                var p when p.StartsWith("381") => "RS", // Serbia
                                var p when p.StartsWith("382") => "ME", // Montenegro
                                var p when p.StartsWith("385") => "HR", // Croatia
                                var p when p.StartsWith("386") => "SI", // Slovenia
                                var p when p.StartsWith("387") => "BA", // Bosnia
                                var p when p.StartsWith("389") => "MK", // North Macedonia

                                // Middle East
                                var p when p.StartsWith("961") => "LB", // Lebanon
                                var p when p.StartsWith("962") => "JO", // Jordan
                                var p when p.StartsWith("963") => "SY", // Syria
                                var p when p.StartsWith("964") => "IQ", // Iraq
                                var p when p.StartsWith("965") => "KW", // Kuwait
                                var p when p.StartsWith("966") => "SA", // Saudi Arabia
                                var p when p.StartsWith("967") => "YE", // Yemen
                                var p when p.StartsWith("968") => "OM", // Oman
                                var p when p.StartsWith("970") => "PS", // Palestine
                                var p when p.StartsWith("971") => "AE", // UAE
                                var p when p.StartsWith("972") => "IL", // Israel
                                var p when p.StartsWith("973") => "BH", // Bahrain
                                var p when p.StartsWith("974") => "QA", // Qatar
                                var p when p.StartsWith("975") => "BT", // Bhutan
                                var p when p.StartsWith("976") => "MN", // Mongolia
                                var p when p.StartsWith("977") => "NP", // Nepal

                                // Central Asia
                                var p when p.StartsWith("992") => "TJ", // Tajikistan
                                var p when p.StartsWith("993") => "TM", // Turkmenistan
                                var p when p.StartsWith("994") => "AZ", // Azerbaijan
                                var p when p.StartsWith("995") => "GE", // Georgia
                                var p when p.StartsWith("996") => "KG", // Kyrgyzstan
                                var p when p.StartsWith("998") => "UZ", // Uzbekistan

                                _ => null
                            };
                        }
                    }

                    // Registration month - format as MM.YYYY from user creation date
                    if (userDoc.Contains("Date") && !userDoc["Date"].IsBsonNull)
                    {
                        var registrationTimestamp = userDoc["Date"].AsInt32;
                        var registrationDate = DateTimeOffset.FromUnixTimeSeconds(registrationTimestamp);
                        tSettings.RegistrationMonth = registrationDate.ToString("MM.yyyy");
                    }

                    // Name change date
                    if (userDoc.Contains("NameChangeDate") && !userDoc["NameChangeDate"].IsBsonNull)
                    {
                        tSettings.NameChangeDate = userDoc["NameChangeDate"].AsInt32;
                    }

                    // Photo change date
                    if (userDoc.Contains("PhotoChangeDate") && !userDoc["PhotoChangeDate"].IsBsonNull)
                    {
                        tSettings.PhotoChangeDate = userDoc["PhotoChangeDate"].AsInt32;
                    }
                }
            }
        }

        // Check for connected business bot
        await SetBusinessBotFieldsAsync(settings, userId, peer.PeerId, peer.PeerType);

        var peerSettings = new MyTelegram.Schema.Messages.TPeerSettings
        {
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Settings = settings
        };
        return peerSettings;
    }

    private Task SetBusinessBotFieldsAsync(Schema.IPeerSettings settings, long selfUserId, long targetPeerId, PeerType peerType)
    {
        return peerType == PeerType.User
            ? businessBotNotifier.FillPeerSettingsAsync(settings, selfUserId, targetPeerId)
            : Task.CompletedTask;
    }
}