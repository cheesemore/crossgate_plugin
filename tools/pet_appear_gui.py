#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""【游戏外第三方工具】动画形象预览 + 写出 battle_appear.json。

人物/宠物/坐骑外形预览共用同一套 AnimationId（animdata）。
战斗配置里 pet_anim、char_anim 填该 ID；ride_skin 用坐骑下拉（配置 Id）。

不进游戏包。游戏内进战替换由 SeqChapterBattleAppear + 百科「形象」页完成。
"""
from __future__ import annotations

import json
import re
import struct
import sys
import threading
import tkinter as tk
from pathlib import Path
from tkinter import messagebox, scrolledtext, ttk

from PIL import Image, ImageTk

def _bootstrap_tool_paths() -> Path:
    """tools 目录：脚本旁优先 / PyInstaller MEIPASS / 傻瓜补丁包 animator|tools。

    注意：必须让「脚本所在 tools」始终在 sys.path 最前。
    若把其它仓库（如 crossgate_cursor）insert(0)，会抢走 game_profile /
    bundle_map 缓存，导致本地明明有的动画（如 100199）被误判「不可播」。
    """
    here = Path(__file__).resolve().parent
    roots = [here]
    if getattr(sys, "frozen", False):
        meipass = getattr(sys, "_MEIPASS", None)
        if meipass:
            roots.append(Path(meipass))
            roots.append(Path(meipass) / "animator")
            roots.append(Path(meipass) / "tools")
        exe_dir = Path(sys.executable).resolve().parent
        roots.extend(
            [
                exe_dir / "animator",
                exe_dir / "tools",
                exe_dir,
            ]
        )
    seen: set[str] = set()
    # 先收集，再按「here 优先」写入：append 到 path 前端时倒序 insert
    ordered: list[Path] = []
    for root in roots:
        try:
            key = str(root.resolve())
        except OSError:
            continue
        if not root.is_dir() or key in seen:
            continue
        seen.add(key)
        ordered.append(root)
    for root in reversed(ordered):
        s = str(root)
        if s in sys.path:
            sys.path.remove(s)
        sys.path.insert(0, s)
    return here


TOOLS = _bootstrap_tool_paths()

from battle_appear_codec import encode_code  # noqa: E402
from game_profile import SEQCHAPTER  # noqa: E402
from pet_anim_manager import (  # noqa: E402
    bundle_path,
    load_anim_index,
    profile_is_monolithic,
    scan_bundle_map,
)
from pet_preview import (  # noqa: E402
    frame_interval_ms,
    get_battle_animation_frames,
)
from preview_rgba import make_checkerboard  # noqa: E402

def _data_file(name: str) -> Path:
    for base in (TOOLS, Path(getattr(sys, "_MEIPASS", TOOLS))):
        p = Path(base) / name
        if p.is_file():
            return p
        p2 = Path(base) / "animator" / name
        if p2.is_file():
            return p2
        p3 = Path(base) / "tools" / name
        if p3.is_file():
            return p3
    return TOOLS / name


APPEAR_BIN = _data_file("pet_appear.bin")
APPEAR_CSV = _data_file("pet_appear.csv")
APPEAR_JSON = _data_file("pet_appear.json")
RIDE_CSV = _data_file("ride_skin.csv")
RIDE_JSON = _data_file("ride_skin.json")
CREST_JSON = _data_file("pet_max_crest.json")
ROLE_HALO_JSON = _data_file("role_halo.json")


def _writable_cfg_dir() -> Path:
    """写出 battle_appear.json：优先游戏根 tools/，否则脚本旁。"""
    root = SEQCHAPTER.root
    if root.is_dir() and (root / "cg37.exe").is_file():
        d = root / "tools"
        d.mkdir(parents=True, exist_ok=True)
        return d
    if getattr(sys, "frozen", False):
        d = Path(sys.executable).resolve().parent / "animator_data"
        d.mkdir(parents=True, exist_ok=True)
        return d
    return TOOLS


BATTLE_CFG = _writable_cfg_dir() / "battle_appear.json"
LAST_CODE = _writable_cfg_dir() / "battle_appear_last_code.txt"
EXPORT_SCRIPT = TOOLS / "export_pet_appear_bin.py"
EXTRACT_SCRIPT = TOOLS / "extract_seqchapter_configs.py"
ROLE_HALO_SCRIPT = TOOLS / "_parse_role_halo.py"


def load_pet_appear() -> list[dict]:
    if APPEAR_JSON.is_file():
        return json.loads(APPEAR_JSON.read_text(encoding="utf-8"))
    if APPEAR_BIN.is_file():
        return parse_par1(APPEAR_BIN)
    raise FileNotFoundError("请先运行 export_pet_appear_bin.py 生成 pet_appear.*")


def parse_par1(path: Path) -> list[dict]:
    data = path.read_bytes()
    if data[:4] != b"PAR1":
        raise ValueError("not PAR1")
    (count,) = struct.unpack_from("<i", data, 4)
    pos = 8
    rows: list[dict] = []
    for _ in range(count):
        (nlen,) = struct.unpack_from("<H", data, pos)
        pos += 2
        name = data[pos : pos + nlen].decode("utf-8")
        pos += nlen
        temp, album, tribe, anim, perfect = struct.unpack_from("<iihii", data, pos)
        pos += 18
        rows.append(
            {
                "name": name,
                "aliases": [name],
                "temp_no": temp,
                "album_no": album,
                "tribe": tribe,
                "anim_id": anim,
                "perfect_skin_id": perfect,
            }
        )
    return rows


def load_rides() -> list[dict]:
    if RIDE_JSON.is_file():
        return json.loads(RIDE_JSON.read_text(encoding="utf-8"))
    if not RIDE_CSV.is_file():
        return []
    import csv

    with RIDE_CSV.open(encoding="utf-8-sig", newline="") as f:
        return list(csv.DictReader(f))


def load_crests() -> list[dict]:
    if CREST_JSON.is_file():
        return json.loads(CREST_JSON.read_text(encoding="utf-8"))
    return []


def load_role_halos() -> list[dict]:
    if ROLE_HALO_JSON.is_file():
        return json.loads(ROLE_HALO_JSON.read_text(encoding="utf-8"))
    return []


def default_battle_cfg() -> dict:
    slots = [
        {
            "slot": i,
            "pet_anim": 0,
            "role_halo": 0,
            "perfect": 0,
            "max_crest": 0,
            "char_anim": 0,
            "ride_skin": 0,
        }
        for i in range(1, 6)
    ]
    return {"enabled": True, "slots": slots}


class AppearPreviewGui(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("序章 · 动画预览（游戏外）")
        self.geometry("980x700")
        self.minsize(880, 600)
        self.profile = SEQCHAPTER
        self.anims: list[dict] = []  # 可播 AnimationId 列表（有名+无名）
        self.anim_filtered: list[dict] = []
        self.crests: list[dict] = []
        self.role_halos: list[dict] = []
        self._photo = None
        self._anim_frames: list = []
        self._anim_idx = 0
        self._anim_after = None
        self._preview_still = None  # 当前静帧源图（缩放用）
        self._preview_anim_id = 0
        self._preview_copy_value = ""  # 当前可复制的 AnimationId
        self.preview_zoom = tk.DoubleVar(value=1.0)
        self._bundle_map: dict[int, str] = {}
        self._animdata_ids: set[int] = set()
        self._table_total = 0
        self._anims_named = 0
        self._anims_extra = 0
        self._appear_by_anim: dict[int, dict] = {}
        self._ride_by_id: dict[int, dict] = {}  # 坐骑配置 Id → 行（≠动画 Id）
        self._ride_by_grano: dict[int, dict] = {}
        self._ride_rows: list[dict] = []
        self._ride_combo_to_value: dict[str, int] = {"0": 0}
        self._ride_value_to_label: dict[int, str] = {0: "0"}
        self._crest_ids: set[int] = set()
        self._role_halo_by_id: dict[int, dict] = {}
        self._role_halo_by_grano: dict[int, dict] = {}
        self._editing_slot = 1
        self._loading_form = False
        self._pet_anim_check_after = None
        self.battle_cfg = default_battle_cfg()
        self._build()
        self.protocol("WM_DELETE_WINDOW", self._on_close)
        self.after(50, self._bootstrap)

    def _build(self) -> None:
        top = ttk.Frame(self)
        top.pack(fill="x", padx=8, pady=6)
        ttk.Button(top, text="重新导出形象表", command=self._reexport).pack(side="left", padx=4)
        ttk.Button(top, text="刷新列表", command=self._reload_tables).pack(side="left", padx=4)
        self.status = ttk.Label(top, text="就绪")
        self.status.pack(side="left", padx=12)

        body = ttk.Panedwindow(self, orient="horizontal")
        body.pack(fill="both", expand=True, padx=8, pady=4)

        left = ttk.Notebook(body)
        right = ttk.Frame(body)
        body.add(left, weight=3)
        body.add(right, weight=2)

        self.tab_anim = ttk.Frame(left)
        self.tab_hook = ttk.Frame(left)
        left.add(self.tab_anim, text="动画预览")
        left.add(self.tab_hook, text="生成代码")

        self._build_anim_tab()
        self._build_hook_tab()
        self._build_preview(right)

    def _build_anim_tab(self) -> None:
        tip = ttk.Label(
            self.tab_anim,
            text=(
                "人物 / 宠物 / 坐骑外形预览都是同一套 AnimationId（animdata）。\n"
                "复制的 ID 可填到「生成代码」的 宠物形象 或 人物形象；\n"
                "坐骑字段 ride_skin：下拉选配置 Id（含骑宠皮如奥术飞毯）；旧码 Grano 仍可识别。"
            ),
            justify="left",
            foreground="#444",
        )
        tip.pack(anchor="w", padx=6, pady=4)
        bar = ttk.Frame(self.tab_anim)
        bar.pack(fill="x", padx=6, pady=2)
        ttk.Label(bar, text="搜索").pack(side="left")
        self.anim_q = tk.StringVar()
        ent = ttk.Entry(bar, textvariable=self.anim_q)
        ent.pack(side="left", fill="x", expand=True, padx=6)
        ent.bind("<KeyRelease>", lambda _e: self._filter_anims())
        ttk.Button(bar, text="复制动画ID", command=self._copy_selected_anim).pack(side="left", padx=4)
        self.anim_list = self._make_scroll_listbox(self.tab_anim)
        self.anim_list.bind("<<ListboxSelect>>", self._on_anim_select)
        self.anim_list.bind("<Double-Button-1>", lambda _e: self._copy_selected_anim())

        row = ttk.Frame(self.tab_anim)
        row.pack(fill="x", padx=6, pady=4)
        ttk.Label(row, text="动画 ID").pack(side="left")
        self.anim_id_var = tk.StringVar(value="0")
        self.anim_entry = ttk.Entry(row, textvariable=self.anim_id_var, width=12)
        self.anim_entry.pack(side="left", padx=6)
        self.anim_entry.bind("<Double-Button-1>", lambda _e: self._copy_selected_anim())
        self.anim_entry.bind("<Return>", lambda _e: self._preview_anim_id_input())
        ttk.Button(row, text="预览", command=self._preview_anim_id_input).pack(side="left", padx=2)

    def _make_scroll_listbox(self, parent: ttk.Frame) -> tk.Listbox:
        """左侧长列表：Listbox + 纵向滚动条 + 滚轮。"""
        wrap = ttk.Frame(parent)
        wrap.pack(fill="both", expand=True, padx=6, pady=4)
        sb = ttk.Scrollbar(wrap, orient="vertical")
        lb = tk.Listbox(wrap, exportselection=False, yscrollcommand=sb.set)
        sb.configure(command=lb.yview)
        sb.pack(side="right", fill="y")
        lb.pack(side="left", fill="both", expand=True)

        def _on_wheel(event: tk.Event) -> str:
            # Windows: delta 为 ±120 的倍数
            steps = int(-event.delta / 120) if event.delta else 0
            if steps:
                lb.yview_scroll(steps, "units")
            return "break"

        lb.bind("<MouseWheel>", _on_wheel)
        wrap.bind("<MouseWheel>", _on_wheel)
        return lb

    def _build_hook_tab(self) -> None:
        tip = ttk.Label(
            self.tab_hook,
            text=(
                "手动填槽位 →「生成并复制」（自动保存配置与代码；「清空全部」才清除）。\n"
                "宠物/人物填动画 ID；坐骑用下拉。游戏内粘贴导入也会保存并打开钩子。"
            ),
            justify="left",
            foreground="#444",
        )
        tip.pack(anchor="w", padx=8, pady=4)

        top = ttk.Frame(self.tab_hook)
        top.pack(fill="x", padx=8, pady=2)
        ttk.Button(top, text="生成并复制", command=self._gen_copy_code).pack(side="left", padx=2)
        ttk.Button(top, text="清空全部", command=self._clear_all_slots).pack(side="left", padx=6)

        self.hook_slot = tk.IntVar(value=1)
        slot_bar = ttk.Frame(self.tab_hook)
        slot_bar.pack(fill="x", padx=8, pady=4)
        ttk.Label(slot_bar, text="编辑槽位").pack(side="left")
        for i in range(1, 6):
            ttk.Radiobutton(
                slot_bar, text=str(i), value=i, variable=self.hook_slot, command=self._switch_slot
            ).pack(side="left", padx=2)

        form = ttk.Frame(self.tab_hook)
        form.pack(fill="x", padx=8, pady=2)
        self.slot_vars: dict[str, tk.StringVar] = {}

        # 行1：宠物形象 + 满档
        r0 = ttk.Frame(form)
        r0.grid(row=0, column=0, columnspan=2, sticky="w", padx=4, pady=2)
        ttk.Label(r0, text="宠物形象", width=12).pack(side="left")
        self.slot_vars["pet_anim"] = tk.StringVar(value="0")
        pet_ent = ttk.Entry(r0, textvariable=self.slot_vars["pet_anim"], width=12)
        pet_ent.pack(side="left")
        pet_ent.bind("<FocusOut>", lambda _e: self._on_pet_anim_changed())
        pet_ent.bind("<Return>", lambda _e: self._on_pet_anim_changed())
        self.slot_vars["pet_anim"].trace_add("write", self._schedule_pet_anim_check)
        self.perfect_var = tk.BooleanVar(value=False)
        self.perfect_chk = ttk.Checkbutton(r0, text="满档", variable=self.perfect_var)
        self.perfect_chk.pack(side="left", padx=10)
        self.perfect_hint = ttk.Label(r0, text="", foreground="#888")
        self.perfect_hint.pack(side="left")

        # 行2：满档光环 / 人物光环
        r1 = ttk.Frame(form)
        r1.grid(row=1, column=0, columnspan=2, sticky="w", padx=4, pady=2)
        ttk.Label(r1, text="满档光环", width=12).pack(side="left")
        self.crest_var = tk.StringVar(value="0")
        self.crest_combo = ttk.Combobox(
            r1, textvariable=self.crest_var, width=18, state="readonly", values=["0"]
        )
        self.crest_combo.pack(side="left")
        ttk.Label(r1, text="人物光环", width=10).pack(side="left", padx=(12, 0))
        self.role_halo_var = tk.StringVar(value="0")
        self.role_halo_combo = ttk.Combobox(
            r1, textvariable=self.role_halo_var, width=18, state="readonly", values=["0"]
        )
        self.role_halo_combo.pack(side="left")

        # 行3：人物 / 坐骑
        r2 = ttk.Frame(form)
        r2.grid(row=2, column=0, columnspan=2, sticky="w", padx=4, pady=2)
        ttk.Label(r2, text="人物形象", width=12).pack(side="left")
        self.slot_vars["char_anim"] = tk.StringVar(value="0")
        ttk.Entry(r2, textvariable=self.slot_vars["char_anim"], width=12).pack(side="left")
        ttk.Label(r2, text="坐骑", width=8).pack(side="left", padx=(12, 0))
        self.ride_skin_var = tk.StringVar(value="0")
        self.ride_skin_combo = ttk.Combobox(
            r2, textvariable=self.ride_skin_var, width=22, state="readonly", values=["0"]
        )
        self.ride_skin_combo.pack(side="left")
        ttk.Label(
            form,
            text="坐骑：无后缀=人物坐骑表；带「骑宠皮」=奥术飞毯/克洛丝晶王等（钩子注入后进战生效）",
            foreground="#888",
        ).grid(row=3, column=0, columnspan=2, sticky="w", padx=4, pady=(0, 4))

        self.code_box = scrolledtext.ScrolledText(self.tab_hook, height=12, wrap="word")
        self.code_box.pack(fill="both", expand=True, padx=8, pady=4)

    def _build_preview(self, parent: ttk.Frame) -> None:
        ttk.Label(parent, text="预览", font=("", 11, "bold")).pack(anchor="w", padx=4, pady=4)
        self.preview_meta = ttk.Label(parent, text="—", justify="left")
        self.preview_meta.pack(anchor="w", padx=4)
        self.canvas = tk.Label(parent, background="#2b2b2b", width=40, height=20)
        self.canvas.pack(fill="both", expand=True, padx=4, pady=4)
        self.canvas.bind("<Double-Button-1>", lambda _e: self._copy_preview_id())
        zoom_row = ttk.Frame(parent)
        zoom_row.pack(fill="x", padx=4, pady=2)
        ttk.Label(zoom_row, text="缩放").pack(side="left")
        self.zoom_value_lbl = ttk.Label(zoom_row, text="100%", width=5)
        self.zoom_value_lbl.pack(side="right")
        self.zoom_scale = ttk.Scale(
            zoom_row,
            from_=0.5,
            to=3.0,
            variable=self.preview_zoom,
            orient=tk.HORIZONTAL,
            command=self._on_preview_zoom,
        )
        self.zoom_scale.pack(side="left", fill="x", expand=True, padx=6)
        row = ttk.Frame(parent)
        row.pack(fill="x", padx=4, pady=4)
        ttk.Button(row, text="静帧", command=self._show_static).pack(side="left", padx=2)
        ttk.Button(row, text="动画", command=self._show_anim).pack(side="left", padx=2)
        self.btn_copy_preview = ttk.Button(row, text="复制ID", command=self._copy_preview_id)
        self.btn_copy_preview.pack(side="left", padx=8)
        ttk.Label(row, text="双击预览图也可复制", foreground="#666").pack(side="left")

    def _bootstrap(self) -> None:
        try:
            self._reload_tables()
            self._load_battle_cfg()
        except Exception as exc:
            self.status.config(text=f"加载失败: {exc}")

    def _reexport(self) -> None:
        def work() -> None:
            import subprocess

            self.status.config(text="从 crosscopy 提取并导出…")
            # 先从干净目录提取配置到本 tools/_config_extract（不写 crosscopy）
            ex = subprocess.run(
                [sys.executable, str(EXTRACT_SCRIPT)],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
            )
            if ex.returncode != 0:
                msg = (ex.stdout or "") + (ex.stderr or "")
                self.after(0, lambda: self._after_export(ex.returncode, msg))
                return
            proc = subprocess.run(
                [sys.executable, str(EXPORT_SCRIPT)],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
            )
            halo_out = ""
            halo_code = 0
            if ROLE_HALO_SCRIPT.is_file():
                halo = subprocess.run(
                    [sys.executable, str(ROLE_HALO_SCRIPT)],
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                )
                halo_code = halo.returncode
                halo_out = (halo.stdout or "") + (halo.stderr or "")
            code = proc.returncode or halo_code
            msg = (
                (ex.stdout or "")
                + "\n"
                + (proc.stdout or "")
                + (proc.stderr or "")
                + ("\n" + halo_out if halo_out else "")
            )
            self.after(0, lambda: self._after_export(code, msg))

        threading.Thread(target=work, daemon=True).start()

    def _after_export(self, code: int, msg: str) -> None:
        if code != 0:
            messagebox.showerror("导出失败", msg[-800:] or "unknown")
            self.status.config(text="导出失败")
            return
        self._reload_tables()
        self.status.config(text="导出完成")

    def _load_animdata_ids(self) -> set[int]:
        """monolithic：从 animdatainfo；bundle_only：有包即视为有（包内再取）。"""
        if profile_is_monolithic(self.profile):
            info = self.profile.animdata_info.read_bytes()
            return {aid for aid, _off in load_anim_index(info)}
        return {aid for aid in self._bundle_map if aid > 0}

    def _has_anim_bundle(self, anim_id: int) -> bool:
        """本地是否有该形象动画包（不保证 animdata）。"""
        try:
            aid = int(anim_id)
        except (TypeError, ValueError):
            return False
        if aid <= 0:
            return False
        return bundle_path(aid, self._bundle_map, self.profile) is not None

    def _has_playable_anim(self, anim_id: int) -> bool:
        """animdata 有条目 + 有动画包（序章预览必需；仅有 bundle 名数字不够）。"""
        try:
            aid = int(anim_id)
        except (TypeError, ValueError):
            return False
        if aid <= 0:
            return False
        if aid not in self._animdata_ids:
            return False
        return self._has_anim_bundle(aid)

    def _reload_tables(self) -> None:
        self.status.config(text="扫描动画包索引…")
        self.update_idletasks()
        self._bundle_map = scan_bundle_map(self.profile, force=False)
        self._animdata_ids = self._load_animdata_ids()

        raw_pets = load_pet_appear()
        raw_rides = load_rides()
        self._table_total = len(raw_pets)
        named = [p for p in raw_pets if self._has_playable_anim(p.get("anim_id", 0))]
        named_anim_ids = {int(p["anim_id"]) for p in named}
        self._appear_by_anim = {
            int(p["anim_id"]): p for p in raw_pets if int(p.get("anim_id") or 0) > 0
        }
        extras: list[dict] = []
        for aid in sorted(self._animdata_ids):
            if aid in named_anim_ids:
                continue
            if not self._has_playable_anim(aid):
                continue
            extras.append(
                {
                    "name": f"未知#{aid}",
                    "aliases": [f"未知#{aid}", str(aid), "无名"],
                    "temp_no": 0,
                    "album_no": 0,
                    "tribe": 0,
                    "anim_id": aid,
                    "perfect_skin_id": 0,
                    "can_perfect": False,
                    "unnamed": True,
                }
            )
            self._appear_by_anim.setdefault(aid, extras[-1])
        self.anims = named + extras
        self._anims_named = len(named)
        self._anims_extra = len(extras)
        self.crests = load_crests()
        self._crest_ids = {int(c.get("id") or 0) for c in self.crests if int(c.get("id") or 0) > 0}
        self.role_halos = load_role_halos()
        self._role_halo_by_id = {
            int(h.get("id") or 0): h
            for h in self.role_halos
            if int(h.get("id") or 0) > 0 and int(h.get("grano") or 0) > 0
        }
        self._role_halo_by_grano = {
            int(h.get("grano") or 0): h
            for h in self.role_halos
            if int(h.get("grano") or 0) > 0
        }
        # 坐骑：人物表(other_tbride*) + 骑宠皮(pet_tbride*)。
        # 游戏原生只认人物表 Id；骑宠皮靠钩子注入别名。Id 与人物表冲突时写入 Grano。
        self._ride_rows = []
        self._ride_by_id: dict[int, dict] = {}
        self._ride_by_grano: dict[int, dict] = {}
        self._ride_combo_to_value: dict[str, int] = {"0": 0}
        self._ride_value_to_label: dict[int, str] = {0: "0"}
        char_rows = [
            r
            for r in raw_rides
            if r.get("kind", "char") == "char"
            and int(r.get("id") or 0) > 0
            and int(r.get("grano") or 0) > 0
        ]
        pet_rows = [
            r
            for r in raw_rides
            if r.get("kind") == "pet_skin"
            and int(r.get("id") or 0) > 0
            and int(r.get("grano") or 0) > 0
        ]
        char_ids = {int(r.get("id") or 0) for r in char_rows}
        for r in char_rows:
            rid = int(r.get("id") or 0)
            grano = int(r.get("grano") or 0)
            name = str(r.get("name") or "").strip()
            label = f"{rid}.{name}" if name else str(rid)
            self._ride_by_id[rid] = r
            self._ride_by_grano[grano] = r
            self._ride_rows.append(r)
            self._ride_combo_to_value[label] = rid
            self._ride_value_to_label[rid] = label
            self._ride_value_to_label[grano] = label
        for r in pet_rows:
            rid = int(r.get("id") or 0)
            grano = int(r.get("grano") or 0)
            name = str(r.get("name") or "").strip()
            # 与人物坐骑 Id 冲突时改存 Grano，避免写成地狱妖犬等错误坐骑
            write = grano if rid in char_ids else rid
            label = f"{rid}.{name}（骑宠皮）" if name else f"{rid}（骑宠皮）"
            # 不覆盖人物表同 Id；骑宠皮靠 grano / combo 映射识别
            if rid not in self._ride_by_id:
                self._ride_by_id[rid] = r
            self._ride_by_grano[grano] = r
            self._ride_rows.append(r)
            self._ride_combo_to_value[label] = write
            self._ride_value_to_label[write] = label
            if rid not in char_ids:
                self._ride_value_to_label[rid] = label
            self._ride_value_to_label[grano] = label
        crest_vals = ["0"] + [
            f"{int(c.get('id'))}.{c.get('name') or ''}".rstrip(".")
            for c in self.crests
            if int(c.get("id") or 0) > 0
        ]
        halo_vals = ["0"] + [
            f"{int(h.get('id'))}.{h.get('name') or ''}".rstrip(".")
            for h in self.role_halos
            if int(h.get("id") or 0) > 0 and int(h.get("grano") or 0) > 0
        ]
        ride_vals = ["0"] + [
            lab
            for lab in self._ride_combo_to_value
            if lab != "0"
        ]
        # 人物坐骑在前，骑宠皮在后（combo dict 已按插入序；Py3.7+）
        try:
            self.crest_combo.configure(values=crest_vals)
        except Exception:
            pass
        try:
            self.role_halo_combo.configure(values=halo_vals)
        except Exception:
            pass
        try:
            self.ride_skin_combo.configure(values=ride_vals)
        except Exception:
            pass
        self._filter_anims()
        named_drop = self._table_total - self._anims_named
        n_char = len(char_rows)
        n_pet = len(pet_rows)
        self.status.config(
            text=(
                f"动画 {len(self.anims)}（有名{self._anims_named}/表{self._table_total}"
                f" 无播{named_drop} +无名{self._anims_extra}）· "
                f"坐骑 人物{n_char}+骑宠皮{n_pet}· "
                f"{self.profile.label}"
            )
        )

    def _filter_anims(self) -> None:
        q = (self.anim_q.get() or "").strip().lower()
        self.anim_filtered = []
        self.anim_list.delete(0, "end")
        for p in self.anims:
            aliases = p.get("aliases") or [p.get("name", "")]
            blob = (
                f"{p.get('name','')} {' '.join(map(str, aliases))} "
                f"{p.get('anim_id','')} {p.get('temp_no','')}"
            ).lower()
            if q and q not in blob:
                continue
            self.anim_filtered.append(p)
            tags = []
            if p.get("unnamed"):
                tags.append("无名")
            if p.get("can_perfect"):
                tags.append("可满档")
            skin = int(p.get("perfect_skin_id") or 0)
            if skin:
                tags.append(f"换皮→{skin}")
            extra = ("  " + " ".join(tags)) if tags else ""
            if p.get("unnamed"):
                label = f"[无名] anim={p.get('anim_id')}{extra}"
            else:
                label = (
                    f"{p.get('name')}  anim={p.get('anim_id')}  "
                    f"temp={p.get('temp_no')}{extra}"
                )
            self.anim_list.insert("end", label)

    def _clipboard_set(self, text: str, tip: str) -> None:
        text = str(text).strip()
        if not text:
            messagebox.showinfo("提示", "没有可复制的内容")
            return
        try:
            self.clipboard_clear()
            self.clipboard_append(text)
            self.update()
        except tk.TclError:
            messagebox.showwarning("剪贴板", "无法写入剪贴板")
            return
        self.status.config(text=tip)

    def _copy_selected_anim(self) -> None:
        sel = self.anim_list.curselection()
        if sel:
            anim = str(int(self.anim_filtered[sel[0]]["anim_id"]))
            self.anim_id_var.set(anim)
            self._on_anim_select()
            self._clipboard_set(anim, f"已复制动画ID {anim}（pet_anim / char_anim）")
            return
        raw = (self.anim_id_var.get() or "").strip()
        try:
            anim = str(int(raw))
        except ValueError:
            messagebox.showwarning("提示", "请先选中列表项，或输入动画 ID")
            return
        if int(anim) <= 0:
            messagebox.showinfo("提示", "请先选中列表项，或输入有效动画 ID")
            return
        self._preview_anim_id_input()
        self._clipboard_set(anim, f"已复制动画ID {anim}（pet_anim / char_anim）")

    def _copy_preview_id(self) -> None:
        if not self._preview_copy_value:
            messagebox.showinfo("提示", "请先在左侧选中或预览一个动画")
            return
        self._clipboard_set(
            self._preview_copy_value,
            f"已复制动画ID {self._preview_copy_value}（pet_anim / char_anim）",
        )

    def _on_anim_select(self, _e=None) -> None:
        sel = self.anim_list.curselection()
        if not sel:
            return
        p = self.anim_filtered[sel[0]]
        anim = int(p["anim_id"])
        self.anim_id_var.set(str(anim))
        self._preview_anim_id = anim
        self._preview_copy_value = str(anim)
        try:
            self.btn_copy_preview.config(text="复制动画ID")
        except Exception:
            pass
        aliases = "|".join(p.get("aliases") or [])
        can = "是" if p.get("can_perfect") else "否"
        mat = p.get("perfect_mat") or "—"
        skin = int(p.get("perfect_skin_id") or 0)
        skin_s = str(skin) if skin else "无（同形象+材质）"
        name = p.get("name") or f"#{anim}"
        self.preview_meta.config(
            text=(
                f"动画 {name}\n"
                f"AnimationId={anim}  temp={p.get('temp_no')}  album={p.get('album_no')}\n"
                f"可满档={can}  材质={mat}  换皮Skin={skin_s}\n"
                f"别名: {aliases}\n"
                f"（可填 pet_anim / char_anim；坐骑 ride_skin 用配置Id）"
            )
        )
        self._show_static()

    def _preview_anim_id_input(self) -> None:
        try:
            anim = int(self.anim_id_var.get().strip())
        except ValueError:
            messagebox.showwarning("提示", "动画 ID 无效")
            return
        if anim <= 0:
            messagebox.showinfo("提示", "请输入 >0 的动画 ID")
            return
        self._preview_anim_id = anim
        self._preview_copy_value = str(anim)
        try:
            self.btn_copy_preview.config(text="复制动画ID")
        except Exception:
            pass
        p = self._appear_by_anim.get(anim)
        name = (p.get("name") if p else None) or f"#{anim}"
        self.preview_meta.config(
            text=(
                f"动画 {name}（手动）\n"
                f"AnimationId={anim}\n"
                f"（可填 pet_anim / char_anim）"
            )
        )
        self._show_static()

    def _stop_anim(self) -> None:
        if self._anim_after is not None:
            try:
                self.after_cancel(self._anim_after)
            except Exception:
                pass
            self._anim_after = None

    def _on_preview_zoom(self, _value=None) -> None:
        try:
            z = float(self.preview_zoom.get())
        except (TypeError, ValueError):
            z = 1.0
        self.zoom_value_lbl.config(text=f"{int(round(z * 100))}%")
        # 动画播放中由 _tick_anim 跟缩放；静帧立即重绘
        if self._anim_after is not None:
            return
        if self._preview_still is not None:
            self._paint_frame(self._preview_still)
        elif self._anim_frames:
            idx = self._anim_idx % len(self._anim_frames)
            self._paint_frame(self._anim_frames[idx])

    def _paint_frame(self, img: Image.Image) -> None:
        """按缩放滑动条绘制一帧（宠物/坐骑/人物共用）。"""
        try:
            z = float(self.preview_zoom.get())
        except (TypeError, ValueError):
            z = 1.0
        z = max(0.5, min(3.0, z))
        src = img.convert("RGBA")
        w, h = src.size
        max_inner = max(32, int(340 * z))
        scale = min(max_inner / max(w, 1), max_inner / max(h, 1))
        nw = max(1, int(w * scale))
        nh = max(1, int(h * scale))
        scaled = (
            src.resize((nw, nh), Image.Resampling.NEAREST)
            if (nw, nh) != (w, h)
            else src
        )
        cw = ch = 360
        if nw > cw or nh > ch:
            left = max(0, (nw - cw) // 2)
            top = max(0, (nh - ch) // 2)
            scaled = scaled.crop((left, top, left + min(cw, nw), top + min(ch, nh)))
            nw, nh = scaled.size
        canvas = make_checkerboard(cw, ch).convert("RGBA")
        overlay = Image.new("RGBA", (cw, ch), (0, 0, 0, 0))
        overlay.paste(scaled, ((cw - nw) // 2, (ch - nh) // 2), scaled)
        disp = Image.alpha_composite(canvas, overlay).convert("RGB")
        photo = ImageTk.PhotoImage(disp)
        self._photo = photo
        self.canvas.config(image=photo, text="", foreground="#eee")

    def _show_static(self) -> None:
        """静帧 = 站立动作第一帧（不是整张图集）。"""
        self._stop_anim()
        anim = self._preview_anim_id
        if anim <= 0:
            return
        try:
            frames, _clip, _meta = get_battle_animation_frames(anim, self.profile)
            if not frames:
                raise RuntimeError("无动画帧")
            self._anim_frames = []
            self._preview_still = frames[0]
            self._paint_frame(self._preview_still)
        except Exception as exc:
            self._preview_still = None
            self.canvas.config(image="", text=f"预览失败\n{exc}", foreground="#f88")

    def _show_anim(self) -> None:
        self._stop_anim()
        anim = self._preview_anim_id
        if anim <= 0:
            return
        try:
            frames, clip, _meta = get_battle_animation_frames(anim, self.profile)
            if not frames:
                raise RuntimeError("无动画帧")
            self._preview_still = None
            self._anim_frames = frames
            self._anim_clip = clip
            self._anim_idx = 0
            self._tick_anim()
        except Exception as exc:
            self.canvas.config(image="", text=f"动画失败\n{exc}", foreground="#f88")

    def _tick_anim(self) -> None:
        if not self._anim_frames:
            return
        img = self._anim_frames[self._anim_idx % len(self._anim_frames)]
        self._paint_frame(img)
        self._anim_idx += 1
        delay = 80
        try:
            delay = frame_interval_ms(self._anim_clip) if getattr(self, "_anim_clip", None) else 80
        except Exception:
            delay = 80
        self._anim_after = self.after(max(40, int(delay)), self._tick_anim)

    def _load_battle_cfg(self) -> None:
        if BATTLE_CFG.is_file():
            self.battle_cfg = json.loads(BATTLE_CFG.read_text(encoding="utf-8"))
        else:
            self.battle_cfg = default_battle_cfg()
        self._editing_slot = int(self.hook_slot.get() or 1)
        self._fill_slot_form()
        # 恢复上次生成的 CGAP1 代码（清空全部会删掉此文件）
        if LAST_CODE.is_file():
            try:
                text = LAST_CODE.read_text(encoding="utf-8")
                self.code_box.delete("1.0", "end")
                if text.strip():
                    self.code_box.insert("end", text)
            except OSError:
                pass
        self.status.config(text=f"已载入 {BATTLE_CFG.name}" + (" + 上次代码" if LAST_CODE.is_file() else ""))

    def _persist_battle_cfg(self) -> None:
        """自动保存当前槽位配置到 battle_appear.json。"""
        try:
            self._collect_slot_form_to(self._editing_slot)
            self._ensure_five_slots()
            BATTLE_CFG.write_text(
                json.dumps(self.battle_cfg, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
        except OSError as exc:
            self.status.config(text=f"自动保存失败: {exc}")

    def _persist_last_code(self, code: str) -> None:
        try:
            LAST_CODE.write_text(code, encoding="utf-8")
        except OSError as exc:
            self.status.config(text=f"代码保存失败: {exc}")

    def _on_close(self) -> None:
        try:
            self._persist_battle_cfg()
        except Exception:
            pass
        self.destroy()

    def _slot_dict(self, slot_no: int) -> dict:
        slots = self.battle_cfg.setdefault("slots", [])
        for s in slots:
            if int(s.get("slot", 0)) == slot_no:
                return s
        d = dict(default_battle_cfg()["slots"][slot_no - 1])
        slots.append(d)
        return d

    def _switch_slot(self) -> None:
        """切换槽位前先把当前表单写回旧槽，避免清空/串档。"""
        self._collect_slot_form_to(self._editing_slot)
        self._persist_battle_cfg()
        self._editing_slot = int(self.hook_slot.get())
        self._fill_slot_form()

    def _fill_slot_form(self) -> None:
        d = self._slot_dict(self._editing_slot)
        self._loading_form = True
        try:
            for key, var in self.slot_vars.items():
                var.set(str(int(d.get(key, 0) or 0)))
            crest = int(d.get("max_crest", 0) or 0)
            if crest > 0 and crest not in self._crest_ids:
                vals = list(self.crest_combo.cget("values") or ("0",))
                extra = str(crest)
                if extra not in vals:
                    self.crest_combo.configure(values=list(vals) + [extra])
            self.crest_var.set(self._crest_label(crest))
            role_halo = int(d.get("role_halo", d.get("pet_halo", 0)) or 0)
            self.role_halo_var.set(self._role_halo_label(role_halo))
            ride = int(d.get("ride_skin", 0) or 0)
            if ride > 0 and ride not in self._ride_by_id and ride not in self._ride_by_grano:
                vals = list(self.ride_skin_combo.cget("values") or ("0",))
                extra = str(ride)
                if extra not in vals:
                    self.ride_skin_combo.configure(values=list(vals) + [extra])
            self.ride_skin_var.set(self._ride_skin_label(ride))
            perfect = int(d.get("perfect", 0) or 0)
            self.perfect_var.set(perfect == 1)
        finally:
            self._loading_form = False
        self._refresh_perfect_state(force_off_if_invalid=True)

    def _parse_int_var(self, var: tk.StringVar, default: int = 0) -> int:
        raw = (var.get() or "").strip()
        try:
            return int(raw)
        except ValueError:
            return default

    def _parse_combo_id(self, raw: str, default: int = 0) -> int:
        """下拉值形如 '0' 或 '1.炫光律动' → 取前导整数。"""
        text = (raw or "").strip()
        if not text:
            return default
        m = re.match(r"(\d+)", text)
        if not m:
            return default
        try:
            return int(m.group(1))
        except ValueError:
            return default

    def _crest_label(self, crest_id: int) -> str:
        if crest_id <= 0:
            return "0"
        for c in self.crests:
            if int(c.get("id") or 0) == crest_id:
                name = str(c.get("name") or "").strip()
                return f"{crest_id}.{name}" if name else str(crest_id)
        return str(crest_id)

    def _crest_name(self, crest_id: int) -> str:
        if crest_id <= 0:
            return ""
        for c in self.crests:
            if int(c.get("id") or 0) == crest_id:
                return str(c.get("name") or "").strip()
        return ""

    def _role_halo_label(self, grano: int) -> str:
        """表单显示用：按 Grano 反查 Id.Name；未知则显示 Grano 数字。"""
        if grano <= 0:
            return "0"
        h = self._role_halo_by_grano.get(grano)
        if h:
            hid = int(h.get("id") or 0)
            name = str(h.get("name") or "").strip()
            return f"{hid}.{name}" if name else str(hid or grano)
        return str(grano)

    def _role_halo_grano_from_combo(self) -> int:
        """下拉 'Id.名称' → Grano；未知纯数字当作 Grano。"""
        raw = (self.role_halo_var.get() or "").strip()
        if not raw or raw == "0":
            return 0
        hid = self._parse_combo_id(raw, 0)
        if hid > 0 and hid in self._role_halo_by_id:
            return int(self._role_halo_by_id[hid].get("grano") or 0)
        # 兼容直接填 Grano
        try:
            g = int(raw)
            return g if g > 0 else 0
        except ValueError:
            return 0

    def _role_halo_name(self, grano: int) -> str:
        if grano <= 0:
            return ""
        h = self._role_halo_by_grano.get(grano)
        return str((h or {}).get("name") or "").strip()

    def _pet_name(self, anim_id: int) -> str:
        if anim_id <= 0:
            return ""
        p = self._appear_by_anim.get(anim_id)
        return str((p or {}).get("name") or "").strip()

    def _ride_name(self, ride_value: int) -> str:
        if ride_value <= 0:
            return ""
        r = self._ride_by_id.get(ride_value) or self._ride_by_grano.get(ride_value)
        name = str((r or {}).get("name") or "").strip()
        if name in ("0", "0.0"):
            return ""
        return name

    def _ride_skin_label(self, value: int) -> str:
        """表单显示：配置 Id / 骑宠皮 Id / Grano → 下拉文案。"""
        if value <= 0:
            return "0"
        lab = getattr(self, "_ride_value_to_label", {}).get(value)
        if lab:
            return lab
        r = self._ride_by_id.get(value) or self._ride_by_grano.get(value)
        if r:
            rid = int(r.get("id") or 0)
            name = str(r.get("name") or "").strip()
            if r.get("kind") == "pet_skin":
                return f"{rid}.{name}（骑宠皮）" if name else f"{rid}（骑宠皮）"
            return f"{rid}.{name}" if name else str(rid or value)
        return str(value)

    def _ride_skin_id_from_combo(self) -> int:
        """下拉 → 写入值：人物坐骑=配置 Id；骑宠皮=Id（冲突则 Grano）。"""
        raw = (self.ride_skin_var.get() or "").strip()
        if not raw or raw == "0":
            return 0
        mapped = getattr(self, "_ride_combo_to_value", {}).get(raw)
        if mapped is not None:
            return int(mapped)
        rid = self._parse_combo_id(raw, 0)
        if rid > 0 and rid in self._ride_by_id:
            r = self._ride_by_id[rid]
            if r.get("kind") == "pet_skin":
                # 与人物 Id 冲突的骑宠皮：存 Grano
                char_hit = any(
                    x.get("kind", "char") == "char" and int(x.get("id") or 0) == rid
                    for x in self._ride_rows
                )
                if char_hit:
                    return int(r.get("grano") or rid)
            return rid
        if rid > 0 and rid in self._ride_by_grano:
            r = self._ride_by_grano[rid]
            if r.get("kind") == "pet_skin":
                return int(r.get("grano") or rid)
            return int(r.get("id") or rid)
        try:
            n = int(raw)
            return n if n > 0 else 0
        except ValueError:
            return 0

    def _collect_slot_form_to(self, slot_no: int) -> None:
        d = self._slot_dict(slot_no)
        d["slot"] = slot_no
        d["pet_anim"] = self._parse_int_var(self.slot_vars["pet_anim"], 0)
        d["max_crest"] = max(0, self._parse_combo_id(self.crest_var.get(), 0))
        d["char_anim"] = self._parse_int_var(self.slot_vars["char_anim"], 0)
        d["ride_skin"] = max(0, self._ride_skin_id_from_combo())
        d["role_halo"] = max(0, self._role_halo_grano_from_combo())
        d.pop("pet_halo", None)
        can = self._can_perfect(d["pet_anim"])
        d["perfect"] = 1 if (self.perfect_var.get() and can) else 0

    def _can_perfect(self, anim_id: int) -> bool:
        if anim_id <= 0:
            return False
        p = self._appear_by_anim.get(anim_id)
        return bool(p and p.get("can_perfect"))

    def _schedule_pet_anim_check(self, *_args) -> None:
        if self._loading_form:
            return
        if self._pet_anim_check_after is not None:
            try:
                self.after_cancel(self._pet_anim_check_after)
            except Exception:
                pass
        self._pet_anim_check_after = self.after(150, self._on_pet_anim_changed)

    def _on_pet_anim_changed(self) -> None:
        if self._loading_form:
            return
        self._pet_anim_check_after = None
        self._refresh_perfect_state(force_off_if_invalid=True)

    def _refresh_perfect_state(self, force_off_if_invalid: bool = False) -> None:
        anim = self._parse_int_var(self.slot_vars["pet_anim"], 0)
        can = self._can_perfect(anim)
        if can:
            self.perfect_chk.configure(state="normal")
            self.perfect_hint.configure(text="可满档", foreground="#2a7")
        else:
            if force_off_if_invalid or anim <= 0:
                self.perfect_var.set(False)
            self.perfect_chk.configure(state="disabled")
            if anim <= 0:
                self.perfect_hint.configure(text="", foreground="#888")
            elif self._has_playable_anim(anim):
                self.perfect_hint.configure(text="不可满档→不配置", foreground="#a60")
            else:
                self.perfect_hint.configure(text="本地不可播", foreground="#a60")

    def _ensure_five_slots(self) -> None:
        by = {
            int(s.get("slot", 0)): s
            for s in self.battle_cfg.get("slots", [])
            if isinstance(s, dict)
        }
        slots = []
        for i in range(1, 6):
            base = by.get(i) or default_battle_cfg()["slots"][i - 1]
            # 旧配置 perfect=-1 视为 0
            p = int(base.get("perfect", 0) or 0)
            if p < 0:
                p = 0
            anim = int(base.get("pet_anim", 0) or 0)
            if p and not self._can_perfect(anim):
                p = 0
            slots.append(
                {
                    "slot": i,
                    "pet_anim": max(0, anim),
                    "role_halo": max(
                        0, int(base.get("role_halo", base.get("pet_halo", 0)) or 0)
                    ),
                    "perfect": 1 if p else 0,
                    "max_crest": max(0, int(base.get("max_crest", 0) or 0)),
                    "char_anim": max(0, int(base.get("char_anim", 0) or 0)),
                    "ride_skin": max(0, int(base.get("ride_skin", 0) or 0)),
                }
            )
        self.battle_cfg["slots"] = slots
        self.battle_cfg["enabled"] = True

    def _validate_slots(self) -> list[str]:
        errs: list[str] = []
        for s in self.battle_cfg.get("slots") or []:
            slot = int(s.get("slot", 0))
            anim = int(s.get("pet_anim", 0) or 0)
            perfect = int(s.get("perfect", 0) or 0)
            crest = int(s.get("max_crest", 0) or 0)
            role_halo = int(s.get("role_halo", 0) or 0)
            char_anim = int(s.get("char_anim", 0) or 0)
            ride = int(s.get("ride_skin", 0) or 0)
            if anim < 0:
                errs.append(f"槽{slot}: 宠物形象无效")
            elif anim > 0 and not self._has_playable_anim(anim):
                errs.append(f"槽{slot}: 宠物形象 {anim} 本地不可播（无 animdata/动画包）")
            if perfect not in (0, 1):
                errs.append(f"槽{slot}: 满档只能是 0/1")
            elif perfect == 1 and not self._can_perfect(anim):
                errs.append(f"槽{slot}: 形象 {anim} 不可满档")
            if crest < 0 or (crest > 0 and crest not in self._crest_ids):
                errs.append(f"槽{slot}: 满档光环 {crest} 无效")
            if role_halo < 0:
                errs.append(f"槽{slot}: 人物光环无效")
            elif role_halo > 0 and role_halo not in self._role_halo_by_grano:
                errs.append(f"槽{slot}: 人物光环 Grano {role_halo} 不在表内")
            if char_anim < 0:
                errs.append(f"槽{slot}: 人物形象无效")
            elif char_anim > 0 and not self._has_playable_anim(char_anim):
                errs.append(f"槽{slot}: 人物形象 {char_anim} 本地不可播")
            if ride < 0:
                errs.append(f"槽{slot}: 坐骑 Id 无效")
            elif ride > 0 and ride not in self._ride_by_id and ride not in self._ride_by_grano:
                errs.append(f"槽{slot}: 坐骑 {ride} 不在坐骑表内")
        return errs

    def _slot_comment_line(self, s: dict) -> str:
        slot = int(s.get("slot", 0))
        char_anim = int(s.get("char_anim", 0) or 0)
        role_halo = int(s.get("role_halo", 0) or 0)
        pet_anim = int(s.get("pet_anim", 0) or 0)
        perfect = int(s.get("perfect", 0) or 0)
        crest = int(s.get("max_crest", 0) or 0)
        ride = int(s.get("ride_skin", 0) or 0)

        if char_anim > 0:
            char_part = f"人物使用{char_anim}"
        else:
            char_part = "人物不配置"

        if role_halo > 0:
            h = self._role_halo_by_grano.get(role_halo) or {}
            hid = int(h.get("id") or 0)
            hname = str(h.get("name") or "").strip()
            if hid and hname:
                halo_part = f"人物光环{hid}.{hname}"
            elif hname:
                halo_part = f"人物光环{hname}"
            else:
                halo_part = f"人物光环{role_halo}"
        else:
            halo_part = "人物光环不配置"

        pet_name = self._pet_name(pet_anim)
        if pet_anim > 0:
            pet_part = f"宠物使用{pet_anim}" + (f"（{pet_name}）" if pet_name else "")
        else:
            pet_part = "宠物不配置"

        if perfect:
            perfect_part = "开启满档效果"
        else:
            perfect_part = "满档不配置"

        if crest > 0:
            cname = self._crest_name(crest)
            crest_part = f"满档光环{crest}.{cname}" if cname else f"满档光环{crest}"
        else:
            crest_part = "满档光环不配置"

        if ride > 0:
            r = self._ride_by_id.get(ride) or self._ride_by_grano.get(ride) or {}
            rid = int(r.get("id") or 0) or ride
            rname = str(r.get("name") or "").strip()
            if rname:
                ride_part = f"坐骑Id{rid}（{rname}）"
            else:
                ride_part = f"坐骑Id{ride}"
        else:
            ride_part = "坐骑不配置"

        return (
            f"{slot}号位{char_part}，{halo_part}，{pet_part}，{perfect_part}，{crest_part}，{ride_part}"
        )

    def _build_slot_comments(self) -> list[str]:
        return [self._slot_comment_line(s) for s in self.battle_cfg.get("slots") or []]

    def _gen_copy_code(self) -> None:
        self._collect_slot_form_to(self._editing_slot)
        self._ensure_five_slots()
        # 不可满档强制 0 后再校验
        for s in self.battle_cfg["slots"]:
            s.pop("pet_halo", None)
            if int(s.get("perfect") or 0) and not self._can_perfect(int(s.get("pet_anim") or 0)):
                s["perfect"] = 0
        # 生成码默认带 enabled，游戏内粘贴即启用
        self.battle_cfg["enabled"] = True
        self._fill_slot_form()
        errs = self._validate_slots()
        if errs:
            messagebox.showerror("校验失败", "\n".join(errs))
            self.status.config(text="校验失败，未生成")
            return
        code = encode_code(
            self.battle_cfg, with_comment=True, slot_comments=self._build_slot_comments()
        )
        self.code_box.delete("1.0", "end")
        self.code_box.insert("end", code)
        try:
            self.clipboard_clear()
            self.clipboard_append(code)
            self.update()
        except tk.TclError:
            messagebox.showwarning("剪贴板", "无法写入剪贴板，请手动复制下方文本")
            return
        self._persist_battle_cfg()
        self._persist_last_code(code)
        self.status.config(text="已生成并复制（已自动保存配置与代码）")

    def _clear_all_slots(self) -> None:
        """清空表单与上次代码；并写入空配置（下次打开不再恢复旧码）。"""
        self.battle_cfg = default_battle_cfg()
        self.battle_cfg["enabled"] = False
        self._editing_slot = int(self.hook_slot.get() or 1)
        self._fill_slot_form()
        self.code_box.delete("1.0", "end")
        try:
            BATTLE_CFG.write_text(
                json.dumps(self.battle_cfg, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
        except OSError:
            pass
        try:
            if LAST_CODE.is_file():
                LAST_CODE.unlink()
        except OSError:
            pass
        self.status.config(text="已清空（已清除自动保存）")


def main() -> None:
    if not SEQCHAPTER.exists():
        print("序章游戏目录不存在:", SEQCHAPTER.root)
    app = AppearPreviewGui()
    app.mainloop()


if __name__ == "__main__":
    main()
