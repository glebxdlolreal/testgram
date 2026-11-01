namespace MyTelegram.Messenger.Services.Phone;

public class CallSessionDocument
{
    public long Id { get; set; }
    public long CallId { get; set; }
    public long AccessHash { get; set; }
    public long CallerAccessHash { get; set; }
    public long CalleeAccessHash { get; set; }
    public long CallerId { get; set; }
    public long CalleeId { get; set; }
    public int RandomId { get; set; }
    public byte[]? GAHash { get; set; }
    public byte[]? GA { get; set; }
    public byte[]? GB { get; set; }
    /// <summary>The call's reflector peer_tag; both participants get the same one.</summary>
    public byte[]? ReflectorPeerTag { get; set; }
    public long KeyFingerprint { get; set; }
    public string State { get; set; } = CallSessionStates.Requested;
    public bool Video { get; set; }
    public int Date { get; set; }

    /// <summary>
    /// Unix time of the most recent <see cref="State"/> transition. Expiry deadlines are measured from
    /// here rather than from <see cref="Date"/>, because e.g. the connect deadline for <c>accepted</c>
    /// starts when the call was answered, not when it was placed. Null on documents written before this
    /// field existed, in which case <see cref="Date"/> is used.
    /// </summary>
    public int? StateChangedDate { get; set; }

    public int? ReceivedDate { get; set; }
    /// <summary>Seconds the call was connected; null for a call that never connected.</summary>
    public int? Duration { get; set; }
    /// <summary>The participant whose phone.discardCall ended the call; 0 when the server expired it.</summary>
    public long DiscardedByUserId { get; set; }
    public string? DiscardReason { get; set; }
    public string? DiscardReasonSlug { get; set; }
    public bool NeedRating { get; set; }
    public bool NeedDebug { get; set; }
    public List<string> CallerLibraryVersions { get; set; } = [];
    public List<string> CalleeLibraryVersions { get; set; } = [];
    public bool CallerConferenceSupported { get; set; }
    public bool CalleeConferenceSupported { get; set; }
    public string? ProtocolJson { get; set; }
    public string? DebugJson { get; set; }
    public int? Rating { get; set; }
    public string? RatingComment { get; set; }
    public bool RatingUserInitiative { get; set; }
    public long? LogFileId { get; set; }
    public int? LogFileParts { get; set; }
    public string? LogFileName { get; set; }
    public string? LogFileMd5Checksum { get; set; }

    /// <summary>Unix time the session entered its current <see cref="State"/>.</summary>
    public int StateSince => StateChangedDate ?? Date;

    public bool IsParticipant(long userId)
    {
        return CallerId == userId || CalleeId == userId;
    }

    public long GetAccessHashForUser(long userId)
    {
        if (CallerId == userId)
        {
            return CallerAccessHash != 0 ? CallerAccessHash : AccessHash;
        }

        if (CalleeId == userId)
        {
            return CalleeAccessHash != 0 ? CalleeAccessHash : AccessHash;
        }

        return 0;
    }

    public bool HasAccessHashForUser(long userId, long accessHash)
    {
        if (!IsParticipant(userId))
        {
            return false;
        }

        // R29.1/R29.3: authorize the requesting user strictly against the per-user
        // access hash issued to them (GetAccessHashForUser falls back to the legacy
        // shared AccessHash only when that user has no per-user hash). We must NOT
        // accept the shared/other-party AccessHash here, otherwise the callee could
        // reference the call using the caller's access hash (and vice versa),
        // breaking independent per-user authorization.
        return accessHash != 0 && GetAccessHashForUser(userId) == accessHash;
    }
}
