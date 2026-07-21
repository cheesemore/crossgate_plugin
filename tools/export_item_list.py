#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Export item_tbitemconfig from local AssetBundle (same table GM store uses).

GM store builds its list from ConfigManager.GetTbItemConfig().DataList, loaded from
Assets/Res/Config/ExcelGeneral/item_tbitemconfig.bytes inside bundle
4bd60e623f3f8796cb234b3f01f0c91a.b.

The on-disk asset uses a 2-byte header (0xAA 0x38) plus fixed 124-byte records with:
  - 3x u8-length UTF-8 strings: Name, Secretname, Label  (GM search uses Secretname + Id)
  - Id at record offset +28 (u32 LE)
  - Imagenumber at offset +29 (u8)
  - BatchUse at offset +25 (u16 LE)

This is a best-effort parser for the shipped binary layout (not full Luban decompression).
"""
from __future__ import annotations

import argparse
import csv
import json
import struct
import sys
from datetime import datetime
from pathlib import Path

import re
from openpyxl import Workbook
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_BUNDLE = ROOT / "cg37_Data/assets/4bd60e623f3f8796cb234b3f01f0c91a.b"
RECORD_STRIDE = 124
HEADER_MAGIC = b"\xaa\x38"
_LEVEL_ONLY = re.compile(r"^\d+级$")
_HAS_CJK = re.compile(r"[\u4e00-\u9fff]")


def extract_textasset_bytes(bundle_path: Path, asset_name: str) -> bytes:
    try:
        import UnityPy
    except ImportError as exc:
        raise SystemExit("UnityPy required: pip install UnityPy") from exc

    raw = bundle_path.read_bytes()
    idx = raw.find(b"UnityFS")
    if idx < 0:
        raise FileNotFoundError(f"UnityFS header not found in {bundle_path}")
    env = UnityPy.load(raw[idx:])
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        data = obj.read()
        if data.m_Name != asset_name:
            continue
        reader = obj.reader
        reader.Position = obj.byte_start
        rawobj = reader.read(obj.byte_size)
        pos = 4 + struct.unpack_from("<I", rawobj, 0)[0]
        pos = (pos + 3) & ~3
        size = struct.unpack_from("<I", rawobj, pos)[0]
        pos += 4
        return rawobj[pos : pos + size]
    raise FileNotFoundError(f"{asset_name} not in {bundle_path}")


def read_u8_string(buf: bytes, pos: int) -> tuple[str, int]:
    ln = buf[pos]
    pos += 1
    if ln == 0 or pos + ln > len(buf):
        raise ValueError(f"invalid string length {ln} at {pos}")
    return buf[pos : pos + ln].decode("utf-8"), pos + ln


def _clean_text(value: str) -> str:
    value = value.encode("utf-8", "replace").decode("utf-8")
    return "".join(ch for ch in value if ch >= " " or ch in "\t")


def _valid_secret(name: str) -> bool:
    if not name or len(name) > 40:
        return False
    if _LEVEL_ONLY.match(name):
        return False
    if name.endswith("？") or name.endswith("?"):
        return False
    return bool(_HAS_CJK.search(name))


def parse_record(buf: bytes, start: int) -> dict | None:
    if start + RECORD_STRIDE > len(buf):
        return None
    rec = buf[start : start + RECORD_STRIDE]
    try:
        pos = 0
        name, pos = read_u8_string(rec, pos)
        secret, pos = read_u8_string(rec, pos)
        label, pos = read_u8_string(rec, pos)
        if not _valid_secret(secret):
            return None
        batch_use = struct.unpack_from("<H", rec, 25)[0]
        item_id = struct.unpack_from("<I", rec, 28)[0]
        image = rec[29]
        if item_id < 100 or item_id > 5_000_000:
            return None
        return {
            "Id": item_id,
            "Secretname": _clean_text(secret),
            "Name": _clean_text(name),
            "Label": _clean_text(label),
            "BatchUse": batch_use,
            "Imagenumber": image,
        }
    except (UnicodeDecodeError, ValueError, struct.error):
        return None


def load_items(config_bytes: bytes) -> list[dict]:
    if not config_bytes.startswith(HEADER_MAGIC):
        raise ValueError(f"unexpected header {config_bytes[:4].hex()}, expected aa38")
    body = config_bytes[2:]
    items_by_id: dict[int, dict] = {}
    pos = 0
    while pos + RECORD_STRIDE <= len(body):
        row = parse_record(body, pos)
        if row:
            items_by_id.setdefault(row["Id"], row)
            pos += RECORD_STRIDE
        else:
            pos += 1
    items = list(items_by_id.values())
    items.sort(key=lambda x: x["Id"])
    return items


EXCEL_COLUMNS = [
    ("Id", "道具ID", 12),
    ("Secretname", "显示名", 22),
    ("Name", "分类/内部名", 18),
    ("Label", "标签", 10),
    ("Imagenumber", "图标编号", 10),
    ("BatchUse", "批量使用", 10),
]


def write_excel(items: list[dict], path: Path) -> None:
    wb = Workbook()
    ws = wb.active
    ws.title = "道具列表"

    header_fill = PatternFill("solid", fgColor="1F4E79")
    header_font = Font(name="微软雅黑", bold=True, color="FFFFFF", size=11)
    body_font = Font(name="微软雅黑", size=10)
    alt_fill = PatternFill("solid", fgColor="F2F7FB")
    white_fill = PatternFill("solid", fgColor="FFFFFF")
    thin = Side(style="thin", color="D0D7DE")
    border = Border(left=thin, right=thin, top=thin, bottom=thin)
    center = Alignment(horizontal="center", vertical="center")
    left = Alignment(horizontal="left", vertical="center")

    # Title row
    ws.merge_cells(start_row=1, start_column=1, end_row=1, end_column=len(EXCEL_COLUMNS))
    title = ws.cell(row=1, column=1, value="魔力宝贝：序章 — 道具配表 (item_tbitemconfig)")
    title.font = Font(name="微软雅黑", bold=True, size=14, color="1F4E79")
    title.alignment = Alignment(horizontal="left", vertical="center")
    ws.row_dimensions[1].height = 28

    meta = ws.cell(
        row=2,
        column=1,
        value=f"导出时间：{datetime.now():%Y-%m-%d %H:%M:%S}    条目数：{len(items)}    来源：GM商店同源配表",
    )
    ws.merge_cells(start_row=2, start_column=1, end_row=2, end_column=len(EXCEL_COLUMNS))
    meta.font = Font(name="微软雅黑", size=9, color="666666")
    meta.alignment = left
    ws.row_dimensions[2].height = 20

    header_row = 3
    for col_idx, (_, title_cn, width) in enumerate(EXCEL_COLUMNS, start=1):
        cell = ws.cell(row=header_row, column=col_idx, value=title_cn)
        cell.fill = header_fill
        cell.font = header_font
        cell.alignment = center
        cell.border = border
        ws.column_dimensions[get_column_letter(col_idx)].width = width
    ws.row_dimensions[header_row].height = 22
    ws.freeze_panes = "A4"

    for row_idx, item in enumerate(items, start=header_row + 1):
        fill = alt_fill if (row_idx - header_row) % 2 == 0 else white_fill
        for col_idx, (key, _, _) in enumerate(EXCEL_COLUMNS, start=1):
            value = item.get(key, "")
            cell = ws.cell(row=row_idx, column=col_idx, value=value)
            cell.font = body_font
            cell.fill = fill
            cell.border = border
            if key in ("Id", "Imagenumber", "BatchUse"):
                cell.alignment = center
                cell.number_format = "0"
            else:
                cell.alignment = left

    ws.auto_filter.ref = f"A{header_row}:{get_column_letter(len(EXCEL_COLUMNS))}{header_row + len(items)}"
    wb.save(path)


def write_csv(items: list[dict], path: Path) -> None:
    fields = [key for key, _, _ in EXCEL_COLUMNS]
    with path.open("w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, fieldnames=fields, quoting=csv.QUOTE_ALL)
        w.writeheader()
        w.writerows(items)


def gm_search(items: list[dict], query: str) -> list[dict]:
    """Same logic as GMStorePanel.OnClickSearchCallBack."""
    q = query.strip()
    if not q:
        return items
    out = []
    for it in items:
        if q in it["Secretname"] or q in str(it["Id"]):
            out.append(it)
    return out


def main() -> int:
    parser = argparse.ArgumentParser(description="Export item_tbitemconfig (GM store table)")
    parser.add_argument("--bundle", type=Path, default=DEFAULT_BUNDLE)
    parser.add_argument(
        "--out",
        type=Path,
        default=ROOT / "tools/item_list_export.xlsx",
        help="output path (.xlsx default, .csv if suffix is .csv)",
    )
    parser.add_argument("--json", type=Path, default=None, help="optional JSON output")
    parser.add_argument("--search", type=str, default=None, help="test GM local search")
    args = parser.parse_args()

    if not args.bundle.is_file():
        print(f"bundle missing: {args.bundle}", file=sys.stderr)
        return 1

    raw = extract_textasset_bytes(args.bundle, "item_tbitemconfig")
    items = load_items(raw)
    if args.search:
        hits = gm_search(items, args.search)
        print(f"GM search '{args.search}': {len(hits)} hit(s)")
        for it in hits[:20]:
            print(f"  Id={it['Id']}  Secretname={it['Secretname']}  Label={it['Label']}")
        if len(hits) > 20:
            print(f"  ... and {len(hits) - 20} more")

    args.out.parent.mkdir(parents=True, exist_ok=True)
    if args.out.suffix.lower() == ".csv":
        write_csv(items, args.out)
    else:
        if args.out.suffix.lower() not in (".xlsx", ".xlsm"):
            args.out = args.out.with_suffix(".xlsx")
        write_excel(items, args.out)
    print(f"exported {len(items)} items -> {args.out}")

    if args.json:
        args.json.write_text(json.dumps(items, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"json -> {args.json}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
