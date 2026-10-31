#!/bin/bash

# Assign (or take back) a Fragment collectible username.
#
# Usage: ./assign-nft-username.sh [--channel] [--revoke] <peer_id> <nft_username>
#
#   --channel   the peer is a channel/supergroup (default: a user or bot)
#   --revoke    detach the username from its owner instead of attaching it
#
# The collectible must already be listed in the fragment_collectibles catalogue
# (see init-fragment-nft.sh). This script only records the sale in that catalogue
# (owner + assign_state "pending"); messenger-command-server picks it up within a
# few seconds and attaches the username through the peer's aggregate, so it is
# reserved, resolvable and survives a read-model rebuild. A new collectible starts
# disabled: the owner turns it on in Settings -> Username.

PEER_TYPE=user
REVOKE=0
while [[ "$1" == --* ]]; do
    case "$1" in
        --channel) PEER_TYPE=channel ;;
        --revoke) REVOKE=1 ;;
        *) echo "❌ Unknown option: $1" >&2; exit 1 ;;
    esac
    shift
done

if [ "$#" -ne 2 ]; then
    echo "Usage: $0 [--channel] [--revoke] <peer_id> <nft_username>"
    echo "Example: $0 2010001 blockchain"
    exit 1
fi

PEER_ID=$1
NFT_USERNAME=$(echo "$2" | tr '[:upper:]' '[:lower:]')

if [[ ! "$PEER_ID" =~ ^[0-9]+$ ]]; then
    echo "❌ Error: peer_id must be a positive integer" >&2
    exit 1
fi

# Telegram usernames: letters, digits and underscores. Checking this also keeps
# the value safe to embed in the mongosh script below.
if [[ ! "$NFT_USERNAME" =~ ^[a-z0-9_]{1,32}$ ]]; then
    echo "❌ Error: '$2' is not a valid username" >&2
    exit 1
fi

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/compose-helper.sh"

if [ "$REVOKE" = "1" ]; then
    STATE=revoke_pending
    echo "🔷 Detaching NFT username '$NFT_USERNAME' from $PEER_TYPE $PEER_ID..."
else
    STATE=pending
    echo "🔷 Assigning NFT username '$NFT_USERNAME' to $PEER_TYPE $PEER_ID..."
fi

RESULT=$(compose exec -T mongodb mongosh tg --quiet --eval "
const peerType = '$PEER_TYPE';
const peerId = NumberLong('$PEER_ID');
const username = '$NFT_USERNAME';
const revoke = $REVOKE === 1;

const collectible = db.fragment_collectibles.findOne({ type: 'username', username: username });
if (!collectible) {
    print('ERROR:COLLECTIBLE_NOT_FOUND');
    quit(0);
}

const peerExists = peerType === 'channel'
    ? db['eventflow-channelreadmodel'].countDocuments({ ChannelId: peerId }) > 0
    : db['eventflow-userreadmodel'].countDocuments({ UserId: peerId, IsDeleted: { \$ne: true } }) > 0;
if (!peerExists) {
    print('ERROR:PEER_NOT_FOUND');
    quit(0);
}

if (revoke) {
    if (collectible.owner_peer_type !== peerType || Number(collectible.owner_peer_id) !== Number(peerId)) {
        print('ERROR:NOT_THE_OWNER');
        quit(0);
    }
} else if (collectible.owner_peer_id !== undefined && collectible.assign_state !== 'revoked' &&
           collectible.assign_state !== 'failed' &&
           (collectible.owner_peer_type !== peerType || Number(collectible.owner_peer_id) !== Number(peerId))) {
    print('ERROR:OWNED_BY:' + collectible.owner_peer_type + ' ' + collectible.owner_peer_id);
    quit(0);
}

db.fragment_collectibles.updateOne(
    { _id: collectible._id },
    {
        \$set: { owner_peer_type: peerType, owner_peer_id: peerId, assign_state: '$STATE' },
        \$unset: { assign_error: '' }
    });
print('OK');
" | tr -d '\r')

case "$RESULT" in
    OK) ;;
    ERROR:COLLECTIBLE_NOT_FOUND)
        echo "❌ Error: Fragment collectible '$NFT_USERNAME' not found! Listed usernames:" >&2
        compose exec -T mongodb mongosh tg --quiet --eval "
        db.fragment_collectibles.find({type: 'username'}).forEach(doc =>
            print('  - ' + doc.username + (doc.owner_peer_id !== undefined && doc.assign_state !== 'revoked'
                ? ' (' + doc.owner_peer_type + ' ' + doc.owner_peer_id + ', ' + doc.assign_state + ')' : '')))
        " >&2
        exit 1 ;;
    ERROR:PEER_NOT_FOUND)
        echo "❌ Error: $PEER_TYPE $PEER_ID not found!" >&2
        exit 1 ;;
    ERROR:NOT_THE_OWNER)
        echo "❌ Error: '$NFT_USERNAME' does not belong to $PEER_TYPE $PEER_ID" >&2
        exit 1 ;;
    ERROR:OWNED_BY:*)
        echo "❌ Error: '$NFT_USERNAME' already belongs to ${RESULT#ERROR:OWNED_BY:} (revoke it first)" >&2
        exit 1 ;;
    *)
        echo "❌ Error: mongosh failed: $RESULT" >&2
        exit 1 ;;
esac

echo "Waiting for messenger-command-server to apply it..."
for _ in $(seq 1 30); do
    sleep 1
    STATE_NOW=$(compose exec -T mongodb mongosh tg --quiet --eval "
    const doc = db.fragment_collectibles.findOne({ type: 'username', username: '$NFT_USERNAME' });
    print(doc.assign_state + (doc.assign_error ? ':' + doc.assign_error : ''));
    " | tr -d '\r')
    case "$STATE_NOW" in
        applied)
            echo "✅ NFT username '$NFT_USERNAME' attached to $PEER_TYPE $PEER_ID (disabled until the owner turns it on)."
            exit 0 ;;
        revoked)
            echo "✅ NFT username '$NFT_USERNAME' detached from $PEER_TYPE $PEER_ID."
            exit 0 ;;
        failed:*)
            echo "❌ Error: ${STATE_NOW#failed:}" >&2
            exit 1 ;;
    esac
done

echo "⚠️  Still '$STATE_NOW' after 30 s: is messenger-command-server running? It will be applied when it starts." >&2
exit 1
