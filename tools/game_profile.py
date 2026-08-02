#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""游戏实例配置：当前魔力 / 魔力永恒 / 序章。"""
from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
PROJECT = TOOLS.parent


def _resolve_seqchapter_root() -> Path:
    """优先环境变量 / tools 上一级游戏目录 / 开发默认路径。"""
    env = (os.environ.get("SEQCHAPTER_ROOT") or "").strip()
    if env:
        return Path(env)
    parent = TOOLS.parent
    if (parent / "cg37.exe").is_file() and (parent / "cg37_Data").is_dir():
        return parent
    # 傻瓜补丁包：animator/ 或 tools/ 旁再找上级游戏目录
    for cand in (parent.parent, parent.parent.parent):
        if (cand / "cg37.exe").is_file() and (cand / "cg37_Data").is_dir():
            return cand
    return Path(r"E:\cross\魔力宝贝：序章")

CONFIG_BUNDLE_NAME = "4bd60e623f3f8796cb234b3f01f0c91a.b"

# 跨版本头像已知问题（暂禁，仅做战斗外形）
ETERNAL_CROSS_HEAD_KNOWN_ISSUE = (
    "魔力永恒头像贴到当前魔力会出现白图：永恒 pethead 图集 UV/纹理与当前客户端不完全一致，"
    "仅复制 atlas 映射无法正确采样。战斗外形替换不受影响。待单独排查。"
)
CROSS_HEAD_BLOCK: dict[str, str] = {
    "eternal": ETERNAL_CROSS_HEAD_KNOWN_ISSUE,
}


@dataclass(frozen=True)
class GameProfile:
    key: str
    label: str
    root: Path
    data_dir_name: str = "cross_Data"
    exe_name: str = "cross.exe"

    @property
    def data_dir(self) -> Path:
        return self.root / self.data_dir_name

    @property
    def assets(self) -> Path:
        return self.data_dir / "assets"

    @property
    def animdata_dir(self) -> Path:
        return self.assets / "clientresource" / "animdata"

    @property
    def animdata_info(self) -> Path:
        return self.animdata_dir / "animdatainfo.bin"

    @property
    def animdata_bin(self) -> Path:
        return self.animdata_dir / "animdata.bin"

    @property
    def animdata_mode(self) -> str:
        """monolithic = animdata.bin；bundle_only = 仅 .b 包内 animdata。"""
        if self.animdata_bin.exists():
            return "monolithic"
        return "bundle_only"

    @property
    def config_bundle(self) -> Path:
        return self.assets / CONFIG_BUNDLE_NAME

    @property
    def cache_dir(self) -> Path:
        return TOOLS / f"pet_anim_cache_{self.key}" if self.key != "local" else TOOLS / "pet_anim_cache"

    @property
    def bundle_map_file(self) -> Path:
        return self.cache_dir / "bundle_map.json"

    @property
    def pet_index_file(self) -> Path:
        return self.cache_dir / "pet_index.json"

    @property
    def store(self) -> Path:
        if self.key == "local":
            return TOOLS / "pet_anim_store"
        return TOOLS / f"pet_anim_store_{self.key}"

    @property
    def manifest_file(self) -> Path:
        return self.store / "manifest.json"

    @property
    def global_dir(self) -> Path:
        return self.store / "global"

    @property
    def appearances_dir(self) -> Path:
        return self.store / "appearances"

    @property
    def table_csv(self) -> Path:
        suffix = "" if self.key == "local" else f"_{self.key}"
        return TOOLS / f"pet_swap_table{suffix}.csv"

    @property
    def table_md(self) -> Path:
        suffix = "" if self.key == "local" else f"_{self.key}"
        return TOOLS / f"pet_swap_table{suffix}.md"

    def exists(self) -> bool:
        return self.root.is_dir() and self.assets.is_dir()


LOCAL = GameProfile(
    key="local",
    label="当前魔力",
    root=PROJECT,
)

ETERNAL = GameProfile(
    key="eternal",
    label="魔力永恒",
    root=Path(r"D:\Desktop\魔力永恒\魔力永恒"),
)

# 序章：可用环境变量 SEQCHAPTER_ROOT 覆盖；默认尝试 tools 上一级或开发目录
# 勿与本仓库 cross.exe / crossgate_cursor 混淆
SEQCHAPTER = GameProfile(
    key="seqchapter",
    label="魔力宝贝序章",
    root=_resolve_seqchapter_root(),
    data_dir_name="cg37_Data",
    exe_name="cg37.exe",
)

PROFILES: dict[str, GameProfile] = {
    LOCAL.key: LOCAL,
    ETERNAL.key: ETERNAL,
    SEQCHAPTER.key: SEQCHAPTER,
}
