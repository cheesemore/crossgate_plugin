#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""瘦客户端：animdata 在 .b 包内 TextAsset，无 animdata.bin（当前未维护）。"""
from __future__ import annotations

from pathlib import Path

import UnityPy


def extract_animdata_from_bundle(bundle_path: Path) -> bytes:
    raw = bundle_path.read_bytes()
    off = raw.find(b"UnityFS")
    if off < 0:
        raise RuntimeError(f"UnityFS missing: {bundle_path}")
    env = UnityPy.load(raw[off:])
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        ta = obj.read()
        if ta.m_Name == "animdata":
            s = ta.m_Script
            return s if isinstance(s, bytes) else s.encode("utf-8", "surrogateescape")
    raise RuntimeError(f"包内无 animdata: {bundle_path.name}")


def write_animdata_to_bundle(bundle_path: Path, chunk: bytes) -> None:
    raw = bundle_path.read_bytes()
    off = raw.find(b"UnityFS")
    if off < 0:
        raise RuntimeError(f"UnityFS missing: {bundle_path}")
    env = UnityPy.load(raw[off:])
    found = False
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        ta = obj.read()
        if ta.m_Name != "animdata":
            continue
        if isinstance(chunk, bytes):
            ta.m_Script = chunk.decode("utf-8", "surrogateescape")
        else:
            ta.m_Script = chunk
        ta.save()
        found = True
        break
    if not found:
        raise RuntimeError(f"包内无 animdata: {bundle_path.name}")
    out = raw[:off] + env.file.save(packer="lz4")
    bundle_path.write_bytes(out)


def bundle_has_animdata(bundle_path: Path) -> bool:
    try:
        chunk = extract_animdata_from_bundle(bundle_path)
        return len(chunk) > 0
    except Exception:
        return False
