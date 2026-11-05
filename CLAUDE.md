# Testgram Development Guide

Self-hosted C# Telegram server (MyTelegram fork): MTProto 2.0, API Layer 222.
Stack: .NET 10, CQRS/Event Sourcing (EventFlow), MongoDB, RabbitMQ, Redis, MinIO, Coturn.
Solution: `source/MyTelegram.slnx`. Tests: `source/test/*`.

## Required reading, on demand

The full protocol notes, collection catalogue, client observations, debugging table and probing recipes are preserved in [docs/development-reference.md](docs/development-reference.md). **Before changing a surface, read its relevant section there.** It is a reference, not an automatic context import. Do not copy its long histories back into this file; keep this guide below 40,000 characters.

Reference sections (search the exact heading):
- Core: `Handler Pattern`, `Common Patterns`, `TL Schema`, `MongoDB`.
- Media: `Files and server-side video processing`, `File references`, `GIFs`, `Notification sounds (ringtones)`, `Wallpapers`.
- Speech/text: `Voice message transcription`, `Message translation`.
- Stickers/emoji: `Stickers`, `Animated dice`, `Animated emojis`, `Emoji categories and animated emoji`, `Custom emoji in messages`.
- Chats: `Message drafts`, `Dialog folders`, `Top peer rating`, `Telegram Business`.
- Accounts: `BotFather`, `Account deletion`, `Third-party bot verification`, `Telegram Passport`, `Fragment / multiple usernames`.
- Operations: `Build & Deploy`, `Temporary auth keys (PFS)`, `Debugging`, `WebRTC / Calls`.

## Non-negotiable rules

- **No stubs unless explicitly requested.** No fabricated successful response, dummy translation, unexplained empty vector, `NotImplementedException`, or log-and-return-default. If infrastructure is missing (CDN/FileServer/WebProxy), explain and ask.
- Take caller identity from **`input.UserId`**, never a client-supplied `obj.UserId`. Validate input, permissions and access hashes; upstream validation is not universal.
- Use **`RpcErrors...ThrowRpcError()`**, not generic exceptions for RPC failures.
- Initialize every **`TVector<T>`** and every required TL field. Shared flag bits require all associated nullable fields together. Dates on the wire are Unix seconds; store timestamps as `long`, cast to TL `int` deliberately.
- **Never edit generated `MyTelegram.Schema` code.** Alternate constructors use the existing LayerN schema/converter/forward-handler pattern. An unregistered constructor can leave a request unanswered forever.
- **Never write `eventflow-*` directly**; use aggregates and emit events. Only the documented closed-file-server workarounds for server-published media/web files are sanctioned exceptions; read the reference before touching them. Historical manual database examples do not authorize new exceptions.
- Never commit `.env` or secrets. Never use public STUN servers in production. Use injected options for addresses, ports and credentials.
- Batch queries rather than N+1. BSON numeric values may be Int32/Int64/Double; file references may be Binary/Array/absent. Nested TL `Id` may be stored as `_id` or `Id`.
- Temporary probes belong in `$TMPDIR` or `/tmp`, **not `scripts/`**, and are not committed. Ask before promoting a probe to maintained tooling.
- Do not commit or deploy unless requested. Preserve unrelated working-tree changes.

## Layout

Under `source/src/`:
- `MyTelegram.Messenger/Handlers/LatestLayer/<Category>/`: new RPC handlers.
- `MyTelegram.Messenger/Services/`: application services.
- `MyTelegram.Messenger/Converters/`: entity-to-TL mapping.
- `MyTelegram.Domain/`: aggregates/events.
- `MyTelegram.GatewayServer/`: MTProto gateway.
- `MyTelegram.QueryHandlers.MongoDB/`: read-model queries.
- `MyTelegram.Schema/`: generated TL entities, do not edit.

`build/docker/`: image build scripts. `docker/compose/`: deployment. `scripts/`: maintained seeders/verifiers. `docs/`: reference material.

## Implementation workflow

1. **Research is mandatory:** `/schema-jppgr-am search <method>` for constructor/signature; `https://core.telegram.org/method/<method>` for semantics/errors; TDLib and official clients for complex behavior. Prefer Google Custom Search API/Yandex XML API to the limited built-in web search.
2. Read the relevant section of `docs/development-reference.md`, then inspect existing handlers/services and affected call paths.
3. Implement through constructor-injected services (`IUserAppService`, `IMessageAppService`, `IPeerHelper`, `IQueryProcessor`, `IMongoDatabase`, `ILogger<T>` as appropriate).
4. Run `dotnet test`, rebuild the image when deployment is authorized, test with the **official client**, inspect logs and MongoDB. Report skipped or blocked verification honestly.
5. **Client-rendered surfaces must be compared with real Telegram**, not declared healthy from logs. Here “prod” means real Telegram (`api.telegram.org`, DC `149.154.167.51`), never this deployment. Call each surface method on both with `hash = 0`; compare counts, order and contents. Follow returned IDs through `getCustomEmojiDocuments`/`getStickerSet` to a real `upload.getFile`. Re-quote hashes and verify `*NotModified`; non-empty lists must not use zero. Respect real Telegram flood waits; `getTopPeers` permits only a small probe budget.

A successful RPC does not prove a client can render the response. Empty lists, zero hashes, missing bodies and wrong IDs often produce no error logs.

## Handler and event patterns

- `internal sealed class`, namespace `MyTelegram.Messenger.Handlers.LatestLayer.<Category>`.
- Inherit the matching `RpcResultObjectHandler<Request, Result>`; XML summary links to the API. Check with `/check-handler <path>`.
- Resolve peers through `IPeerHelper.GetPeer(...)` on both write/read paths. Self peers normalize to `PeerType.Self`, not `PeerType.User`.
- `SendMessageAsync` is asynchronous event sourcing. Never fabricate the created message in a response; it arrives through push. `TUpdates` requires initialized `Updates`, `Users`, `Chats`, plus `Date` and `Seq`.
- `return null!` is valid only when a domain-event handler will send the RPC response; otherwise the client waits forever.
- Request commands deduplicate by request `msg_id`; multiple commands to the same aggregate can collapse. Use the established batch/TempId patterns.
- Pushes normally exclude **`PermAuthKeyId`**, not the temporary `AuthKeyId`. Exceptions such as transcription must reach the caller too.
- Durable updates such as drafts must use the established domain-event push path with `globalSeqNo` for offline recovery, not only immediate object sending.
- Channel fan-out belongs to session-server: push to the channel peer, do not enumerate members yourself.
- Optional input fields mean “leave unchanged” unless the protocol says otherwise; do not replace whole settings with defaults.

## Protocol constraints worth remembering

Read the reference for full rules and evidence; this list is only a guard against common regressions.

### Media and hashes
- File bodies live in MinIO `tg-files`: `{fileId}`, thumbnails `{fileId}_{sizeType}`. Client uploads are AES-256-CTR encrypted with key/IV in the file read model; server-created bodies are plain. Document metadata normally belongs to the external file-server via gRPC.
- `SaveFile` caps around 4 MB; larger server-created bodies use `IStoredFileStorage`, then register metadata. The closed file-server also handles ordinary `upload.getFile` and several upload routes.
- Do not change the access-hash byte layout: deployed binaries depend on it.
- File references are minted/stamped centrally. Only add refusal checks after reading the method's documented errors; many methods deliberately accept cached references. Preserve exact indexed/cover error spelling.
- First deployment of file-reference validation is **LogOnly for a full day**, then inspect logs before Enforce. Unknown/unset mode defaults to LogOnly.
- Client-computed hashes require the documented **unsigned** `VectorHashHelper` algorithm and exact input order. Do not reuse signed `MessageSearchMongoHelper.CalcHash`, randomized `System.HashCode`, or session-specific fields.
- Hash inputs differ: installed stickers use set hashes; featured stickers use set IDs plus unread markers; many media lists use ordered document IDs. Some hashes are server-defined and merely need stability. Consult the specific surface.
- `AppConfigHelper._hash` is derived from the serialized config, so table changes need no manual bump; per-account/session fields must be appended and folded into its response hash.

### Stickers and emoji
- **Keep `config.preload_featured_stickers` off**: with featured emoji it crash-loops Android (`length=6; index=6`).
- Mirror special animated sets and emoji taxonomy from real Telegram. Preserve document alts and keycap packs; category icons are custom-emoji documents, not system glyphs.
- Curated avatar/background pickers need seeded lists and downloadable documents. Seeded custom emoji are intentionally free; copying Premium-only flags locks the UI.
- Sticker mutations return complete sets and push updates. Lists enforce advertised limits and stable order before hashing.
- Private-chat emoji interactions need message-ID translation between each participant's numbering.
- Dice outcomes are server-generated with emoji-specific ranges. No edits/albums/drafts; private-chat revoke waits 24 hours, with documented exemptions.

### Other surfaces
- GIFs are silent MPEG4 plus `documentAttributeAnimated`; sending saves them, re-saving moves to front. Android inline previews need proxied registered web documents.
- `uploadRingtone` itself saves the sound. Duration comes from real audio probing, rounded to nearest second; conversion replaces the original list entry with a real MP3 document. Notification sound fields are platform-specific and mute-only writes must preserve them.
- Transcription returns pending quickly with nonzero ID, then pushes the matching final update to all caller sessions within the client timeout. Failed work must not leave a spinner forever. Trials are charged once, refunded on final failure; exhaustion is `FLOOD_WAIT_%d`. Deepgram accepts supported OGG/MP4 directly; other providers may need conversion.
- Translation results are positional: exactly one per input. Preserve UTF-16 entity offsets; Premium formatting is carried through HTML. Cache paid results. Missing provider is an honest RPC error, never fabricated text. Both `translations_*_enabled` AppConfig keys are needed by clients.
- Drafts belong to dialogs/topics; empty means clear, and clears must update both draft and dialog read models through events. Send-time draft ownership is the requesting user, not a channel outbox owner.
- Folder order includes default ID 0; custom IDs start at 2. Peer folders allow only 0/1; absent folder means 0. Shared-folder identity is the invite slug, not the exporter's filter ID.
- Top-peer hash spans requested categories in client enum order. Resets are per category; usage-derived categories need explicit recording. Cross-process cache visibility may lag 60 seconds.
- Wallpapers have a catalogue and a per-user list. Removing preinstalled entries needs tombstones. `Listed` is not `IsDefault`. A wallpaper invitation accepted by message ID is not removal; ID 0 with emoticon settings is a channel wallpaper.
- Account deletion protects system/support accounts and bots, retains messages, and respects delayed 2FA deletion. Passport is an encrypted blind relay, not server-decryptable data.

## Build, deployment and diagnosis

Use Docker Compose **v2**, always **`-p mytelegram`**. A bare Compose invocation can start a duplicate stack and lock MongoDB's data directory. Run Compose in `docker/compose/` or pass its file explicitly.

```bash
# Tests
 dotnet test source/MyTelegram.slnx

# Build one image (from build/docker)
./1.build-messenger-command-server.sh

# Restart with the built image (from docker/compose; requires deployment authorization)
docker compose -p mytelegram up -d messenger-command-server

# Inspect logs
docker compose -p mytelegram logs --since 10m messenger-command-server
```

Build scripts: `1.` command-server, `2.` query-server, `4.` sms-sender, `5.` gateway-server, `6.` auth-server, `7.` data-seeder. `build-all-amd64.sh` builds all images.

Check disk space before builds/tests. Do not blindly prune Docker caches: a known unavailable Ubuntu mirror can make rebuilding impossible without cache. The historical clean-build/prune recipe in the reference is not a standing instruction to delete caches.

- Unsupported constructor + hanging RPC: registration/LayerN compatibility, not necessarily a network problem.
- Half the media stuck with no `RequestGetFile`: check session-server `authKey not found`. PFS temp keys exist only in memory and vanish on restart; lifetime must exceed Android's 24 hours (`App__TempAuthKeyExpirationMinutes=1500` here).
- Global media failures with `FILE_REFERENCE_*`: inspect refresh sources and validation mode.
- Clean logs but blank UI: compare real data, flags, hashes and downloadable bodies against Telegram.
- Telethon probes need this server's RSA public key; copy the connection/RSA block from `scripts/verify_stickers.py`. Use authorized existing sessions rather than repeated logins. Never expose auth keys in output.
- Check which host a probe contacts before attributing clock skew or failures to this deployment.
- Detailed symptom-to-cause mapping and Mongo collection fields are in `docs/development-reference.md`.

## Resources

- API: https://core.telegram.org/api and https://core.telegram.org/methods
- Schema: https://schema.jppgr.am (`/schema-jppgr-am search`, `diff`, `layer`, `hex2object`)
- TDLib: https://github.com/tdlib/td
- Android: https://github.com/DrKLO/Telegram
- Desktop: https://github.com/telegramdesktop/tdesktop
- Detailed project reference: [docs/development-reference.md](docs/development-reference.md)
