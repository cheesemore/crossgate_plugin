#!/usr/bin/env python3
"""Extract full client send protocol catalog from decompiled hotfix C#."""
from __future__ import annotations

import json
import re
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DECOMP = ROOT / "tools" / "hotfix_ilspy" / "hotfix.dll.bytes.decompiled.cs"
OUT_JSON = ROOT / "tools" / "protocol_catalog.json"
OUT_MD = ROOT / "tools" / "PROTOCOL_CATALOG.md"


def parse_lssproto_enum(text: str) -> dict[str, int]:
    m = re.search(r"public enum LSSPROTO\s*\{(.*?)\n\}", text, re.S)
    if not m:
        raise RuntimeError("LSSPROTO enum not found")
    protos: dict[str, int] = {}
    for line in m.group(1).splitlines():
        line = line.strip().rstrip(",")
        if not line or line.startswith("["):
            continue
        mm = re.match(r"(\w+)\s*=\s*(-?\d+)", line)
        if mm:
            protos[mm.group(1)] = int(mm.group(2))
    return protos


def parse_proto_classes(text: str) -> dict[str, list[str]]:
    classes: dict[str, list[str]] = {}
    class_pat = re.compile(
        r"public sealed class (Proto_CS_\w+)\s*:\s*IMessage",
    )
    field_pat = re.compile(r"public const int (\w+)FieldNumber\s*=\s*\d+")
    for m in class_pat.finditer(text):
        name = m.group(1)
        chunk = text[m.start() : m.start() + 12000]
        fields = []
        seen = set()
        for fm in field_pat.finditer(chunk):
            fn = fm.group(1)
            if fn not in seen:
                seen.add(fn)
                fields.append(fn)
        if fields:
            classes[name] = fields
    return classes


def var_to_proto(var: str) -> str | None:
    if var.startswith("proto_CS_"):
        return "Proto_CS_" + var[9:]
    if var.startswith("Proto_CS_"):
        return var
    return None


def parse_send_messages(text: str) -> list[dict]:
    pat = re.compile(
        r"SendMessage\(LSSPROTO\.(LSSPROTO_\w+),\s*\(IMessage\)\(object\)(\w+)"
    )
    rows = []
    for m in pat.finditer(text):
        rows.append(
            {
                "opcode_name": m.group(1),
                "proto_var": m.group(2),
                "proto_type": var_to_proto(m.group(2)),
                "line": text[: m.start()].count("\n") + 1,
            }
        )
    return rows


def extract_send_wrapper_methods(text: str) -> list[dict]:
    """Methods containing SendMessage — opcode, signature, Type param name, literals."""
    results = []
    # Find each SendMessage and walk back to method signature
    send_pat = re.compile(
        r"SendMessage\(LSSPROTO\.(LSSPROTO_\w+),\s*\(IMessage\)\(object\)(\w+)"
    )
    for sm in send_pat.finditer(text):
        pos = sm.start()
        chunk_start = max(0, pos - 4000)
        chunk = text[chunk_start : pos + 200]
        headers = [
            m
            for m in re.finditer(r"public void (Send\w+)\(([^)]*)\)", chunk)
            if chunk_start + m.start() < pos
        ]
        if not headers:
            continue
        hm = headers[-1]
        method = hm.group(1)
        args = hm.group(2).strip()
        body = text[chunk_start + hm.end() : pos]
        # detect Type assignment source
        type_source = None
        if re.search(r"\.Type\s*=\s*title\b", body):
            type_source = "title"
        elif re.search(r"\.Type\s*=\s*type\b", body):
            type_source = "type"
        elif re.search(r"\.Type\s*=\s*msg\b", body):
            type_source = "msg"
        elif re.search(r"\.Type\s*=\s*str\b", body):
            type_source = "str"
        literals = sorted(set(re.findall(r'\.Type\s*=\s*"([^"]+)"', body)))
        field_assigns = defaultdict(set)
        for fm in re.finditer(r"\.(\w+)\s*=\s*([^;\n]+)", body):
            fn, val = fm.group(1), fm.group(2).strip()
            if fn == "Type":
                continue
            val = re.sub(r"\s+", " ", val)[:80]
            field_assigns[fn].add(val)
        results.append(
            {
                "method": method,
                "args": args,
                "opcode": sm.group(1),
                "proto_type": var_to_proto(sm.group(2)),
                "type_source": type_source,
                "type_literals_in_body": literals,
                "field_assigns": {k: sorted(v)[:10] for k, v in field_assigns.items()},
                "line": text[: hm.start() + max(0, pos - 4000)].count("\n") + 1,
            }
        )
    return results


def extract_call_site_literals(text: str, wrappers: list[dict]) -> dict[str, set[str]]:
    """Collect SendXxx("literal") call sites grouped by opcode."""
    method_to_opcode: dict[str, set[str]] = defaultdict(set)
    for w in wrappers:
        method_to_opcode[w["method"]].add(w["opcode"])

    opcode_literals: dict[str, set[str]] = defaultdict(set)
    for method, ops in method_to_opcode.items():
        call_pat = re.compile(rf"\b{re.escape(method)}\(\s*\"([^\"]+)\"")
        for m in call_pat.finditer(text):
            lit = m.group(1)
            for op in ops:
                opcode_literals[op].add(lit)
    return opcode_literals


def build_catalog(text: str) -> dict:
    protos = parse_lssproto_enum(text)
    proto_classes = parse_proto_classes(text)
    sends = parse_send_messages(text)
    wrappers = extract_send_wrapper_methods(text)
    call_literals = extract_call_site_literals(text, wrappers)

    usage: dict[str, dict] = {}
    for name, num in protos.items():
        usage[name] = {
            "opcode": num,
            "client_send": False,
            "send_count": 0,
            "proto_types": [],
            "proto_fields": {},
            "type_literals": [],
            "send_wrappers": [],
        }

    proto_by_opcode: dict[str, set[str]] = defaultdict(set)
    for s in sends:
        op = s["opcode_name"]
        usage[op]["client_send"] = True
        usage[op]["send_count"] += 1
        if s["proto_type"]:
            proto_by_opcode[op].add(s["proto_type"])

    wrappers_by_opcode: dict[str, list[dict]] = defaultdict(list)
    seen_wrapper: dict[str, set[str]] = defaultdict(set)
    for w in wrappers:
        op = w["opcode"]
        key = w["method"] + "|" + w["args"]
        if key not in seen_wrapper[op]:
            seen_wrapper[op].add(key)
            wrappers_by_opcode[op].append(
                {
                    "method": w["method"],
                    "args": w["args"],
                    "proto_type": w["proto_type"],
                    "type_source": w["type_source"],
                    "type_literals_in_body": w["type_literals_in_body"],
                    "field_assigns": w["field_assigns"],
                }
            )

    for op, u in usage.items():
        pts = sorted(proto_by_opcode.get(op, []))
        u["proto_types"] = pts
        u["proto_fields"] = {pt: proto_classes.get(pt, []) for pt in pts}
        lits = set(call_literals.get(op, []))
        for w in wrappers_by_opcode.get(op, []):
            lits.update(w["type_literals_in_body"])
        u["type_literals"] = sorted(lits)
        u["send_wrappers"] = wrappers_by_opcode.get(op, [])

    all_types = sorted(
        set(t for u in usage.values() for t in u["type_literals"])
        | set(re.findall(r'\.Type\s*=\s*"([^"]+)"', text))
        | set(
            m.group(1)
            for m in re.finditer(
                r"(?:Send\w+)\(\s*\"([^\"]+)\"", text
            )
        )
    )

    return {
        "protos": protos,
        "proto_classes": proto_classes,
        "sends": sends,
        "usage": usage,
        "all_type_literals": all_types,
        "stats": {
            "protocol_count": len(protos),
            "client_send_count": sum(1 for u in usage.values() if u["client_send"]),
            "proto_class_count": len(proto_classes),
            "type_literal_count": len(all_types),
            "send_message_calls": len(sends),
            "send_wrappers": len(wrappers),
        },
    }


def write_markdown(catalog: dict) -> str:
    protos = catalog["protos"]
    usage = catalog["usage"]
    all_types = catalog["all_type_literals"]
    proto_classes = catalog["proto_classes"]
    stats = catalog["stats"]

    lines: list[str] = []
    lines.append("# 魔力宝贝：序章 — 客户端发送协议完整目录")
    lines.append("")
    lines.append("> 自动生成：`tools/extract_protocol_catalog.py`")
    lines.append("> 数据源：`tools/hotfix_ilspy/hotfix.dll.bytes.decompiled.cs`")
    lines.append("")
    lines.append("## 统计")
    lines.append("")
    lines.append(f"| 项 | 数量 |")
    lines.append(f"|----|-----:|")
    for k, v in stats.items():
        lines.append(f"| {k} | {v} |")
    lines.append("")
    lines.append("## 发包 API")
    lines.append("")
    lines.append("```csharp")
    lines.append("Manager<NetManager>.Instance.SendMessage(LSSPROTO opcode, IMessage proto);")
    lines.append("// GM: LSSPROTO_TALKEX_FUNC(1000), Channel=PROTO_CHANNEL_TYPE_GM, Msg=命令文本")
    lines.append("```")
    lines.append("")
    lines.append("助手 IPC：`序章助手共享/assistant_common/ipc.py` → `send_proto(opcode, proto_type, fields, uid?)`")
    lines.append("")

    lines.append("## 全量协议表（229）")
    lines.append("")
    lines.append("| 编号 | 协议 | 发包 | 次数 | Proto_CS | Type 数量 |")
    lines.append("|-----:|------|:----:|-----:|----------|----------:|")
    for name in sorted(protos, key=lambda k: protos[k]):
        u = usage[name]
        send = "Y" if u["client_send"] else "—"
        cnt = u["send_count"] or "—"
        pts = ", ".join(u["proto_types"]) or "—"
        tc = len(u["type_literals"])
        lines.append(
            f"| {u['opcode']} | `{name}` | {send} | {cnt} | {pts} | {tc if tc else '—'} |"
        )
    lines.append("")

    lines.append("## 已发包协议 — 字段 / Type / 封装")
    lines.append("")
    for name in sorted(protos, key=lambda k: protos[k]):
        u = usage[name]
        if not u["client_send"]:
            continue
        lines.append(f"### {u['opcode']} `{name}`")
        lines.append("")
        for pt, fields in u.get("proto_fields", {}).items():
            if fields:
                lines.append(f"**{pt}**：`{', '.join(fields)}`")
            else:
                lines.append(f"**{pt}**")
        if u["type_literals"]:
            lines.append("")
            lines.append("**Type / 操作名：**")
            for t in u["type_literals"]:
                lines.append(f"- `{t}`")
        if u["send_wrappers"]:
            lines.append("")
            lines.append("**Send 封装：**")
            for w in u["send_wrappers"]:
                sig = f"`{w['method']}({w['args']})`"
                if w["type_source"]:
                    sig += f" → Type=`{w['type_source']}`"
                lines.append(f"- {sig}")
                if w["field_assigns"]:
                    fa = ", ".join(f"{k}={v[0]}" for k, v in list(w["field_assigns"].items())[:6])
                    lines.append(f"  - 字段赋值：{fa}")
        lines.append("")

    lines.append("## Proto_CS 全字段索引")
    lines.append("")
    for cls in sorted(proto_classes):
        lines.append(f"- **{cls}**：`{', '.join(proto_classes[cls])}`")
    lines.append("")

    lines.append("## 全局 Type / 操作名字符串")
    lines.append("")
    for t in all_types:
        lines.append(f"- `{t}`")
    lines.append("")
    return "\n".join(lines)


def main() -> None:
    print(f"Reading {DECOMP} ...")
    text = DECOMP.read_text(encoding="utf-8", errors="replace")
    catalog = build_catalog(text)
    catalog["source"] = str(DECOMP.relative_to(ROOT))

    OUT_JSON.write_text(
        json.dumps(
            {
                "source": catalog["source"],
                "stats": catalog["stats"],
                "protocols": {
                    n: catalog["usage"][n]
                    for n in sorted(catalog["protos"], key=lambda k: catalog["protos"][k])
                },
                "proto_classes": catalog["proto_classes"],
                "all_type_literals": catalog["all_type_literals"],
            },
            ensure_ascii=False,
            indent=2,
        ),
        encoding="utf-8",
    )
    OUT_MD.write_text(write_markdown(catalog), encoding="utf-8")
    print(f"Wrote {OUT_JSON} ({OUT_JSON.stat().st_size // 1024} KB)")
    print(f"Wrote {OUT_MD} ({OUT_MD.stat().st_size // 1024} KB)")
    print(json.dumps(catalog["stats"], ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
