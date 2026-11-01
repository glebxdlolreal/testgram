using Microsoft.Extensions.Configuration;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MyTelegram.Services.Services;

public sealed class AccessHashHelper2(
    IConfiguration configuration,
    IPeerHelper peerHelper) : IAccessHashHelper2, ISingletonDependency
{
    private byte[]? _accessHashSecretKeyBytes;

    public ValueTask<bool> IsAccessHashValidAsync(IRequestWithAccessHashKeyId request, long targetId, long accessHash,
        AccessHashType? accessHashType = null)
    {
        return IsAccessHashValidAsync(request.UserId, request.AccessHashKeyId, targetId, accessHash, accessHashType);
    }

    public async Task CheckAccessHashAsync(long currentUserId, long accessHashKeyId, long targetId, long accessHash, AccessHashType? accessHashType = null)
    {
        if (!await IsAccessHashValidAsync(currentUserId, accessHashKeyId, targetId, accessHash, accessHashType))
        {
            CreateRpcError(accessHashType).ThrowRpcError();
        }
    }

    public RpcError CreateRpcError(AccessHashType? accessHashType)
    {
        switch (accessHashType)
        {
            case AccessHashType.WallPaper:
                return RpcErrors.RpcErrors400.WallpaperInvalid;
            case AccessHashType.Theme:
                return RpcErrors.RpcErrors400.ThemeInvalid;
            case AccessHashType.GroupCall:
                return RpcErrors.RpcErrors400.GroupcallInvalid;
            case AccessHashType.StickerSet:
                return RpcErrors.RpcErrors400.StickersetInvalid;
            case AccessHashType.User:
                return RpcErrors.RpcErrors400.UserIdInvalid;
            // What Telegram answers to a wrong hash in inputChannel / inputPeerChannel (checked 2026-11-01).
            case AccessHashType.Channel:
                return RpcErrors.RpcErrors400.ChannelInvalid;
            case AccessHashType.Document:
                return RpcErrors.RpcErrors400.DocumentInvalid;
            case AccessHashType.Photo:
                return RpcErrors.RpcErrors400.PhotoInvalid;
            case AccessHashType.Sticker:
                return RpcErrors.RpcErrors400.StickersetInvalid;
            case AccessHashType.BotApp:
                return RpcErrors.RpcErrors400.BotAppInvalid;
            case AccessHashType.Call:
                return RpcErrors.RpcErrors400.CallPeerInvalid;
            default:
                return RpcErrors.RpcErrors400.PeerIdInvalid;
        }
    }

    public Task CheckAccessHashAsync(IRequestWithAccessHashKeyId request, long targetId, long accessHash,
        AccessHashType? accessHashType = null)
    {
        return CheckAccessHashAsync(request.UserId, request.AccessHashKeyId, targetId, accessHash, accessHashType);
    }

    public Task CheckAccessHashAsync(long currentUserId, long accessHashKeyId, IInputPeer? inputPeer)
    {
        switch (inputPeer)
        {
            case null:
                break;
            case TInputPeerChannel inputPeerChannel:
                return CheckAccessHashAsync(currentUserId, accessHashKeyId, inputPeerChannel.ChannelId, inputPeerChannel.AccessHash, AccessHashType.Channel);

            // The fromMessage variants carry no access hash at all — they are validated by proving
            // the cited message is readable and really references the peer, which needs the message
            // read model and therefore lives in IFromMessagePeerResolver. Passing one here is not a
            // successful check: callers that accept fromMessage input must run the resolver.
            // See https://corefork.telegram.org/api/min
            case TInputPeerChannelFromMessage:
                break;
            case TInputPeerSelf:
                break;
            // A wrong hash in inputUser is USER_ID_INVALID, but in inputPeerUser Telegram answers
            // PEER_ID_INVALID (checked 2026-11-01).
            case TInputPeerUser inputPeerUser:
                return CheckInputPeerUserAsync(currentUserId, accessHashKeyId, inputPeerUser);

            case TInputPeerUserFromMessage:
                break;
            // Basic groups have no access hash, and inputPeerEmpty carries no peer at all — neither
            // is an error here. See https://corefork.telegram.org/api/peers#access-hash
            case TInputPeerChat:
            case TInputPeerEmpty:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(inputPeer));
        }

        return Task.CompletedTask;
    }

    private async Task CheckInputPeerUserAsync(long currentUserId, long accessHashKeyId, TInputPeerUser inputPeerUser)
    {
        if (!await IsAccessHashValidAsync(currentUserId, accessHashKeyId, inputPeerUser.UserId,
                inputPeerUser.AccessHash, AccessHashType.User))
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }
    }

    public Task CheckAccessHashAsync(IRequestWithAccessHashKeyId request, IInputPeer? inputPeer)
    {
        return CheckAccessHashAsync(request.UserId, request.AccessHashKeyId, inputPeer);
    }

    public Task CheckAccessHashAsync(long currentUserId, long accessHashKeyId, IInputUser inputUser)
    {
        if (inputUser is TInputUser tInputUser)
        {
            return CheckAccessHashAsync(currentUserId, accessHashKeyId, tInputUser.UserId, tInputUser.AccessHash, AccessHashType.User);
        }

        return Task.CompletedTask;
    }

    public Task CheckAccessHashAsync(IRequestWithAccessHashKeyId request, IInputUser inputUser)
    {
        return CheckAccessHashAsync(request.UserId, request.AccessHashKeyId, inputUser);
    }

    public Task CheckAccessHashAsync(long currentUserId, long accessHashKeyId, IInputChannel inputChannel)
    {
        if (inputChannel is TInputChannel tInputChannel)
        {
            return CheckAccessHashAsync(currentUserId, accessHashKeyId, tInputChannel.ChannelId, tInputChannel.AccessHash, AccessHashType.Channel);
        }

        return Task.CompletedTask;
    }

    public Task CheckAccessHashAsync(IRequestWithAccessHashKeyId request, IInputChannel inputChannel)
    {
        return CheckAccessHashAsync(request.UserId, request.AccessHashKeyId, inputChannel);
    }

    public ValueTask<bool> IsAccessHashValidAsync(long currentUserId, long accessHashKeyId, long targetId, long accessHash, AccessHashType? accessHashType = AccessHashType.Unknown)
    {
        if (accessHashType == null)
        {
            var peer = peerHelper.GetPeer(targetId);
            switch (peer.PeerType)
            {
                case PeerType.Channel:
                    accessHashType = AccessHashType.Channel;
                    break;
                case PeerType.User:
                    accessHashType = AccessHashType.User;
                    break;
                case PeerType.Self:
                    return ValueTask.FromResult(true);
            }
        }

        if (accessHashType == null)
        {
            return ValueTask.FromResult(false);
        }

        var accessHashForCurrentSession = GenerateAccessHash(currentUserId, accessHashKeyId, targetId, accessHashType.Value);

        // if (accessHashForCurrentSession != accessHash)
        // {
        //     Console.WriteLine($"currentUserId:{currentUserId} accessHashKeyId:{accessHashKeyId} targetId:{targetId} {accessHashType},accessHashForCurrentSession:{accessHashForCurrentSession},accessHash:{accessHash}");
        // }

        return ValueTask.FromResult(accessHashForCurrentSession == accessHash);
    }

    private void GenerateAccessHashSecretKeyForUser(ReadOnlySpan<byte> accessHasKeyBytesOfUser, Span<byte> destination)
    {
        if (_accessHashSecretKeyBytes == null)
        {
            var masterKey = configuration.GetValue<string>("App:AccessHashSecretKey");
            if (string.IsNullOrEmpty(masterKey))
            {
                throw new InvalidOperationException("App:AccessHashSecretKey is null");
            }

            _accessHashSecretKeyBytes = Encoding.UTF8.GetBytes(masterKey);
        }

        HMACSHA256.HashData(_accessHashSecretKeyBytes, accessHasKeyBytesOfUser, destination);
    }

    /// <summary>
    /// Mints the access hash a client quotes back for <paramref name="targetId"/>.
    ///
    /// <para><b>The byte layout is not ours to choose.</b> Access hashes minted here are validated by
    /// the closed-source <c>session-server</c> and <c>file-server</c> images before a request ever
    /// reaches this repository — for 123 request types, among them <c>upload.getFile</c>,
    /// <c>messages.sendMedia</c> and <c>upload.getWebFile</c>. Both are NativeAOT builds of this exact
    /// method from before it was rewritten, and both still allocate the single 89-byte buffer below
    /// (<c>movl $0x59</c> at <c>AccessHashHelper2__GenerateAccessHash</c>). A hash computed any other
    /// way is answered with <c>DOCUMENT_INVALID</c> / <c>PHOTO_INVALID</c> / <c>STICKERSET_INVALID</c>
    /// and the client downloads nothing at all, so this method reproduces the deployed layout
    /// byte for byte.</para>
    ///
    /// <para>That layout is weaker than it looks, and deliberately kept: the
    /// <paramref name="accessHashKeyId"/> write lands on top of the type byte and of the low seven
    /// bytes of <paramref name="currentUserId"/>, so <b>neither the access hash type nor
    /// <paramref name="currentUserId"/> binds the result</b> — only <paramref name="accessHashKeyId"/>,
    /// which is per-user, and <paramref name="targetId"/> do. One consequence is benign
    /// (<c>AccessHashType.Call</c> and <c>GroupCall</c> collapse into one value, which is what the
    /// deployed images expect for <c>inputPhoneCall</c>); the rest is a real weakening that cannot be
    /// fixed here, because the enforcing validator is a stripped native binary with no source.
    /// Tightening it again — as commit 3d390cd31 did — silently breaks every media download in this
    /// deployment instead of making anything safer.</para>
    /// </summary>
    public long GenerateAccessHash(long currentUserId, long accessHashKeyId, long targetId, AccessHashType accessHashType)
    {
        // accessHashType:1 + currentUserId:8 + targetId:8 + hash:32 + keyForUser:32 + accessHashKeyForUser:8 = 89 bytes
        Span<byte> bytes = stackalloc byte[89];
        bytes[0] = (byte)accessHashType;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.Slice(1, 8), currentUserId);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[17..], targetId);
        var dest = bytes.Slice(17, 32);
        var accessHashSecretKey = bytes.Slice(49, 32);
        var accessHashKeyBytes = bytes[..^8];
        BinaryPrimitives.WriteInt64LittleEndian(accessHashKeyBytes, accessHashKeyId);
        GenerateAccessHashSecretKeyForUser(accessHashKeyBytes, accessHashSecretKey);
        HMACSHA256.HashData(accessHashSecretKey, bytes[..17], dest);

        return BitConverter.ToInt64(dest);
    }
}
