#!/bin/bash

# Fragment NFT Usernames Initialization Script
# Creates sample Fragment collectibles (usernames and phone numbers)

echo "🔷 Initializing Fragment NFT data..."

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/compose-helper.sh"

# Connect to MongoDB
compose exec -T mongodb mongosh tg --quiet --eval '

// Create fragment_collectibles collection with sample data
// Upsert the catalogue fields only: owner_peer_* and assign_state record who bought
// a collectible (assign-nft-username.sh) and must survive a re-run.
print("Updating fragment_collectibles catalogue...");
[
  // Premium usernames
  {
    _id: "fragment-username-crypto",
    type: "username",
    username: "crypto",
    purchase_date: 1712160000,
    currency: "USD",
    amount: 50000,
    crypto_currency: "TON",
    crypto_amount: NumberLong("150000000000"),
    url: "https://fragment.com/username/crypto"
  },
  {
    _id: "fragment-username-blockchain",
    type: "username",
    username: "blockchain",
    purchase_date: 1712160000,
    currency: "USD",
    amount: 14500,
    crypto_currency: "TON",
    crypto_amount: NumberLong("50000000000"),
    url: "https://fragment.com/username/blockchain"
  },
  {
    _id: "fragment-username-defi",
    type: "username",
    username: "defi",
    purchase_date: 1712160000,
    currency: "USD",
    amount: 25000,
    crypto_currency: "TON",
    crypto_amount: NumberLong("80000000000"),
    url: "https://fragment.com/username/defi"
  },
  {
    _id: "fragment-username-nft",
    type: "username",
    username: "nft",
    purchase_date: 1712160000,
    currency: "USD",
    amount: 30000,
    crypto_currency: "TON",
    crypto_amount: NumberLong("100000000000"),
    url: "https://fragment.com/username/nft"
  },
  {
    _id: "fragment-username-web3",
    type: "username",
    username: "web3",
    purchase_date: 1712160000,
    currency: "USD",
    amount: 20000,
    crypto_currency: "TON",
    crypto_amount: NumberLong("70000000000"),
    url: "https://fragment.com/username/web3"
  },
  // Premium phone numbers (888 prefix)
  {
    _id: "fragment-phone-888123456",
    type: "phone",
    phone: "888123456",
    purchase_date: 1712160000,
    currency: "USD",
    amount: 29900,
    crypto_currency: "TON",
    crypto_amount: NumberLong("100000000000"),
    url: "https://fragment.com/number/888123456"
  },
  {
    _id: "fragment-phone-888777888",
    type: "phone",
    phone: "888777888",
    purchase_date: 1712160000,
    currency: "USD",
    amount: 45000,
    crypto_currency: "TON",
    crypto_amount: NumberLong("150000000000"),
    url: "https://fragment.com/number/888777888"
  },
  {
    _id: "fragment-phone-888999999",
    type: "phone",
    phone: "888999999",
    purchase_date: 1712160000,
    currency: "USD",
    amount: 60000,
    crypto_currency: "TON",
    crypto_amount: NumberLong("200000000000"),
    url: "https://fragment.com/number/888999999"
  }
].forEach(function (doc) {
  var id = doc._id;
  delete doc._id;
  db.fragment_collectibles.updateOne({ _id: id }, { $set: doc }, { upsert: true });
});

// Create indexes
print("Creating indexes...");
db.fragment_collectibles.createIndex({ "username": 1 });
db.fragment_collectibles.createIndex({ "phone": 1 });
db.fragment_collectibles.createIndex({ "type": 1 });

print("✅ Fragment NFT initialization complete!");
print("Catalogue holds " + db.fragment_collectibles.countDocuments({}) + " collectibles");
print("  - " + db.fragment_collectibles.countDocuments({type: "username"}) + " usernames");
print("  - " + db.fragment_collectibles.countDocuments({type: "phone"}) + " phone numbers");

'

echo "✅ Fragment NFT data initialized successfully!"
echo ""
echo "Available NFT usernames:"
echo "  - crypto (50,000 USD / 150 TON)"
echo "  - blockchain (14,500 USD / 50 TON)"
echo "  - defi (25,000 USD / 80 TON)"
echo "  - nft (30,000 USD / 100 TON)"
echo "  - web3 (20,000 USD / 70 TON)"
echo ""
echo "Available NFT phone numbers:"
echo "  - 888123456 (29,900 USD / 100 TON)"
echo "  - 888777888 (45,000 USD / 150 TON)"
echo "  - 888999999 (60,000 USD / 200 TON)"
