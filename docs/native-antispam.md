# Local native-antispam implementation

This implementation exposes Telegram's native-antispam RPC and admin-log surface, but uses **local deterministic rules, not Telegram's ML classifier**. Message content is not sent to external classifiers.

## Activation and identity

Run the existing DataSeeder through the usual authorized deployment procedure before enabling the feature. `AntiSpamActorDataSeeder` creates a bot through the user aggregate, reserving `SystemBotUserIdBase + 2`. `help.getAppConfig.telegram_antispam_user_id` advertises that local ID, not the unrelated account on Telegram's servers. The actor is never inserted into a group's participants or administrators.

Seeding refuses an occupied ID or username without matching creation provenance. `system-antispam-actor` retains the generated identity proof and a readiness marker; incomplete seeding fails explicitly. Investigate conflicting or incomplete identity state rather than renaming an existing account or fabricating readiness. Runtime activation checks the marker and actual bot identity. A missing actor causes an RPC error; it does not silently substitute the owner or notification-service account.

An administrator with `delete_messages` can enable the feature in a supergroup (not a broadcast channel or gigagroup) with at least the advertised minimum membership. Disabling remains possible below that threshold. The setting is event-sourced and exposed in `channelFull.antispam`.

## Rules and limits

Configure `App:AntiSpam` (environment variables use `App__AntiSpam__...`, e.g. `App__AntiSpam__DeleteBurst`).

- **Scope.** Text messages and media captions (including forwards and messages sent as the participant's own channel) by an ordinary participant of an enabled supergroup with at least `telegram_antispam_group_size_min` members, the same AppConfig value `channels.toggleAntiSpam` enforces: as in Telegram, the filter acts wherever it can be enabled. The sender is the real `sender_id`, so the creator, administrators, anonymous administrators (posting as the group), bots and system accounts are exempt. Broadcast channels, gigagroups and linked-channel auto-forwards are never checked.
- **Normalization.** NFKC plus an explicit fold of fullwidth/mathematical/enclosed Latin letters and digits (the solution runs with `InvariantGlobalization`, where `string.Normalize` is a no-op for non-ASCII), zero-width characters, variation selectors, bidi/format controls and emoji removed, whitespace collapsed. The *strict* key keeps case and punctuation; the *loose* key keeps only case-folded letters and digits with common Cyrillic/Greek look-alikes folded to Latin.
- **Link gate.** `messageEntityUrl`, `messageEntityTextUrl` (hidden links) and `messageEntityMention`, plus schemes (also `http:// x`, `hxxp[:]//`), `www.`, `t.me/...`, `@usernames`, and bare or defanged domains (`site.com`, `site[.]com`, `site(dot)ru`, `site . com`) for a curated TLD list. Text outside `MinimumTextLength`..`MaxTextLength` (40..4096) or without a link is ignored.
- **Rule.** `RepeatThreshold` (4) messages with the same strict key or `LooseRepeatThreshold` (5) with the same loose key from one sender in one group within `WindowSeconds` (120). The window is a per-(chat, sender) ring of the last 16 entries; entries older than the window are dropped on every observation (no fixed buckets). Idle windows are swept and at most `MaxTrackedSenders` are kept. Every entry is also written through to `antispam_window` (TTL = window); a cold window, e.g. after a command-server restart, is reloaded from it before evaluation. The hot path stays in memory, which assumes one command-server replica (the current deployment): replicas would each see only their share of a burst.
- **Edits.** An edit that keeps the key keeps the single entry and re-fences the queued deletion at the new message version, dropping effects captured for the old one and rotating the lease so a worker mid-way through the old version loses its CAS steps. Only once mention admission or dispatch has begun is the job left to the fencing protocol, which cancels on the version mismatch; a changed text replaces the entry and is re-evaluated; a text that left scrutiny is removed.
- **Burst deletion.** With `DeleteBurst` (default true) every matching message of the window is queued, not only the triggering one. As in Telegram, each deletion gets its own `channelAdminLogEventActionDeleteMessage` entry by the antispam actor, so every deleted message can be reported with `channels.reportAntiSpamFalsePositive`.
- **Metrics.** Meter `MyTelegram.AntiSpam`: `antispam.triggers{mode}`, `antispam.messages_enqueued`, `antispam.recheck_cancellations{reason}`, `antispam.deletions_confirmed`, `antispam.false_positive_reports` (distinct reports), `antispam.tracked_senders`. Triggers and recheck cancellations are also logged at Information. Calibrate thresholds by the ratio of false-positive reports and recheck cancellations to confirmed deletions.

The worker delays evaluation, rechecks channel state and sender privileges, then uses the standard channel-deletion saga. Durable intents retain original message snapshots and operation identities. The administrator log is written only after a correlated real deletion event, with an idempotent operation key. Destructive dispatch attempts are bounded; reconciliation of already-dispatched operations is independent so an outage does not permanently discard a confirmed deletion's log. Unresolved intents must retain their snapshots; retention applies only to terminal work.

Channel configuration and administrator membership are separate aggregates. Eligibility is revalidated before dispatch; a message-version fence rejects messages changed before deletion execution. Disabling antispam or promoting the sender prevents future eligible dispatches once observed, but an already-dispatched deletion may complete. Rechecks do not constitute an atomic transaction across those aggregates, and projection lag can delay observing a change. Do not represent this as Telegram's production classifier or as a global exactly-once transaction.

## Feedback

`channels.reportSpam` validates and persists feedback; it does not delete messages or ban anyone. Reports are not votes that automatically trigger removal. `channels.reportAntiSpamFalsePositive` accepts an administrator's report only for a real deletion attributed to the dedicated actor in that channel's admin log. It never restores the message. Repeated reports are idempotent.

No deployment or real-client verification is implied by compilation or automated tests. Test the toggle, channelFull refresh and recent-actions display with an official client after an explicitly authorized deployment.

## Channel Pts migration prerequisite

The durable allocator is a **general channel-Pts change**, not merely an antispam option. `IdGenerator` routes channel allocations through one authority; user Pts remain on their previous path. With no `channel_pts_readiness` cutover record, **all channel Pts allocations fail closed**, even when antispam itself is disabled. Do not deploy this build as an ordinary rolling update.

A separately authorized maintenance operation must quiesce every old channel writer before cutover. Inventory discovery cannot prove quiescence, and old processes may still own cached HiLo ranges. Keep them stopped throughout discovery, bootstrap and replacement; do not restart old writers afterwards. No live migration or service stop has been performed as part of development.

`DurableChannelPtsAllocator.DiscoverLegacyInventoryAsync` reads the persisted HiLo sequence (`eventflow.counter`) plus channel/Pts projections, including orphan channel sequences. `BootstrapQuiescedAsync` validates the supplied inventory against those sources before seeding and before publishing readiness. It writes only the allocator's own collections, not `eventflow-*`. The inclusive legacy boundary for persisted high `H` is `H * 1000 + PtsInitId`, checked for overflow and combined with observed Pts. Skipped historic ranges are never handed out again.

The new collections are `channel_pts_counters` (one scalar and at most one pending reservation per channel), `channel_pts_operations` (immutable assigned range per channel/operation), and `channel_pts_readiness`. Unique `_id` lookups are the reservation indexes. Another writer recovers a pending reservation into its journal before the counter can advance; a timeout does not release or reuse it. The returned Pts is the last value of the reserved range. Stable operation retries return that same assignment and reject a changed count.

The operation journal has **no TTL**: ordinary allocations also produce journal rows, so storage grows with traffic and must be monitored/capacity-planned. Never independently delete, expire, or restore journal/counter/fence collections; replay evidence and allocator state must remain consistent. A bounded archival policy is a separate design, not a safe arbitrary TTL.

## Official-client acceptance procedure (not executed)

Prerequisites: explicit authorization to update command-server, query-server and data-seeder images; an official Android/Desktop client connected to the isolated Testgram instance; authorized owner, delete-messages administrator and ordinary sender accounts. Run the actor seeder only in that authorized instance. Do not mutate real Telegram for this test.

1. Use a disposable supergroup with at least the advertised minimum membership (`telegram_antispam_group_size_min`, 10 here against Telegram's 200). Confirm the actor resolves from AppConfig and is absent from actual participants/admin records. Keep a second client session offline for the delivery test.
2. Enable antispam as the delete-messages administrator. Refetch group information: `channelFull.antispam` must be true; the toggle must reach the owner's other session. Repeat the same toggle and expect `CHAT_NOT_MODIFIED`. An administrator without delete-messages must be refused.
3. Send one ordinary link-bearing message as the ordinary sender: it must remain. Send four distinct messages with identical sufficiently long link-bearing text within 120 seconds: verify all four are deleted (with `DeleteBurst`), each with its own Recent Actions entry. Repeat from an administrator and in a disabled group: no automatic deletion. Use ordinary encrypted-at-rest storage as configured; no special unencrypted test-only send path.
4. Open Recent Actions: exactly one delete entry per removed message, attributed to the dedicated local actor, plus the toggle action. Report a false positive twice: the message stays deleted, the report remains a single record. Report spam on another valid message: it must neither disappear nor cause a ban.
5. Reconnect the offline session and let the official client's normal difference synchronization run: it must remove the same server message without requiring a manual history wipe. Compare its remaining history and top message with the online session.
6. Disable the feature, including after membership falls below the activation minimum. New repeated messages must remain. Dispose of only the isolated test group/resources after approval.

Acceptance remains blocked until an authorized deployment and official-client device/session are supplied. Transport acceptance alone is not proof that a client applied the update.
