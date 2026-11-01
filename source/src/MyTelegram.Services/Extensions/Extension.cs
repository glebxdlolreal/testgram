using MyTelegram.Schema;
using System.Diagnostics.CodeAnalysis;

namespace MyTelegram.Services.Extensions;

public static class Extension
{
    public static IPhoneCallProtocol ToPhoneCallProtocol(this PhoneCallProtocol source)
    {
        return new TPhoneCallProtocol
        {
            LibraryVersions = new TVector<string>(source.LibraryVersions),
            MaxLayer = source.MaxLayer,
            MinLayer = source.MinLayer,
            UdpP2p = source.UdpP2P,
            UdpReflector = source.UdpReflector
        };
    }

    public static IInputPeer ToInputPeer(this InputPeer source)
    {
        switch (source.Peer.PeerType)
        {
            case PeerType.Empty:
            case PeerType.Self:
            case PeerType.User:
                return new TInputPeerUser { AccessHash = source.AccessHash, UserId = source.Peer.PeerId };
            case PeerType.Chat:
                return new TInputPeerChat { ChatId = source.Peer.PeerId };
            case PeerType.Channel:
                return new TInputPeerChannel { AccessHash = source.AccessHash, ChannelId = source.Peer.PeerId };
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public static IChatAdminRights? ToChatAdminRights(this ChatAdminRights? source)
    {
        if (source == null)
        {
            return null;
        }

        return new TChatAdminRights
        {
            ChangeInfo = source.ChangeInfo,
            PostMessages = source.PostMessages,
            EditMessages = source.EditMessages,
            DeleteMessages = source.DeleteMessages,
            BanUsers = source.BanUsers,
            InviteUsers = source.InviteUsers,
            PinMessages = source.PinMessages,
            AddAdmins = source.AddAdmins,
            Anonymous = source.Anonymous,
            ManageCall = source.ManageCall,
            Other = source.Other,
            ManageTopics = source.ManageTopics,
            PostStories = source.PostStories,
            EditStories = source.EditStories,
            DeleteStories = source.DeleteStories,
            ManageDirectMessages = source.ManageDirectMessages,
            ManageRanks = source.ManageRanks
        };
    }

    public static IChatBannedRights? ToChatBannedRights(this ChatBannedRights? source)
    {
        if (source == null)
        {
            return null;
        }

        return new TChatBannedRights
        {
            ViewMessages = source.ViewMessages,
            SendMessages = source.SendMessages,
            SendMedia = source.SendMedia,
            SendStickers = source.SendStickers,
            SendGifs = source.SendGifs,
            SendGames = source.SendGames,
            SendInline = source.SendInline,
            EmbedLinks = source.EmbedLinks,
            SendPolls = source.SendPolls,
            ChangeInfo = source.ChangeInfo,
            InviteUsers = source.InviteUsers,
            PinMessages = source.PinMessages,
            ManageTopics = source.ManageTopics,
            SendPhotos = source.SendPhotos,
            SendVideos = source.SendVideos,
            SendRoundvideos = source.SendRoundVideos,
            SendAudios = source.SendAudios,
            SendVoices = source.SendVoices,
            SendDocs = source.SendDocs,
            SendPlain = source.SendPlain,
            EditRank = source.EditRank,
            UntilDate = source.UntilDate
        };
    }

    public static TRpcError ToRpcError(this RpcError rpcError)
    {
        return new TRpcError
        {
            ErrorCode = rpcError.ErrorCode,
            ErrorMessage = rpcError.Message
        };
    }

    public static TRpcError ToRpcError(this RpcError rpcError, int xToReplace)
    {
        return new TRpcError
        {
            ErrorCode = rpcError.ErrorCode,
            ErrorMessage = string.Format(rpcError.Message, xToReplace)
        };
    }

    /// <summary>
    /// Maps an <see cref="IInputChannel"/> to a peer. <c>inputChannelFromMessage</c> only carries a
    /// message context instead of an access hash, so it resolves to the bare id here — the context
    /// itself must be validated through <c>IFromMessagePeerResolver</c> by the caller.
    /// See https://corefork.telegram.org/api/min
    /// </summary>
    public static Peer ToChannelPeer(this IInputChannel channel)
    {
        switch (channel)
        {
            case TInputChannel inputChannel:
                return new Peer(PeerType.Channel, inputChannel.ChannelId/*, inputChannel.AccessHash*/);
            case TInputChannelFromMessage inputChannelFromMessage:
                return new Peer(PeerType.Channel, inputChannelFromMessage.ChannelId);
        }

        RpcErrors.RpcErrors400.ChannelIdInvalid.ThrowRpcError();
        return null!;
    }

    /// <summary>
    /// Builds the reply header of a message. <paramref name="forumTopic"/> must come from the message
    /// itself: <c>top_msg_id</c> is set for both forum topics and comment threads, so it cannot be used
    /// to tell them apart. See https://corefork.telegram.org/api/threads
    /// </summary>
    public static IMessageReplyHeader? ToMessageReplyHeader(this IInputReplyTo? inputReplyTo, bool forumTopic = false)
    {
        return inputReplyTo switch
        {
            TInputReplyToMessage inputReplyToMessage => new TMessageReplyHeader
            {
                ForumTopic = forumTopic,
                Quote = !string.IsNullOrEmpty(inputReplyToMessage.QuoteText),
                QuoteEntities = inputReplyToMessage.QuoteEntities,
                QuoteText = inputReplyToMessage.QuoteText,
                ReplyToMsgId = inputReplyToMessage.ReplyToMsgId,
                ReplyToTopId = inputReplyToMessage.TopMsgId,
                ReplyToPeerId = inputReplyToMessage.ReplyToPeerId?.ToPeer().ToPeer(),
                // Replies can target a single item of a checklist rather than the whole message —
                // see https://corefork.telegram.org/api/todo
                TodoItemId = inputReplyToMessage.TodoItemId > 0 ? inputReplyToMessage.TodoItemId : null,
            },
            TInputReplyToStory inputReplyToStory => new TMessageReplyStoryHeader
            {
                StoryId = inputReplyToStory.StoryId,
                Peer = inputReplyToStory.Peer.ToPeer().ToPeer()
            },
            _ => null
        };
    }

    /// <summary>
    /// Maps an <see cref="IInputPeer"/> to a peer. The <c>fromMessage</c> variants carry a message
    /// context instead of an access hash, so they resolve to the bare id here — the context itself
    /// must be validated through <c>IFromMessagePeerResolver</c> by the caller. Previously they fell
    /// through to <see cref="NotSupportedException"/>, which turned a legitimate client request into
    /// a 500. See https://corefork.telegram.org/api/min
    /// </summary>
    public static Peer ToPeer(this IInputPeer peer,
        long selfUserId = 0)
    {
        PeerType peerType;
        long peerId;
        //long accessHash = 0;

        switch (peer)
        {
            case TInputPeerChannel inputPeerChannel:
                peerType = PeerType.Channel;
                peerId = inputPeerChannel.ChannelId;
                //accessHash = inputPeerChannel.AccessHash;
                break;
            case TInputPeerChannelFromMessage inputPeerChannelFromMessage:
                peerType = PeerType.Channel;
                peerId = inputPeerChannelFromMessage.ChannelId;
                break;
            case TInputPeerChat inputPeerChat:
                peerType = PeerType.Chat;
                peerId = inputPeerChat.ChatId;
                break;
            case TInputPeerEmpty:
                peerType = PeerType.Empty;
                peerId = selfUserId;
                break;
            case TInputPeerSelf:
                peerType = PeerType.Self;
                peerId = selfUserId;
                break;
            case TInputPeerUser inputPeerUser:
                peerType = PeerType.User;
                peerId = inputPeerUser.UserId;
                //accessHash = inputPeerUser.AccessHash;
                break;
            case TInputPeerUserFromMessage inputPeerUserFromMessage:
                peerType = PeerType.User;
                peerId = inputPeerUserFromMessage.UserId;
                break;
            default:
                throw new NotSupportedException(peer.GetType().Name);
        }

        if (peerType == PeerType.User && peerId == selfUserId)
        {
            peerType = PeerType.Self;
        }

        return new Peer(peerType, peerId/*, accessHash*/);
    }

    public static Peer ToPeer(this IInputUser userPeer,
        long selfUserId)
    {
        var peerId = 0L;
        var peerType = PeerType.User;
        //var accessHash = 0L;
        switch (userPeer)
        {
            case TInputUser inputUser:
                peerId = inputUser.UserId;
                //accessHash = inputUser.AccessHash;
                break;
            case TInputUserEmpty:
                break;
            case TInputUserFromMessage inputUserFromMessage:
                peerId = inputUserFromMessage.UserId;

                break;
            case TInputUserSelf:
                peerType = PeerType.Self;
                peerId = selfUserId;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(userPeer));
        }

        return new Peer(peerType, peerId/*, accessHash*/);
    }

    [return: NotNullIfNotNull("peer")]
    public static IPeer? ToPeer(this Peer? peer)
    {
        if (peer == null)
        {
            return null;
        }

        switch (peer.PeerType)
        {
            case PeerType.User:
            case PeerType.Self:
            case PeerType.Empty:
                return new TPeerUser { UserId = peer.PeerId };

            case PeerType.Chat:
                return new TPeerChat { ChatId = peer.PeerId };

            case PeerType.Channel:
                return new TPeerChannel { ChannelId = peer.PeerId };

            default:
                throw new ArgumentOutOfRangeException($"Peer type is invalid:peerType={peer.PeerType} peerId={peer.PeerId}");
        }
    }

    public static IPeerColor? ToPeerColor(this PeerColor? peerColor)
    {
        if (peerColor == null)
        {
            return default;
        }

        // Collectible peer color, taken from a unique star gift.
        // See https://core.telegram.org/api/colors
        if (peerColor.CollectibleId.HasValue)
        {
            return new TPeerColorCollectible
            {
                CollectibleId = peerColor.CollectibleId.Value,
                GiftEmojiId = peerColor.GiftEmojiId ?? 0,
                BackgroundEmojiId = peerColor.BackgroundEmojiId ?? 0,
                AccentColor = peerColor.AccentColor ?? 0,
                Colors = peerColor.Colors == null
                    ? new TVector<int>()
                    : new TVector<int>(peerColor.Colors),
                DarkAccentColor = peerColor.DarkAccentColor,
                DarkColors = peerColor.DarkColors == null
                    ? null
                    : new TVector<int>(peerColor.DarkColors)
            };
        }

        if (!peerColor.Color.HasValue && !peerColor.BackgroundEmojiId.HasValue)
        {
            return default;
        }

        return new TPeerColor
        {
            Color = peerColor.Color,
            BackgroundEmojiId = peerColor.BackgroundEmojiId
        };
    }
    public static RequestInfo ToRequestInfo(this IRequestInput requestInput)
    {
        return new RequestInfo(
            requestInput.ConnectionId,
            requestInput.SessionId,
            requestInput.ReqMsgId,
            requestInput.UserId,
            requestInput.AccessHashKeyId,
            requestInput.AuthKeyId,
            requestInput.PermAuthKeyId,
            requestInput.RequestId,
            requestInput.Layer,
            requestInput.Date,
            requestInput.DeviceType,
            BusinessBotId: BusinessConnectionScope.CurrentBotId);
    }

    public static TRpcError ToRpcError(this string rpcErrorMessage, int rpcCode = 400)
    {
        return new TRpcError
        {
            ErrorCode = rpcCode,
            ErrorMessage = rpcErrorMessage
        };
    }
}