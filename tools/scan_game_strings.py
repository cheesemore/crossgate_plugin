#!/usr/bin/env python3
# -*- coding: utf-8 -*-
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FILES = [
    ROOT / "cg37_Data/partialconfig.bin",
    ROOT / "cg37_Data/StreamingAssets/partialconfig.bin",
    ROOT / "GameAssembly.dll",
    ROOT / "cg37.exe",
    ROOT / "cg37_Data/assets/hotfixdata/hotfix.dll.bytes",
    ROOT / "cg37_Data/tempdnsconfig.dat",
]

URL_RE = re.compile(rb"https?://[A-Za-z0-9._\-/:%?=&+#]+")
DOMAIN_RE = re.compile(
    rb"[A-Za-z0-9][A-Za-z0-9._\-]{2,80}\.(?:com|cn|net|org|io)(?:[/?#]|\\x00|$)"
)
ICP_RE = re.compile(
    rb"(?:ICP|\xe5\xa4\x87\xe6\xa1\x88|"
    rb"\xe4\xba\xacICP|\xe6\xb2\xaaICP|\xe7\xb2\xa4ICP|\xe6\xb5\x99ICP)[^\x00]{0,120}",
    re.I,
)


def scan(path: Path) -> None:
    if not path.is_file():
        return
    data = path.read_bytes()
    urls = sorted({u.decode("ascii", "ignore") for u in URL_RE.findall(data)})
    domains = sorted({d.decode("ascii", "ignore").rstrip("\x00") for d in DOMAIN_RE.findall(data)})
    icps = sorted({x.decode("utf-8", "ignore") for x in ICP_RE.findall(data)})
    print(f"=== {path.name} ({len(data)} bytes) ===")
    for u in urls:
        print(f"  URL  {u}")
    for d in domains:
        low = d.lower()
        if any(x in low for x in ("system.", "microsoft", "unity", "google", "github")):
            continue
        print(f"  DOM  {d}")
    for i in icps:
        print(f"  ICP  {i}")
    print()


if __name__ == "__main__":
    for f in FILES:
        scan(f)
