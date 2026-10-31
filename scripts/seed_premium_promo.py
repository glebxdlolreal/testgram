#!/usr/bin/env python3
"""
Premium promo seeder for MyTelegram server.

Usage:
  1. Download premium promo data from Telegram:
       TG_API_ID=... TG_API_HASH=... TG_PHONE=... python3 seed_premium_promo.py --download

  2. Import downloaded promo media into the server:
       MONGO_URL=mongodb://localhost:27017 \
       MINIO_ENDPOINT=localhost:9000 \
       MINIO_ACCESS_KEY=... \
       MINIO_SECRET_KEY=... \
       python3 seed_premium_promo.py --import
"""
import asyncio
import base64
import argparse
import io
import json
import os
import time
from pathlib import Path
from typing import Any, Dict, List

TG_API_ID = int(os.environ.get("TG_API_ID", "0"))
TG_API_HASH = os.environ.get("TG_API_HASH", "")
TG_PHONE = os.environ.get("TG_PHONE", "")

MONGO_URL = os.environ.get("MONGO_URL", "mongodb://localhost:27017")
MINIO_ENDPOINT = os.environ.get("MINIO_ENDPOINT", "localhost:9000")
MINIO_ACCESS_KEY = os.environ.get("MINIO_ACCESS_KEY", "")
MINIO_SECRET_KEY = os.environ.get("MINIO_SECRET_KEY", "")
MINIO_BUCKET = "tg-files"
DC_ID = 1

OUT_DIR = Path("premium_promo_files")
MANIFEST_FILE = Path("premium_promo_manifest.json")

MIME_TO_EXT = {
    "application/x-tgsticker": "tgs",
    "video/webm": "webm",
    "image/webp": "webp",
    "image/png": "png",
    "image/gif": "gif",
    "video/mp4": "mp4",
}


def get_file_ext(mime_type: str, file_name: str = "") -> str:
    if mime_type in MIME_TO_EXT:
        return MIME_TO_EXT[mime_type]
    if file_name:
        ext = Path(file_name).suffix.lower().lstrip(".")
        if ext:
            return ext
    return "bin"


def to_int64(v):
    if isinstance(v, dict):
        val = (v.get("high", 0) << 32) | (v.get("low", 0) & 0xFFFFFFFF)
        return val - (1 << 64) if val >= (1 << 63) else val
    if isinstance(v, int):
        return v
    return int(v)


def build_input_stickerset_id(set_id: int, set_access_hash: int) -> Dict[str, Any]:
    # "_id", not "Id" — see the same note in seed_stickers.py: the C# driver reads a nested TL object's
    # Id member from the _id element, and the other spelling silently yields id 0.
    return {
        "_t": "TInputStickerSetID",
        "_id": set_id,
        "AccessHash": set_access_hash,
    }


def build_primary_attribute(doc: Dict[str, Any]) -> Dict[str, Any] | None:
    sticker_set = doc.get("sticker_set")
    alt = doc.get("alt", "")
    if not sticker_set:
        return None
    set_id = to_int64(sticker_set.get("id", 0))
    access_hash = to_int64(sticker_set.get("access_hash", 0))
    if doc.get("is_custom_emoji"):
        return {
            "_t": "TDocumentAttributeCustomEmoji",
            "Free": bool(doc.get("free", False)),
            "TextColor": bool(doc.get("text_color", False)),
            "Alt": alt,
            "Stickerset": build_input_stickerset_id(set_id, access_hash),
        }
    return {
        "_t": "TDocumentAttributeSticker",
        "Alt": alt,
        "Mask": False,
        "Stickerset": build_input_stickerset_id(set_id, access_hash),
    }


def build_video_attributes(doc: Dict[str, Any]) -> List[Dict[str, Any]]:
    """documentAttributeVideo + documentAttributeFilename, in the shape the C# driver reads back."""
    attributes: List[Dict[str, Any]] = []
    video = doc.get("video")
    if video:
        attributes.append({
            "_t": "TDocumentAttributeVideo",
            "RoundMessage": video.get("round_message", False),
            "SupportsStreaming": video.get("supports_streaming", False),
            "Nosound": video.get("nosound", False),
            "Duration": float(video.get("duration", 0)),
            "W": int(video.get("w", 0)),
            "H": int(video.get("h", 0)),
            "PreloadPrefixSize": None,
            "VideoStartTs": None,
            "VideoCodec": None,
        })
    if doc.get("file_name"):
        attributes.append({"_t": "TDocumentAttributeFilename", "FileName": doc["file_name"]})
    return attributes


def build_stripped_thumbs(doc: Dict[str, Any]) -> List[Dict[str, Any]] | None:
    """The inline photoStrippedSize clients show while the promo video loads."""
    if not doc.get("stripped_thumb"):
        return None
    data = base64.b64decode(doc["stripped_thumb"])
    return [{"W": 0, "H": 0, "Size": len(data), "Type": "i", "StrippedThumb": data, "Bytes": None}]


def merge_attributes(existing_attributes: Any, new_primary_attribute: Dict[str, Any] | None) -> List[Dict[str, Any]]:
    if new_primary_attribute is None:
        if isinstance(existing_attributes, list):
            return [x for x in existing_attributes if isinstance(x, dict)]
        return []
    attributes = [new_primary_attribute]
    if isinstance(existing_attributes, list):
        for attribute in existing_attributes:
            if not isinstance(attribute, dict):
                continue
            if attribute.get("_t") in {"TDocumentAttributeSticker", "TDocumentAttributeCustomEmoji"}:
                continue
            attributes.append(attribute)
    return attributes


async def cmd_download():
    from telethon import TelegramClient
    from telethon.tl import functions, types

    if not TG_API_ID or not TG_API_HASH or not TG_PHONE:
        raise RuntimeError("Set TG_API_ID, TG_API_HASH, TG_PHONE env vars")

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    client = TelegramClient(os.environ.get("TG_SESSION", "premium_promo_seeder"), TG_API_ID, TG_API_HASH)
    await client.start(phone=TG_PHONE)

    promo = await client(functions.help.GetPremiumPromoRequest())
    print(f"Got premium promo: sections={len(promo.video_sections)} videos={len(promo.videos)} options={len(promo.period_options)}")

    sticker_set_cache: Dict[str, Dict[str, Any] | None] = {}
    videos = []
    for index, section in enumerate(promo.video_sections):
        if index >= len(promo.videos):
            print(f"  WARNING: section '{section}' has no matching video")
            continue

        doc = promo.videos[index]
        ext = get_file_ext(doc.mime_type, str(doc.id))
        path = OUT_DIR / f"{section}_{doc.id}.{ext}"
        if not path.exists():
            data = await client.download_media(doc, file=bytes)
            if not data:
                print(f"  WARNING: failed to download doc {doc.id} for section {section}")
                continue
            path.write_bytes(data)
            print(f"  Downloaded {section}: {doc.id}.{ext}")
        else:
            print(f"  Reused {section}: {doc.id}.{ext}")

        # Since 2024 the promo sections are mp4 screen recordings, not stickers: the client plays them
        # as videos, so their documentAttributeVideo (size, duration, streaming) has to travel too.
        video = None
        file_name = ""
        stripped = None
        for thumb in getattr(doc, "thumbs", None) or []:
            if thumb.__class__.__name__ == "PhotoStrippedSize":
                stripped = base64.b64encode(thumb.bytes).decode()
        for attr in getattr(doc, "attributes", []):
            class_name = attr.__class__.__name__
            if class_name == "DocumentAttributeVideo":
                video = {
                    "duration": float(getattr(attr, "duration", 0) or 0),
                    "w": int(getattr(attr, "w", 0) or 0),
                    "h": int(getattr(attr, "h", 0) or 0),
                    "supports_streaming": bool(getattr(attr, "supports_streaming", False)),
                    "round_message": bool(getattr(attr, "round_message", False)),
                    "nosound": bool(getattr(attr, "nosound", False)),
                }
            elif class_name == "DocumentAttributeFilename":
                file_name = attr.file_name

        sticker_set = None
        alt = ""
        is_custom_emoji = False
        free = False
        text_color = False
        for attr in getattr(doc, "attributes", []):
            class_name = attr.__class__.__name__
            if hasattr(attr, "alt") and attr.alt:
                alt = attr.alt
            if class_name == "DocumentAttributeCustomEmoji":
                is_custom_emoji = True
                free = bool(getattr(attr, "free", False))
                text_color = bool(getattr(attr, "text_color", False))
                set_obj = getattr(attr, "stickerset", None)
                if set_obj is not None and hasattr(set_obj, "id"):
                    sticker_set = {"id": set_obj.id, "access_hash": getattr(set_obj, "access_hash", 0)}
            elif class_name == "DocumentAttributeSticker":
                set_obj = getattr(attr, "stickerset", None)
                if set_obj is not None and hasattr(set_obj, "id"):
                    sticker_set = {"id": set_obj.id, "access_hash": getattr(set_obj, "access_hash", 0)}

        if sticker_set is not None:
            set_key = f"{sticker_set['id']}:{sticker_set['access_hash']}"
            if set_key not in sticker_set_cache:
                try:
                    sticker_set_result = await client(functions.messages.GetStickerSetRequest(
                        stickerset=types.InputStickerSetID(id=sticker_set["id"], access_hash=sticker_set["access_hash"]),
                        hash=0,
                    ))
                    sticker_set_cache[set_key] = {
                        "id": sticker_set_result.set.id,
                        "access_hash": sticker_set_result.set.access_hash,
                        "short_name": sticker_set_result.set.short_name,
                        "title": sticker_set_result.set.title,
                    }
                except Exception as e:
                    print(f"  WARNING: failed to resolve sticker set for section {section}: {e}")
                    sticker_set_cache[set_key] = None
            sticker_set = sticker_set_cache[set_key]

        videos.append({
            "section": section,
            "document": {
                "doc_id": doc.id,
                "access_hash": doc.access_hash,
                "mime": doc.mime_type,
                "size": doc.size,
                "file": str(path),
                "ext": ext,
                "alt": alt,
                "is_custom_emoji": is_custom_emoji,
                "free": free,
                "text_color": text_color,
                "sticker_set": sticker_set,
                "video": video,
                "file_name": file_name,
                "stripped_thumb": stripped,
            },
        })

    period_options = []
    for option in promo.period_options:
        period_options.append({
            "months": getattr(option, "months", 0),
            "currency": getattr(option, "currency", ""),
            "amount": getattr(option, "amount", 0),
            "bot_url": getattr(option, "bot_url", ""),
            "store_product": getattr(option, "store_product", ""),
            "current": bool(getattr(option, "current", False)),
        })

    manifest = {
        "status_text": promo.status_text,
        "video_sections": list(promo.video_sections),
        "videos": videos,
        "period_options": period_options,
    }
    MANIFEST_FILE.write_text(json.dumps(manifest, indent=2, ensure_ascii=False))
    print(f"Saved premium promo manifest to {MANIFEST_FILE}")
    await client.disconnect()


def cmd_import() -> int:
    import pymongo
    from minio import Minio

    if not MINIO_ACCESS_KEY or not MINIO_SECRET_KEY:
        print("ERROR: Set MINIO_ACCESS_KEY and MINIO_SECRET_KEY env vars.")
        return 2
    if not MANIFEST_FILE.exists():
        print(f"ERROR: Manifest file {MANIFEST_FILE} not found. Run --download first.")
        return 2

    manifest = json.loads(MANIFEST_FILE.read_text())
    minio = Minio(MINIO_ENDPOINT, access_key=MINIO_ACCESS_KEY, secret_key=MINIO_SECRET_KEY, secure=False)
    if not minio.bucket_exists(MINIO_BUCKET):
        minio.make_bucket(MINIO_BUCKET)

    mongo = pymongo.MongoClient(MONGO_URL)
    db = mongo["tg"]
    doc_col = db["eventflow-documentreadmodel"]
    promo_col = db["premium_promo_media"]

    existing_docs = {
        to_int64(d["DocumentId"]): d
        for d in doc_col.find({}, {"DocumentId": 1, "Attributes2": 1, "Version": 1})
    }

    promo_docs = []
    for order, item in enumerate(manifest.get("videos", []), start=1):
        section = item["section"]
        doc = item["document"]
        doc_id = to_int64(doc["doc_id"]) & 0x7FFFFFFFFFFFFFFF
        path = Path(doc["file"])
        mime = doc.get("mime", "application/octet-stream")
        primary_attribute = build_primary_attribute(doc)

        existing_doc = existing_docs.get(doc_id)
        if existing_doc is not None:
            try:
                minio.stat_object(MINIO_BUCKET, str(doc_id))
            except Exception:
                if path.exists():
                    data = path.read_bytes()
                    minio.put_object(MINIO_BUCKET, str(doc_id), io.BytesIO(data), length=len(data), content_type=mime)
            merged_attributes = build_video_attributes(doc) or merge_attributes(existing_doc.get("Attributes2"), primary_attribute)
            update_fields = {
                "Attributes2": merged_attributes,
                "Version": max(int(existing_doc.get("Version", 1) or 1), 1),
            }
            if doc.get("is_custom_emoji"):
                update_fields["Attributes"] = None
            if doc.get("stripped_thumb"):
                update_fields["Thumbs"] = build_stripped_thumbs(doc)
            doc_col.update_one({"DocumentId": doc_id}, {"$set": update_fields})
        else:
            if not path.exists():
                print(f"  WARNING: file missing for section {section}: {path}")
                continue
            data = path.read_bytes()
            minio.put_object(MINIO_BUCKET, str(doc_id), io.BytesIO(data), length=len(data), content_type=mime)
            document = {
                "_id": f"documentreadmodel-{doc_id}",
                "Id": f"documentreadmodel-{doc_id}",
                "DocumentId": doc_id,
                "LocalFile": str(path),
                "AccessHash": to_int64(doc.get("access_hash", 0)) or int.from_bytes(os.urandom(8), "little", signed=True),
                # No FileReference: derived from the document id on the way out.
                # See https://corefork.telegram.org/api/file-references
                "Date": int(time.time()),
                "DcId": DC_ID,
                "MimeType": mime,
                "Size": doc.get("size", path.stat().st_size),
                "Name": path.name,
                "Thumbs": build_stripped_thumbs(doc),
                "VideoThumbs": None,
                "Attributes": None,
                "Attributes2": build_video_attributes(doc) or merge_attributes(None, primary_attribute),
                "CreatorId": None,
                "Fingerprint": None,
                "Md5CheckSum": None,
                "ThumbId": None,
                "VideoThumbId": None,
                "Version": 1,
            }
            doc_col.insert_one(document)
            existing_docs[doc_id] = document

        sticker_set = doc.get("sticker_set") or {}
        promo_docs.append({
            "_id": f"premium-promo-media-{section}",
            "Section": section,
            "StickerSetId": to_int64(sticker_set.get("id", 0)) if sticker_set else 0,
            "ShortName": sticker_set.get("short_name", "") if sticker_set else "",
            "DocumentId": doc_id,
            "Order": order,
            "Version": 1,
        })

    promo_col.delete_many({})
    if promo_docs:
        promo_col.insert_many(promo_docs)
    print(f"Imported premium promo mappings: {len(promo_docs)}")
    return 0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Download/import Telegram Premium promo media for MyTelegram.")
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--download", action="store_true", help="Download promo metadata/media from Telegram via Telethon.")
    mode.add_argument("--import", dest="import_", action="store_true", help="Import downloaded media into MongoDB/MinIO.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.download:
        if not TG_API_ID or not TG_API_HASH or not TG_PHONE:
            print("ERROR: Set TG_API_ID, TG_API_HASH, TG_PHONE env vars.")
            return 2
        asyncio.run(cmd_download())
        return 0

    if args.import_:
        return cmd_import()

    return 2


if __name__ == "__main__":
    raise SystemExit(main())
