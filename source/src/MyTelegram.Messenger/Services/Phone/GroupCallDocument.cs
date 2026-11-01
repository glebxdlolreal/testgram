namespace MyTelegram.Messenger.Services.Phone;

public class GroupCallDocument
{
    public long Id { get; set; }
    public long CallId { get; set; }
    public long AccessHash { get; set; }
    public int RandomId { get; set; }
    public long PeerId { get; set; }
    public int PeerType { get; set; }
    public long CreatorId { get; set; }
    public bool Active { get; set; } = true;
    public bool JoinMuted { get; set; }
    /// <summary>The call belongs to a broadcast channel: everybody joins muted and join_muted cannot be changed.</summary>
    public bool Broadcast { get; set; }
    public bool RtmpStream { get; set; }
    public string? RtmpUrl { get; set; }
    public string? RtmpStreamKey { get; set; }
    public bool LiveStory { get; set; }
    public int? StoryId { get; set; }
    public bool Conference { get; set; }
    public bool MessagesEnabled { get; set; } = true;
    public bool RecordVideoActive { get; set; }
    public string? RecordTitle { get; set; }
    public string? Title { get; set; }
    public int? ScheduleDate { get; set; }
    public int? RecordStartDate { get; set; }
    public long? SendPaidMessagesStars { get; set; }
    public string? InviteHash { get; set; }
    public string? InviteLink { get; set; }
    public long? DefaultSendAsPeerId { get; set; }
    public int? DefaultSendAsPeerType { get; set; }
    public int Version { get; set; } = 1;
    /// <summary>Optimistic-concurrency counter for read-modify-write updates; unlike <see cref="Version"/>
    /// (the participant-list version clients see) it changes on every write.</summary>
    public long Revision { get; set; }
    public int Date { get; set; }
    public List<GroupCallParticipantDoc> Participants { get; set; } = new();
    public List<long> InvitedUserIds { get; set; } = new();
    public List<GroupCallInviteMessageDoc> InviteMessages { get; set; } = new();
    public List<long> ScheduleStartSubscriberIds { get; set; } = new();
    public List<GroupCallChainBlockDoc> ChainBlocks { get; set; } = new();
    public List<GroupCallMessageDoc> Messages { get; set; } = new();
}

public class GroupCallParticipantDoc
{
    public long UserId { get; set; }
    public long PeerId { get; set; }
    public int PeerType { get; set; }
    public int Source { get; set; }
    public bool Muted { get; set; }
    /// <summary>Muted by an admin, or joined a join_muted call without the right to manage it: the
    /// participant cannot unmute themselves until an admin lets them speak.</summary>
    public bool MutedByAdmin { get; set; }
    public bool VideoStopped { get; set; }
    public bool VideoPaused { get; set; }
    public bool PresentationPaused { get; set; }
    public bool RaiseHand { get; set; }
    public int? Volume { get; set; }
    /// <summary>The volume was set by the participant themselves rather than by an admin.</summary>
    public bool VolumeBySelf { get; set; }
    public int Date { get; set; }
    public bool Left { get; set; }
    public string? ParamsJson { get; set; }
    public string? PresentationParamsJson { get; set; }
    /// <summary>The SFU endpoint other clients key this participant's camera video on.</summary>
    public string? VideoEndpoint { get; set; }
    /// <summary>The SFU endpoint of the participant's screen share.</summary>
    public string? PresentationEndpoint { get; set; }
    public byte[]? PublicKey { get; set; }
}

public class GroupCallChainBlockDoc
{
    public int SubChainId { get; set; }
    public byte[] Block { get; set; } = [];
}

public class GroupCallInviteMessageDoc
{
    public int MessageId { get; set; }
    public long UserId { get; set; }
    public long FromUserId { get; set; }
    public bool Video { get; set; }
    public int Date { get; set; }
    public bool Declined { get; set; }
}

public class GroupCallMessageDoc
{
    public int Id { get; set; }
    public long RandomId { get; set; }
    public long FromPeerId { get; set; }
    public int FromPeerType { get; set; }
    public int Date { get; set; }
    public string Text { get; set; } = string.Empty;
    public byte[] MessageBytes { get; set; } = [];
    public long? PaidMessageStars { get; set; }
    public bool FromAdmin { get; set; }
    public bool Deleted { get; set; }
}

public class DefaultGroupCallJoinAsDocument
{
    public string Id { get; set; } = string.Empty;
    public long UserId { get; set; }
    public long PeerId { get; set; }
    public int PeerType { get; set; }
    public long JoinAsPeerId { get; set; }
    public int JoinAsPeerType { get; set; }
    public int Date { get; set; }
}

public class GroupCallRtmpStreamDocument
{
    public string Id { get; set; } = string.Empty;
    public long PeerId { get; set; }
    public int PeerType { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public int Date { get; set; }
}
