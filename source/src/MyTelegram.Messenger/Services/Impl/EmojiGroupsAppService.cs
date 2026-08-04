using MongoDB.Bson;
using MongoDB.Driver;
using System.Globalization;

namespace MyTelegram.Messenger.Services.Impl;

public class EmojiGroupsAppService(IMongoDatabase mongoDatabase, ILogger<EmojiGroupsAppService> logger)
    : IEmojiGroupsAppService, ITransientDependency
{
    public async Task<MyTelegram.Schema.Messages.IEmojiGroups> GetEmojiGroupsAsync(EmojiGroupType groupType, int hash)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>("emoji_groups");
        var docs = await collection.Find(BuildFilter(groupType))
            .Sort(Builders<BsonDocument>.Sort.Ascending("Order").Ascending("Title"))
            .ToListAsync();

        var groups = docs.Select(BuildGroup).ToList();
        var currentHash = ComputeHash(groups);

        // A zero hash means "no cached copy" on the client side, so it can never match.
        if (currentHash != 0 && hash == currentHash)
        {
            return new TEmojiGroupsNotModified();
        }

        // TDLib drops any category whose icon can't be resolved (EmojiGroupList::
        // get_emoji_categories_object), so a zero icon_emoji_id makes the whole category
        // vanish on iOS/Desktop/tdweb. Surface that as a warning instead of silently
        // serving categories those clients will discard.
        var missingIcons = groups.Count(x => GetIconEmojiId(x) == 0);
        if (missingIcons > 0)
        {
            logger.LogWarning(
                "{Count} of {Total} emoji categories for {GroupType} have no IconEmojiId; TDLib-based clients will discard them",
                missingIcons, groups.Count, groupType);
        }

        return new TEmojiGroups
        {
            Hash = currentHash,
            Groups = new TVector<IEmojiGroup>(groups)
        };
    }

    private static FilterDefinition<BsonDocument> BuildFilter(EmojiGroupType groupType)
    {
        var builder = Builders<BsonDocument>.Filter;
        return groupType switch
        {
            // Documents predating the "For" field belong to the default set.
            EmojiGroupType.Default => builder.Or(builder.Exists("For", false), builder.Eq("For", "default")),
            EmojiGroupType.Stickers => builder.Eq("For", "stickers"),
            EmojiGroupType.Status => builder.Eq("For", "status"),
            EmojiGroupType.ProfilePhoto => builder.Eq("For", "profile_photo"),
            _ => throw new ArgumentOutOfRangeException(nameof(groupType), groupType, null)
        };
    }

    private static IEmojiGroup BuildGroup(BsonDocument doc)
    {
        var kind = doc.Contains("Kind") && doc["Kind"].IsString ? doc["Kind"].AsString : "default";
        var title = doc.Contains("Title") && doc["Title"].IsString ? doc["Title"].AsString : string.Empty;
        var iconEmojiId = doc.Contains("IconEmojiId") && doc["IconEmojiId"].IsNumeric ? doc["IconEmojiId"].ToInt64() : 0;
        var emoticons = new TVector<string>(doc.Contains("Emoticons") && doc["Emoticons"].IsBsonArray
            ? doc["Emoticons"].AsBsonArray.Where(x => x.IsString).Select(x => x.AsString).ToList()
            : []);

        return kind switch
        {
            "premium" => new TEmojiGroupPremium { Title = title, IconEmojiId = iconEmojiId },
            "greeting" => new TEmojiGroupGreeting { Title = title, IconEmojiId = iconEmojiId, Emoticons = emoticons },
            _ => new TEmojiGroup { Title = title, IconEmojiId = iconEmojiId, Emoticons = emoticons }
        };
    }

    /// <summary>
    /// Hashes the actual category contents rather than a stored version counter: a version
    /// maximum never decreases, so deleting a category would leave clients pinned to a stale
    /// notModified forever.
    /// </summary>
    internal static int ComputeHash(IReadOnlyList<IEmojiGroup> groups)
    {
        if (groups.Count == 0)
        {
            return 0;
        }

        unchecked
        {
            // FNV-1a over the fields the client actually renders.
            var hash = 2166136261u;

            void Mix(string value)
            {
                foreach (var c in value)
                {
                    hash = (hash ^ c) * 16777619u;
                }

                hash = (hash ^ 0x1Fu) * 16777619u; // field separator
            }

            foreach (var group in groups)
            {
                Mix(group switch
                {
                    TEmojiGroupPremium => "premium",
                    TEmojiGroupGreeting => "greeting",
                    _ => "default"
                });
                Mix(GetTitle(group));
                Mix(GetIconEmojiId(group).ToString(CultureInfo.InvariantCulture));
                foreach (var emoticon in GetEmoticons(group))
                {
                    Mix(emoticon);
                }
            }

            // messages.emojiGroups.hash is an int; fold to a positive value and keep it
            // non-zero so it can never collide with the client's "no cache" sentinel.
            var result = (int)(hash & 0x7FFFFFFF);
            return result == 0 ? 1 : result;
        }
    }

    private static string GetTitle(IEmojiGroup group) => group switch
    {
        TEmojiGroup x => x.Title,
        TEmojiGroupGreeting x => x.Title,
        TEmojiGroupPremium x => x.Title,
        _ => string.Empty
    };

    private static long GetIconEmojiId(IEmojiGroup group) => group switch
    {
        TEmojiGroup x => x.IconEmojiId,
        TEmojiGroupGreeting x => x.IconEmojiId,
        TEmojiGroupPremium x => x.IconEmojiId,
        _ => 0
    };

    private static IEnumerable<string> GetEmoticons(IEmojiGroup group) => group switch
    {
        TEmojiGroup x => x.Emoticons,
        TEmojiGroupGreeting x => x.Emoticons,
        // emojiGroupPremium carries no emoticons; clients select Premium content instead.
        _ => []
    };
}
