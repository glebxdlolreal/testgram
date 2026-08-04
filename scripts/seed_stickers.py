#!/usr/bin/env python3
"""
Sticker-set seeder for MyTelegram server.

Usage:
  1. Download ALL sticker files from Telegram:
       TG_API_ID=... TG_API_HASH=... TG_PHONE=... python3 seed_stickers.py --download

  2. Import downloaded files into the server:
       MONGO_URL=mongodb://localhost:27017 \
       MINIO_ENDPOINT=localhost:9000 \
       MINIO_ACCESS_KEY=... \
       MINIO_SECRET_KEY=... \
       python3 seed_stickers.py --import
"""
import asyncio
import io
import json
import os
import time
from collections import defaultdict
from pathlib import Path
from typing import List, Dict, Any, Optional

TG_API_ID = int(os.environ.get("TG_API_ID", "0"))
TG_API_HASH = os.environ.get("TG_API_HASH", "")
TG_PHONE = os.environ.get("TG_PHONE", "")

MONGO_URL = os.environ.get("MONGO_URL", "mongodb://localhost:27017")
MINIO_ENDPOINT = os.environ.get("MINIO_ENDPOINT", "localhost:9000")
MINIO_ACCESS_KEY = os.environ.get("MINIO_ACCESS_KEY", "")
MINIO_SECRET_KEY = os.environ.get("MINIO_SECRET_KEY", "")
MINIO_BUCKET = "tg-files"
DC_ID = 1

OUT_DIR = Path("stickers")
MANIFEST_FILE = Path("stickers_manifest.json")

PREMIUM_PROMO_SECTION_CANDIDATES = [
    ("more_upload", ["EmojiAnimations"]),
    ("faster_download", ["EmojiAroundAnimations"]),
    ("voice_to_text", ["EmojiShortAnimations"]),
    ("no_ads", ["EmojiAppearAnimations"]),
    ("unique_reactions", ["EmojiCenterAnimations"]),
    ("premium_stickers", ["AnimatedEmojies", "AnimatedEmoji"]),
    ("advanced_chat_management", ["EmojiGenericAnimations"]),
    ("profile_badge", ["StatusPack", "EmojiDefaultStatuses"]),
    ("animated_userpics", ["StatusPack", "EmojiDefaultStatuses"]),
    ("app_icons", ["GiftsPremium", "PremiumGifts"]),
]

EXTRA_SHORT_NAME_TARGETS = [
    "AnimatedEmojies",
    "EmojiAnimations",
    "EmojiAroundAnimations",
    "EmojiShortAnimations",
    "EmojiAppearAnimations",
    "EmojiCenterAnimations",
    "EmojiGenericAnimations",
    "RestrictedEmoji",
    "StatusPack",
    "GiftsPremium",
    "tg_placeholders_android",
]

MIME_TO_EXT = {
    "application/x-tgsticker": "tgs",
    "video/webm": "webm",
    "image/webp": "webp",
    "image/png": "png",
    "image/gif": "gif",
}


def serialize_thumbs(document) -> List[Dict[str, Any]]:
    """Convert Telethon document thumbs into MongoDB/manifest-safe dictionaries."""
    from telethon.tl.types import (
        PhotoCachedSize,
        PhotoPathSize,
        PhotoSize,
        PhotoSizeEmpty,
        PhotoSizeProgressive,
        PhotoStrippedSize,
    )

    serialized = []
    for thumb in getattr(document, "thumbs", None) or []:
        if isinstance(thumb, PhotoSize):
            serialized.append({
                "_t": "TPhotoSize",
                "Type": thumb.type,
                "W": thumb.w,
                "H": thumb.h,
                "Size": thumb.size,
            })
        elif isinstance(thumb, PhotoCachedSize):
            serialized.append({
                "_t": "TPhotoCachedSize",
                "Type": thumb.type,
                "W": thumb.w,
                "H": thumb.h,
                "Bytes": list(thumb.bytes),
            })
        elif isinstance(thumb, PhotoSizeProgressive):
            serialized.append({
                "_t": "TPhotoSizeProgressive",
                "Type": thumb.type,
                "W": thumb.w,
                "H": thumb.h,
                "Sizes": list(thumb.sizes),
            })
        elif isinstance(thumb, PhotoStrippedSize):
            serialized.append({
                "_t": "TPhotoStrippedSize",
                "Type": thumb.type,
                "Bytes": list(thumb.bytes),
            })
        elif isinstance(thumb, PhotoPathSize):
            serialized.append({
                "_t": "TPhotoPathSize",
                "Type": thumb.type,
                "Bytes": list(thumb.bytes),
            })
        elif isinstance(thumb, PhotoSizeEmpty):
            serialized.append({
                "_t": "TPhotoSizeEmpty",
                "Type": thumb.type,
            })
    return serialized


async def download_thumbs(client, document, output_dir: Path) -> Dict[str, str]:
    """Download server-backed document thumbs so MyTelegram can serve them."""
    from telethon.tl.types import PhotoSize, PhotoSizeProgressive

    files = {}
    for thumb in getattr(document, "thumbs", None) or []:
        if not isinstance(thumb, (PhotoSize, PhotoSizeProgressive)):
            continue
        path = output_dir / f"{document.id}_thumb_{thumb.type}.bin"
        if not path.exists():
            try:
                data = await client.download_media(document, file=bytes, thumb=thumb)
            except Exception as e:
                print(f"    Thumb {document.id}_{thumb.type}: ERROR {e}")
                continue
            if data:
                path.write_bytes(data)
        if path.exists():
            files[thumb.type] = str(path)
    return files


def upload_thumbs(minio, doc_id: int, thumb_files: Dict[str, str]):
    for thumb_type, file_path in (thumb_files or {}).items():
        path = Path(file_path)
        if not path.exists():
            continue
        data = path.read_bytes()
        minio.put_object(
            MINIO_BUCKET,
            f"{doc_id}_{thumb_type}",
            io.BytesIO(data),
            length=len(data),
        )


def serialize_supporting_attributes(document) -> List[Dict[str, Any]]:
    """Preserve non-sticker attributes needed to describe downloaded documents."""
    from telethon.tl.types import (
        DocumentAttributeAnimated,
        DocumentAttributeFilename,
        DocumentAttributeImageSize,
    )

    serialized = []
    for attribute in getattr(document, "attributes", None) or []:
        if isinstance(attribute, DocumentAttributeImageSize):
            serialized.append({
                "_t": "TDocumentAttributeImageSize",
                "W": attribute.w,
                "H": attribute.h,
            })
        elif isinstance(attribute, DocumentAttributeFilename):
            serialized.append({
                "_t": "TDocumentAttributeFilename",
                "FileName": attribute.file_name,
            })
        elif isinstance(attribute, DocumentAttributeAnimated):
            serialized.append({"_t": "TDocumentAttributeAnimated"})
    return serialized


def get_file_ext(mime_type: str, file_name: str = "") -> str:
    if mime_type in MIME_TO_EXT:
        return MIME_TO_EXT[mime_type]
    if file_name:
        ext = Path(file_name).suffix.lower().lstrip(".")
        if ext in ["tgs", "webm", "webp", "png", "gif"]:
            return ext
    return "bin"


def build_section_short_name_map() -> Dict[str, List[str]]:
    mapping: Dict[str, List[str]] = defaultdict(list)
    for section, short_names in PREMIUM_PROMO_SECTION_CANDIDATES:
        for short_name in short_names:
            mapping[short_name].append(section)
    return dict(mapping)


def get_promo_sections_for_set(short_name: str, slug: str) -> List[str]:
    section_map = build_section_short_name_map()
    values = section_map.get(short_name, []).copy()
    for section in section_map.get(slug, []):
        if section not in values:
            values.append(section)
    return values


async def fetch_sticker_set(
    client,
    input_set,
    fallback_name: str,
    slug: Optional[str] = None,
    input_stickerset_type: Optional[str] = None,
) -> Optional[Dict[str, Any]]:
    from telethon.tl import functions

    try:
        result = await client(functions.messages.GetStickerSetRequest(stickerset=input_set, hash=0))
    except Exception as e:
        print(f"    ERROR: {e}")
        return None

    s = result.set
    effective_short_name = getattr(s, "short_name", None) or slug or fallback_name
    effective_slug = slug or effective_short_name
    promo_sections = get_promo_sections_for_set(effective_short_name, effective_slug)
    print(f"    id={s.id} short_name={effective_short_name} count={s.count}")

    set_dir = OUT_DIR / fallback_name
    set_dir.mkdir(parents=True, exist_ok=True)

    docs = []
    downloaded_count = 0
    skipped_count = 0
    for doc in result.documents:
        ext = get_file_ext(doc.mime_type, str(doc.id))
        path = set_dir / f"{doc.id}.{ext}"

        if not path.exists():
            data = await client.download_media(doc, file=bytes)
            if data:
                path.write_bytes(data)
                print(f"    Downloaded {doc.id}.{ext}")
                downloaded_count += 1
            else:
                print(f"    FAILED to download {doc.id}")
                continue
        else:
            skipped_count += 1

        docs.append({
            "doc_id": doc.id,
            "access_hash": doc.access_hash,
            "mime": doc.mime_type,
            "size": doc.size,
            "file": str(path),
            "ext": ext,
            "thumbs": serialize_thumbs(doc),
            "thumb_files": await download_thumbs(client, doc, set_dir),
            "attributes": serialize_supporting_attributes(doc),
        })

    packs = []
    for p in result.packs:
        pack_doc_ids = []
        for d in p.documents:
            doc_id = d.id if hasattr(d, "id") else d
            pack_doc_ids.append(doc_id)
        packs.append({
            "emoticon": p.emoticon,
            "documents": pack_doc_ids,
        })

    return {
        "manifest": {
            "name": fallback_name,
            "slug": effective_slug,
            "set_id": s.id,
            "set_access_hash": s.access_hash,
            "short_name": effective_short_name,
            "title": s.title,
            "documents": docs,
            "packs": packs,
            "promo_sections": promo_sections,
            "input_stickerset_type": input_stickerset_type,
        },
        "downloaded_count": downloaded_count,
        "skipped_count": skipped_count,
    }


async def cmd_download():
    from telethon import TelegramClient
    from telethon.tl import functions, types

    if not TG_API_ID or not TG_API_HASH or not TG_PHONE:
        print("ERROR: Set TG_API_ID, TG_API_HASH, and TG_PHONE environment variables")
        return

    client = TelegramClient("sticker_seeder", TG_API_ID, TG_API_HASH)
    await client.start()
    await client.sign_in(TG_PHONE)

    manifest = []
    downloaded_count = 0
    skipped_count = 0
    processed_keys = set()

    # Check if --set argument is provided
    import sys
    target_set = None
    for i, arg in enumerate(sys.argv):
        if arg == "--set" and i + 1 < len(sys.argv):
            target_set = sys.argv[i + 1]
            break

    if target_set:
        print(f"\n=== Fetching single sticker set: {target_set} ===")
        payload = await fetch_sticker_set(
            client,
            types.InputStickerSetShortName(short_name=target_set),
            target_set,
            target_set,
        )
        if payload:
            manifest.append(payload["manifest"])
            downloaded_count += payload["downloaded_count"]
            skipped_count += payload["skipped_count"]
        else:
            print(f"ERROR: Could not fetch sticker set {target_set}")
    else:
        print("\n=== Fetching featured sticker sets ===")
        try:
            featured_result = await client(functions.messages.GetFeaturedStickersRequest(hash=0))
            print(f"Found {len(featured_result.sets)} featured sticker sets")
            all_sets = list(featured_result.sets)
        except Exception as e:
            print(f"Could not fetch featured stickers: {e}")
            all_sets = []

        print(f"\n=== Processing {len(all_sets)} featured sticker sets ===")
        for i, stickerset in enumerate(all_sets):
            short_name = getattr(stickerset, "short_name", None)
            if not short_name:
                print(f"  [{i + 1}/{len(all_sets)}] Skipping set without short_name")
                continue

            print(f"  [{i + 1}/{len(all_sets)}] Processing: {short_name}")
            payload = await fetch_sticker_set(
                client,
                types.InputStickerSetShortName(short_name=short_name),
                short_name,
                short_name,
            )
            if payload is None:
                continue

            key = payload["manifest"]["short_name"]
            if key in processed_keys:
                continue
            processed_keys.add(key)
            manifest.append(payload["manifest"])
            downloaded_count += payload["downloaded_count"]
            skipped_count += payload["skipped_count"]

    print("\n=== Processing special sets ===")
    # Resolve Telegram-owned sets through their actual InputStickerSet constructors.
    # The fetched server short_name is kept as the canonical slug instead of a
    # local alias such as "animated_emoji" or "dice_🎲".
    special_inputs = [
        ("AnimatedEmojies", types.InputStickerSetAnimatedEmoji(), "inputStickerSetAnimatedEmoji"),
        ("EmojiAnimations", types.InputStickerSetAnimatedEmojiAnimations(), "inputStickerSetAnimatedEmojiAnimations"),
        ("GiftsPremium", types.InputStickerSetPremiumGifts(), "inputStickerSetPremiumGifts"),
        ("EmojiGenericAnimations", types.InputStickerSetEmojiGenericAnimations(), "inputStickerSetEmojiGenericAnimations"),
        ("StatusPack", types.InputStickerSetEmojiDefaultStatuses(), "inputStickerSetEmojiDefaultStatuses"),
        ("Topics", types.InputStickerSetEmojiDefaultTopicIcons(), "inputStickerSetEmojiDefaultTopicIcons"),
        ("StatusPack", types.InputStickerSetEmojiChannelDefaultStatuses(), "inputStickerSetEmojiChannelDefaultStatuses"),
        ("AnimatedDice2", types.InputStickerSetDice(emoticon="🎲"), "inputStickerSetDice:🎲"),
        ("AnimatedDart", types.InputStickerSetDice(emoticon="🎯"), "inputStickerSetDice:🎯"),
        ("AnimatedBasketball", types.InputStickerSetDice(emoticon="🏀"), "inputStickerSetDice:🏀"),
        ("AnimatedPenalty", types.InputStickerSetDice(emoticon="⚽"), "inputStickerSetDice:⚽"),
        ("SlotMachineAnimated", types.InputStickerSetDice(emoticon="🎰"), "inputStickerSetDice:🎰"),
        ("AnimatedBowling", types.InputStickerSetDice(emoticon="🎳"), "inputStickerSetDice:🎳"),
        ("GiftsTons", types.InputStickerSetTonGifts(), "inputStickerSetTonGifts"),
    ]

    for name, input_set, input_stickerset_type in special_inputs:
        print(f"  Processing: {name}")
        payload = await fetch_sticker_set(
            client,
            input_set,
            name,
            input_stickerset_type=input_stickerset_type,
        )
        if payload is None:
            continue

        key = payload["manifest"]["short_name"]
        if key in processed_keys:
            continue
        processed_keys.add(key)
        manifest.append(payload["manifest"])
        downloaded_count += payload["downloaded_count"]
        skipped_count += payload["skipped_count"]

    print("\n=== Processing extra short-name targets ===")
    for short_name in EXTRA_SHORT_NAME_TARGETS:
        if short_name in processed_keys:
            continue

        print(f"  Processing: {short_name}")
        payload = await fetch_sticker_set(
            client,
            types.InputStickerSetShortName(short_name=short_name),
            short_name,
            short_name,
        )
        if payload is None:
            continue

        key = payload["manifest"]["short_name"]
        if key in processed_keys:
            continue
        processed_keys.add(key)
        manifest.append(payload["manifest"])
        downloaded_count += payload["downloaded_count"]
        skipped_count += payload["skipped_count"]

    MANIFEST_FILE.write_text(json.dumps(manifest, indent=2, ensure_ascii=False))
    print(f"\nSaved manifest to {MANIFEST_FILE}")
    print(f"Downloaded: {downloaded_count} files, Skipped: {skipped_count} files")
    await client.disconnect()


def to_int64(v):
    if isinstance(v, dict):
        val = (v.get("high", 0) << 32) | (v.get("low", 0) & 0xFFFFFFFF)
        return val - (1 << 64) if val >= (1 << 63) else val
    if isinstance(v, int):
        return v
    return int(v)


def build_input_stickerset_id(set_id: int, set_access_hash: int) -> Dict[str, Any]:
    return {
        "_t": "TInputStickerSetID",
        "Id": set_id,
        "AccessHash": set_access_hash,
    }


def build_sticker_attribute(set_id: int, set_access_hash: int, alt: str, mask: bool = False) -> Dict[str, Any]:
    return {
        "_t": "TDocumentAttributeSticker",
        "Alt": alt,
        "Stickerset": build_input_stickerset_id(set_id, set_access_hash),
        "Mask": mask,
    }


def build_custom_emoji_attribute(set_id: int, set_access_hash: int, alt: str, free: bool, text_color: bool) -> Dict[str, Any]:
    return {
        "_t": "TDocumentAttributeCustomEmoji",
        "Free": free,
        "TextColor": text_color,
        "Alt": alt,
        "Stickerset": build_input_stickerset_id(set_id, set_access_hash),
    }


def merge_attributes(existing_attributes: Any, new_primary_attribute: Dict[str, Any]) -> List[Dict[str, Any]]:
    attributes = [new_primary_attribute]
    seen_supporting_attributes = set()
    if isinstance(existing_attributes, list):
        for attribute in existing_attributes:
            if not isinstance(attribute, dict):
                continue
            attribute_type = attribute.get("_t") or ""
            if attribute_type.endswith(("TDocumentAttributeSticker", "TDocumentAttributeCustomEmoji")):
                continue
            key = json.dumps(attribute, sort_keys=True, ensure_ascii=False)
            if key in seen_supporting_attributes:
                continue
            seen_supporting_attributes.add(key)
            attributes.append(attribute)
    return attributes


def build_set_keywords(entry: Dict[str, Any]) -> List[Dict[str, Any]]:
    keywords = []
    seen = set()
    doc_emoticon_map: Dict[int, str] = {}
    for pack in entry.get("packs", []):
        emoticon = pack.get("emoticon") or ""
        for raw_doc_id in pack.get("documents", []):
            doc_emoticon_map[to_int64(raw_doc_id) & 0x7FFFFFFFFFFFFFFF] = emoticon

    for raw_doc_id, emoticon in doc_emoticon_map.items():
        tokens = [emoticon]
        if entry.get("is_custom_emoji"):
            tokens.append((entry.get("title") or "").strip())
            tokens.append((entry.get("short_name") or entry.get("slug") or "").replace("_", " "))
        normalized = []
        for token in tokens:
            token = (token or "").strip().lower()
            if token and token not in normalized:
                normalized.append(token)
        if not normalized:
            continue
        dedupe_key = (raw_doc_id, tuple(normalized))
        if dedupe_key in seen:
            continue
        seen.add(dedupe_key)
        keywords.append({
            "DocumentId": raw_doc_id,
            "Keyword": normalized,
        })
    return keywords


def build_emoji_keyword_docs(entries: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    keyword_map: Dict[str, set[str]] = defaultdict(set)
    for entry in entries:
        if not entry.get("is_custom_emoji"):
            continue
        for pack in entry.get("packs", []):
            emoticon = (pack.get("emoticon") or "").strip()
            if not emoticon:
                continue
            keyword_map[emoticon].add(emoticon)
            title = (entry.get("title") or "").strip().lower()
            short_name = (entry.get("short_name") or entry.get("slug") or "").replace("_", " ").strip().lower()
            if title:
                keyword_map[title].add(emoticon)
            if short_name:
                keyword_map[short_name].add(emoticon)

    docs = []
    version = 1
    for keyword in sorted(keyword_map):
        docs.append({
            "_id": f"emoji-keyword-en-{keyword}",
            "LangCode": "en",
            "Keyword": keyword,
            "Emoticons": sorted(keyword_map[keyword]),
            "Version": version,
        })
        version += 1
    return docs


"""
Emoji category definitions, modelled on the categories the official clients show in the
sticker/emoji/GIF search bar. Each entry is (title, icon emoji, member emojis): the icon is
resolved to a real custom-emoji document id at seed time, because TDLib discards any category
whose icon cannot be resolved (EmojiGroupList::get_emoji_categories_object), which would
otherwise leave iOS/Desktop/tdweb with an empty category bar.
"""
EMOJI_CATEGORY_DEFINITIONS = [
    ("Smileys & People", "😀", [
        "😀", "😃", "😄", "😁", "😆", "😅", "😂", "🙂", "😉", "😊", "😍", "😘", "😗", "😙",
        "🤗", "🤔", "😐", "😑", "😶", "🙄", "😏", "😣", "😥", "😮", "😯", "😪", "😫", "😴",
        "😌", "😛", "😜", "😝", "🤤", "😒", "😓", "😔", "😕", "🙃", "🤑", "😲", "😖", "😞",
        "😟", "😤", "😢", "😭", "😦", "😧", "😨", "😩", "😬", "😰", "😱", "😳", "🤪", "😵",
        "😡", "😠", "😷", "🤒", "🤕", "🤢", "🤧", "😇", "🤠", "🤡", "🤥", "🤓", "😈", "👶",
        "🧘", "🕺", "💃", "👀", "🗣", "👑", "🙋", "🤝", "👋", "👣", "💅", "🧠",
    ]),
    ("Animals & Nature", "🐈", [
        "🐈", "🦮", "🦄", "🐟", "🦠", "🐶", "🐱", "🐭", "🐹", "🐰", "🦊", "🐻", "🐼", "🐨",
        "🐯", "🦁", "🐮", "🐷", "🐸", "🐵", "🙈", "🐔", "🐧", "🐦", "🐤", "🦆", "🦅", "🦉",
        "🐺", "🐗", "🐴", "🦋", "🐌", "🐞", "🐜", "🕷", "🐢", "🐍", "🦎", "🐙", "🦀", "🐠",
        "🐬", "🐳", "🐆", "🦓", "🦍", "🐘", "🐫", "🦒", "🐃", "🐑", "🐐", "🌵", "🌲", "🌳",
        "🌴", "🌱", "🌿", "🍀", "🍁", "🍄", "🌷", "🌹", "🌺", "🌸", "🌼", "🌻", "⭐", "⚡",
        "⛅", "🔥", "❄", "🌈",
    ]),
    ("Food & Drink", "🍔", [
        "🍔", "🍕", "🍣", "🍹", "🎂", "☕", "🍓", "🍽", "🍴", "🍏", "🍎", "🍐", "🍊", "🍋",
        "🍌", "🍉", "🍇", "🫐", "🍒", "🍑", "🥭", "🍍", "🥥", "🥝", "🍅", "🥑", "🍆", "🥔",
        "🥕", "🌽", "🌶", "🥒", "🥬", "🧄", "🧅", "🍞", "🥐", "🥖", "🧀", "🥚", "🍳", "🥞",
        "🥓", "🍗", "🍖", "🌭", "🥪", "🌮", "🌯", "🥗", "🍝", "🍜", "🍲", "🍛", "🍤", "🍚",
        "🍦", "🍰", "🍫", "🍬", "🍭", "🍩", "🍪", "🥛", "🍵", "🍺", "🍻", "🥂", "🍷", "🥃",
    ]),
    ("Activity & Sport", "⚽", [
        "⚽", "🏀", "🏆", "🏁", "🎮", "🎬", "🎵", "🎶", "🎤", "🎙", "🎨", "🎭", "🎩", "🎰",
        "🎳", "🎯", "🎲", "🏈", "⚾", "🎾", "🏐", "🏉", "🥏", "🎱", "🏓", "🏸", "🥊", "🥋",
        "⛳", "🏹", "🎣", "🤿", "🎿", "🛷", "🥌", "🏂", "🏄", "🚴", "🏊", "🤸", "🤼", "🤾",
        "🏋", "🚵", "🤺", "🏇", "🎪", "🎟", "🎫", "🎖", "🏅", "🥇", "🥈", "🥉", "🎓", "🪩",
    ]),
    ("Travel & Places", "✈", [
        "✈", "🚗", "🏠", "🏖", "🧳", "🏔", "🏕", "🚂", "🛥", "🚕", "🚙", "🚌", "🚎", "🏎",
        "🚓", "🚑", "🚒", "🚐", "🚚", "🚛", "🚜", "🛴", "🚲", "🛵", "🏍", "🚨", "🚔", "🚍",
        "🚝", "🚄", "🚅", "🚈", "🚂", "🚆", "🚇", "🚊", "🚉", "🛫", "🛬", "🛩", "💺", "🚁",
        "🛰", "🚀", "🛸", "🛶", "⛵", "🚤", "🛳", "⛴", "🚢", "⚓", "🗺", "🗿", "🗽", "🗼",
        "🏰", "🏯", "🎡", "🎢", "⛲", "⛱", "🏝", "🏜", "🌋", "⛰", "🏛", "🏗", "🌁", "🌃",
    ]),
    ("Objects", "💡", [
        "💡", "💻", "📱", "📰", "📝", "📆", "📁", "🔎", "📣", "📈", "📉", "💎", "💰", "💸",
        "🪙", "💱", "💼", "🧪", "🧮", "🖨", "🩺", "💊", "💉", "🧼", "🪪", "🛃", "🔮", "🎄",
        "🎃", "🎉", "🎁", "🛍", "👜", "🛒", "👠", "💄", "📚", "📺", "📞", "🕓", "⌚", "📷",
        "📹", "🎥", "📽", "🔍", "🔬", "🔭", "📡", "🔋", "🔌", "💾", "💿", "📀", "🖥", "⌨",
        "🖱", "🖲", "🕹", "🗜", "💣", "🔧", "🔨", "🪓", "🔪", "🗝", "🔒", "🔓", "✍", "📌",
    ]),
    ("Symbols & Flags", "❤", [
        "❤", "❗", "❓", "‼", "⁉", "🆒", "🔝", "✅", "⛔", "🔞", "💘", "💤", "💬", "🤖",
        "🗳", "🏴‍☠", "🧡", "💛", "💚", "💙", "💜", "🖤", "🤍", "🤎", "💔", "❣", "💕", "💞",
        "💓", "💗", "💖", "💟", "☮", "✝", "☪", "🕉", "☸", "✡", "🔱", "⚛", "♻", "✳",
        "❇", "™", "©", "®", "〰", "➰", "➿", "🔚", "🔙", "🔛", "🔜", "🔃", "🔄", "🔀",
        "🇺🇸", "🇬🇧", "🇷🇺", "🇩🇪", "🇫🇷", "🇮🇹", "🇪🇸", "🇯🇵", "🇨🇳", "🇰🇷", "🇧🇷", "🇨🇦", "🇦🇺", "🇮🇳",
    ]),
]

"""
Categories offered when picking a custom emoji status; the official clients show a smaller,
status-oriented set here rather than the full emoji taxonomy.
"""
STATUS_CATEGORY_DEFINITIONS = [
    ("Busy", "💼", ["💼", "🕓", "⛔", "🧠", "💻", "📝", "📈", "🔥"]),
    ("Away", "💤", ["💤", "🏝", "🧘", "✈", "🧳", "🏖", "⛅", "🌴"]),
    ("Greeting", "👋", ["👋", "🤝", "❤", "🎉", "🙋", "💘", "😊", "🤗"]),
    ("Eating", "🍴", ["🍴", "☕", "🍕", "🍔", "🍣", "🍰", "🍹", "🍺"]),
    ("On the move", "👣", ["👣", "🚗", "🚂", "🛥", "🏔", "🏕", "🚴", "🏃"]),
    ("Calling", "📞", ["📞", "📱", "💬", "🗣", "🎙", "🎤", "📣", "💻"]),
]

"""
Categories offered when picking a custom emoji as a profile picture.
"""
PROFILE_PHOTO_CATEGORY_DEFINITIONS = [
    ("Faces", "😀", ["😀", "😊", "😍", "🤔", "😎", "🤠", "🤓", "😇", "🙃", "😉"]),
    ("Animals", "🐈", ["🐈", "🦮", "🦄", "🐶", "🐱", "🦊", "🐻", "🐼", "🐯", "🦁"]),
    ("Love", "❤", ["❤", "💘", "💕", "💖", "💝", "🧡", "💛", "💚", "💙", "💜"]),
    ("Nature", "🌸", ["🌸", "🌹", "🌺", "🌻", "🌷", "🍀", "🌲", "🌴", "⭐", "🌈"]),
    ("Hobbies", "🎮", ["🎮", "🎨", "🎵", "🎬", "⚽", "🏀", "📚", "🎯", "🎸", "📷"]),
]

"""
Emojis for the greeting category (emojiGroupGreeting), which clients sort to the top when
choosing a sticker for a business introduction.
"""
GREETING_EMOTICONS = ["👋", "🤝", "❤", "🎉", "😊", "🤗", "🙋", "💘", "☺", "🥳"]


def build_emoticon_icon_map(entries: List[Dict[str, Any]]) -> Dict[str, int]:
    """
    Maps a UTF-8 emoji to the id of a custom-emoji document representing it, so categories can
    carry a real icon_emoji_id. Later sets do not override earlier ones, so the preference order
    of the manifest is respected.
    """
    icon_map: Dict[str, int] = {}
    for entry in entries:
        if not entry.get("is_custom_emoji"):
            continue
        for pack in entry.get("packs", []):
            emoticon = (pack.get("emoticon") or "").strip()
            if not emoticon:
                continue
            for raw_doc_id in pack.get("documents", []):
                doc_id = to_int64(raw_doc_id) & 0x7FFFFFFFFFFFFFFF
                if doc_id and emoticon not in icon_map:
                    icon_map[emoticon] = doc_id
                break
    return icon_map


def resolve_icon_emoji_id(icon_map: Dict[str, int], preferred: str, members: List[str]) -> int:
    """
    Picks an icon document for a category: the preferred emoji if a custom emoji exists for it,
    else the first member emoji that has one. Returns 0 only when the seeded sets cover none of
    them, which the caller reports rather than silently writing an icon-less category.
    """
    for candidate in [preferred, *members]:
        # Emoji in the manifest may lack the variation selector present in our definitions.
        for variant in (candidate, candidate.replace("️", ""), candidate + "️"):
            if variant in icon_map:
                return icon_map[variant]
    return 0


def build_category_docs(
    definitions: List[Any],
    icon_map: Dict[str, int],
    known_emoticons: set,
    group_for: str,
    id_prefix: str,
    start_order: int = 1,
) -> List[Dict[str, Any]]:
    """
    Builds category documents, keeping only member emojis that some seeded custom-emoji set
    actually covers - a category whose emojis match nothing would open an empty grid.
    """
    docs = []
    order = start_order
    for title, icon_emoji, members in definitions:
        available = [emoticon for emoticon in members if emoticon in known_emoticons]
        if not available:
            continue
        icon_emoji_id = resolve_icon_emoji_id(icon_map, icon_emoji, available)
        if not icon_emoji_id:
            print(f"  WARNING: no custom emoji icon for category '{title}' ({group_for}); skipping")
            continue
        slug = title.lower().replace(" & ", "-").replace(" ", "-")
        docs.append({
            "_id": f"{id_prefix}-{slug}",
            "For": group_for,
            "Kind": "default",
            "Title": title,
            "IconEmojiId": icon_emoji_id,
            "Emoticons": available[:64],
            "Order": order,
            "Version": 1,
        })
        order += 1
    return docs


def build_emoji_group_docs(entries: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    custom_entries = [entry for entry in entries if entry.get("is_custom_emoji")]
    icon_map = build_emoticon_icon_map(entries)
    known_emoticons = set(icon_map)

    docs: List[Dict[str, Any]] = []

    # messages.getEmojiGroups - emojis, custom emojis and GIFs.
    docs.extend(build_category_docs(
        EMOJI_CATEGORY_DEFINITIONS, icon_map, known_emoticons, "default", "emoji-group-default"))

    # messages.getEmojiStickerGroups - choosing a sticker. The greeting category goes first so
    # clients sorting greetings to the top for business introductions have one to find.
    greeting_available = [e for e in GREETING_EMOTICONS if e in known_emoticons]
    greeting_icon_id = resolve_icon_emoji_id(icon_map, "👋", greeting_available)
    sticker_start_order = 1
    if greeting_available and greeting_icon_id:
        docs.append({
            "_id": "emoji-group-stickers-greeting",
            "For": "stickers",
            "Kind": "greeting",
            "Title": "Greeting",
            "IconEmojiId": greeting_icon_id,
            "Emoticons": greeting_available[:64],
            "Order": 1,
            "Version": 1,
        })
        sticker_start_order = 2
    else:
        print("  WARNING: no greeting category seeded (no custom emoji icon available)")

    docs.extend(build_category_docs(
        EMOJI_CATEGORY_DEFINITIONS, icon_map, known_emoticons, "stickers", "emoji-group-stickers",
        start_order=sticker_start_order))

    # messages.getEmojiStatusGroups - choosing a custom emoji status.
    docs.extend(build_category_docs(
        STATUS_CATEGORY_DEFINITIONS, icon_map, known_emoticons, "status", "emoji-group-status"))

    # messages.getEmojiProfilePhotoGroups - choosing a profile picture.
    docs.extend(build_category_docs(
        PROFILE_PHOTO_CATEGORY_DEFINITIONS, icon_map, known_emoticons, "profile_photo",
        "emoji-group-profile-photo"))

    # emojiGroupPremium carries no emoticons: clients select all Premium-only content instead,
    # which the server answers via the magic emoticon in messages.searchStickers. Premium-only
    # custom emojis are those with free unset; Premium-only stickers are a separate population
    # (those carrying a Premium effect), so each category is only offered where content exists -
    # otherwise the category opens an empty grid.
    has_premium_emoji = any(
        not entry.get("free", False) and entry.get("packs")
        for entry in custom_entries
    )
    has_premium_stickers = any(
        not entry.get("is_custom_emoji") and entry.get("premium_effect")
        for entry in entries
    )
    premium_targets = []
    if has_premium_emoji:
        premium_targets.append("default")
    if has_premium_stickers:
        premium_targets.append("stickers")
    else:
        print("  NOTE: no Premium-effect stickers in the manifest; skipping the sticker Premium category")

    if premium_targets:
        premium_icon_id = resolve_icon_emoji_id(icon_map, "⭐", ["💎", "👑", "🏆", "🔝"])
        if premium_icon_id:
            for group_for in premium_targets:
                docs.append({
                    "_id": f"emoji-group-{group_for.replace('_', '-')}-premium",
                    "For": group_for,
                    "Kind": "premium",
                    "Title": "Premium",
                    "IconEmojiId": premium_icon_id,
                    # Placed last so it does not displace the content categories.
                    "Order": 100,
                    "Version": 1,
                })
        else:
            print("  WARNING: no premium category seeded (no custom emoji icon available)")

    return docs


def build_featured_emoji_set_docs(entries: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    docs = []
    seen_set_ids = set()
    order = 1
    for entry in entries:
        if not entry.get("is_custom_emoji"):
            continue
        set_id = to_int64(entry["set_id"])
        if set_id in seen_set_ids:
            continue
        seen_set_ids.add(set_id)
        docs.append({
            "_id": f"featured-emoji-set-{set_id}",
            "StickerSetId": set_id,
            "Unread": False,
            "Order": order,
            "Version": 1,
        })
        order += 1
    return docs


def build_premium_promo_docs(entries: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    docs = []
    order = 1
    for section, short_names in PREMIUM_PROMO_SECTION_CANDIDATES:
        matched_entry = None
        for entry in entries:
            entry_short_name = entry.get("short_name") or ""
            entry_slug = entry.get("slug") or ""
            if entry_short_name in short_names or entry_slug in short_names:
                matched_entry = entry
                break
        if matched_entry is None:
            continue
        document_ids = [to_int64(d.get("doc_id")) & 0x7FFFFFFFFFFFFFFF for d in matched_entry.get("documents", [])]
        if not document_ids:
            continue
        docs.append({
            "_id": f"premium-promo-media-{section}",
            "Section": section,
            "StickerSetId": to_int64(matched_entry["set_id"]),
            "ShortName": matched_entry.get("short_name") or matched_entry.get("slug") or "",
            "DocumentId": document_ids[0],
            "Order": order,
            "Version": 1,
        })
        order += 1
    return docs


def infer_set_flags(entry: Dict[str, Any]) -> Dict[str, Any]:
    input_stickerset_type = entry.get("input_stickerset_type") or {
        "AnimatedEmojies": "inputStickerSetAnimatedEmoji",
        "StatusPack": "inputStickerSetEmojiDefaultStatuses",
        "Topics": "inputStickerSetEmojiDefaultTopicIcons",
    }.get(entry.get("short_name") or "", "")
    is_custom_emoji = bool(entry.get("is_custom_emoji")) or input_stickerset_type in {
        "inputStickerSetEmojiDefaultStatuses",
        "inputStickerSetEmojiDefaultTopicIcons",
        "inputStickerSetEmojiChannelDefaultStatuses",
    }
    # Match Telegram's set-level and per-document custom-emoji flags.  Default
    # profile statuses are theme-color emoji, but topic icons are full-color
    # custom emoji.  Marking topic icons as text_color makes clients recolor
    # their TGS payload to theme blue/white.
    text_color = bool(entry.get("text_color")) or input_stickerset_type in {
        "inputStickerSetEmojiDefaultStatuses",
        "inputStickerSetEmojiChannelDefaultStatuses",
    }
    channel_emoji_status = bool(entry.get("channel_emoji_status")) or input_stickerset_type == "inputStickerSetEmojiChannelDefaultStatuses"
    free = bool(entry.get("free"))
    return {
        "is_custom_emoji": is_custom_emoji,
        "text_color": text_color,
        "channel_emoji_status": channel_emoji_status,
        "free": free,
    }


def extract_doc_alt(entry: Dict[str, Any], doc_id: int) -> str:
    for pack in entry.get("packs", []):
        for raw_doc_id in pack.get("documents", []):
            if (to_int64(raw_doc_id) & 0x7FFFFFFFFFFFFFFF) == doc_id:
                return (pack.get("emoticon") or "").strip()
    return ""


def update_metadata_collections(db, manifest: List[Dict[str, Any]]) -> None:
    emoji_keywords_col = db["emoji_keywords"]
    emoji_groups_col = db["emoji_groups"]
    featured_emoji_col = db["featured_emoji_sticker_sets"]
    premium_promo_col = db["premium_promo_media"]

    emoji_keyword_docs = build_emoji_keyword_docs(manifest)
    emoji_group_docs = build_emoji_group_docs(manifest)
    featured_docs = build_featured_emoji_set_docs(manifest)
    premium_promo_docs = build_premium_promo_docs(manifest)

    emoji_keywords_col.delete_many({})
    if emoji_keyword_docs:
        emoji_keywords_col.insert_many(emoji_keyword_docs)

    emoji_groups_col.delete_many({})
    if emoji_group_docs:
        emoji_groups_col.insert_many(emoji_group_docs)

    featured_emoji_col.delete_many({})
    if featured_docs:
        featured_emoji_col.insert_many(featured_docs)

    premium_promo_col.delete_many({})
    if premium_promo_docs:
        premium_promo_col.insert_many(premium_promo_docs)

    print(
        f"Updated emoji metadata: keywords={len(emoji_keyword_docs)}, groups={len(emoji_group_docs)}, "
        f"featured_sets={len(featured_docs)}, premium_promo={len(premium_promo_docs)}"
    )

    if premium_promo_docs:
        for promo_doc in premium_promo_docs:
            print(f"  premium section {promo_doc['Section']} -> {promo_doc['ShortName']} -> {promo_doc['DocumentId']}")
        missing_sections = [section for section, _ in PREMIUM_PROMO_SECTION_CANDIDATES if section not in {x['Section'] for x in premium_promo_docs}]
        if missing_sections:
            print(f"  WARNING: missing premium promo sections: {', '.join(missing_sections)}")
    else:
        print("  WARNING: no premium promo media mappings were generated")


def normalize_manifest(manifest: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    normalized = []
    for entry in manifest:
        flags = infer_set_flags(entry)
        set_keywords = build_set_keywords({**entry, **flags})
        normalized.append({**entry, **flags, "keywords": set_keywords})
    return normalized


def cmd_import():
    import pymongo
    from minio import Minio

    assert MINIO_ACCESS_KEY and MINIO_SECRET_KEY, \
        "Set MINIO_ACCESS_KEY and MINIO_SECRET_KEY env vars"

    if not MANIFEST_FILE.exists():
        print(f"ERROR: Manifest file {MANIFEST_FILE} not found. Run --download first.")
        return

    manifest = normalize_manifest(json.loads(MANIFEST_FILE.read_text()))
    minio = Minio(MINIO_ENDPOINT, access_key=MINIO_ACCESS_KEY,
                  secret_key=MINIO_SECRET_KEY, secure=False)

    try:
        if not minio.bucket_exists(MINIO_BUCKET):
            minio.make_bucket(MINIO_BUCKET)
            print(f"Created bucket: {MINIO_BUCKET}")
    except Exception as e:
        print(f"Bucket check/creation error: {e}")

    mongo = pymongo.MongoClient(MONGO_URL)
    db = mongo["tg"]
    doc_col = db["eventflow-documentreadmodel"]
    set_col = db["eventflow-stickersetreadmodel"]

    existing_docs = {
        to_int64(d["DocumentId"]): d
        for d in doc_col.find({}, {"DocumentId": 1, "Attributes2": 1, "AccessHash": 1, "FileReference": 1, "Date": 1, "DcId": 1, "MimeType": 1, "Size": 1, "Name": 1, "Thumbs": 1, "Version": 1})
    }
    print(f"Found {len(existing_docs)} existing documents in MongoDB")

    for entry in manifest:
        name = entry["name"]
        print(f"\n=== {name} ===")

        set_id = to_int64(entry["set_id"])
        set_access_hash = to_int64(entry["set_access_hash"])
        doc_ids = []

        for doc in entry["documents"]:
            doc_id = to_int64(doc["doc_id"]) & 0x7FFFFFFFFFFFFFFF
            doc_ids.append(doc_id)
            p = Path(doc["file"])
            mime = doc.get("mime", "application/octet-stream")
            thumb_files = doc.get("thumb_files") or {}
            alt = extract_doc_alt(entry, doc_id)
            if entry.get("is_custom_emoji"):
                primary_attribute = build_custom_emoji_attribute(set_id, set_access_hash, alt, entry.get("free", False), entry.get("text_color", False))
            else:
                primary_attribute = build_sticker_attribute(set_id, set_access_hash, alt)

            existing_doc = existing_docs.get(doc_id)
            if existing_doc is not None:
                try:
                    minio.stat_object(MINIO_BUCKET, str(doc_id))
                except Exception:
                    if p.exists():
                        data = p.read_bytes()
                        minio.put_object(MINIO_BUCKET, str(doc_id), io.BytesIO(data), length=len(data), content_type=mime)
                        print(f"  Re-uploaded doc {doc_id}")
                    else:
                        print(f"  WARNING: doc {doc_id} missing in MinIO and file not found")
                upload_thumbs(minio, doc_id, thumb_files)

                merged_attributes = merge_attributes(
                    [*(doc.get("attributes") or []), *(existing_doc.get("Attributes2") or [])],
                    primary_attribute,
                )
                update_fields = {
                    "Attributes2": merged_attributes,
                    "Version": max(int(existing_doc.get("Version", 1) or 1), 1),
                }
                if entry.get("is_custom_emoji"):
                    update_fields["Attributes"] = None

                if doc.get("thumbs"):
                    update_fields["Thumbs"] = doc["thumbs"]

                doc_col.update_one({"DocumentId": doc_id}, {"$set": update_fields})
                print(f"  Updated doc {doc_id} attributes")
                continue

            if not p.exists():
                print(f"  MISSING file {p}")
                continue

            data = p.read_bytes()
            file_ref = list(os.urandom(16))
            access_hash = to_int64(doc.get("access_hash", 0)) or int.from_bytes(os.urandom(8), "little", signed=True)
            ext = doc.get("ext", "bin")

            minio.put_object(MINIO_BUCKET, str(doc_id), io.BytesIO(data), length=len(data), content_type=mime)
            upload_thumbs(minio, doc_id, thumb_files)

            attributes2 = merge_attributes(doc.get("attributes"), primary_attribute)

            document = {
                "_id": f"documentreadmodel-{doc_id}",
                "Id": f"documentreadmodel-{doc_id}",
                "DocumentId": doc_id,
                "LocalFile": str(p),
                "AccessHash": access_hash,
                "FileReference": file_ref,
                "Date": int(time.time()),
                "DcId": DC_ID,
                "MimeType": mime,
                "Size": len(data),
                "Name": p.name,
                "Thumbs": doc.get("thumbs") or None,
                "VideoThumbs": None,
                "Attributes": None,
                "Attributes2": attributes2,
                "CreatorId": None,
                "Fingerprint": None,
                "Md5CheckSum": None,
                "ThumbId": None,
                "VideoThumbId": None,
                "Version": 1,
            }
            doc_col.insert_one(document)
            existing_docs[doc_id] = document
            print(f"  Imported doc {doc_id} ({ext}, {len(data)} bytes)")

        packs = []
        for p in entry.get("packs", []):
            pack_doc_ids = [to_int64(d) & 0x7FFFFFFFFFFFFFFF for d in p.get("documents", [])]
            packs.append({
                "Emoticon": p["emoticon"],
                "Documents": pack_doc_ids,
            })

        set_col.update_one(
            {"_id": f"stickersetreadmodel-{set_id}"},
            {"$set": {
                "_id": f"stickersetreadmodel-{set_id}",
                "StickerSetId": set_id,
                "AccessHash": set_access_hash & 0x7FFFFFFFFFFFFFFF,
                "ShortName": entry["short_name"],
                "Title": entry["title"],
                "Slug": entry["slug"],
                "Count": len(doc_ids),
                "DocumentIds": doc_ids,
                "Packs": packs,
                "Keywords": entry.get("keywords", []),
                "Emojis": entry.get("is_custom_emoji", False),
                "TextColor": entry.get("text_color", False),
                "ChannelEmojiStatus": entry.get("channel_emoji_status", False),
                "Version": 1,
            }},
            upsert=True,
        )
        print(f"  Upserted sticker set {set_id} ({len(doc_ids)} docs)")

    update_metadata_collections(db, manifest)
    print("\nDone!")


if __name__ == "__main__":
    import sys
    if "--download" in sys.argv:
        asyncio.run(cmd_download())
    elif "--import" in sys.argv:
        cmd_import()
    else:
        print(__doc__)
