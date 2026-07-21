#!/usr/bin/env python3
from pathlib import Path
import re
import struct

p = Path(r"d:\Downloads\cg37-1102.exe")
data = p.read_bytes()

print("size", len(data))
print("sha256", __import__("hashlib").sha256(data).hexdigest())

# PE sections
e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
num = struct.unpack_from("<H", data, e_lfanew + 6)[0]
opt = struct.unpack_from("<H", data, e_lfanew + 20)[0]
sec = e_lfanew + 24 + opt
for i in range(num):
    o = sec + i * 40
    name = data[o : o + 8].split(b"\0")[0].decode("ascii", "replace")
    vsize, vaddr, rsize, roff = struct.unpack_from("<IIII", data, o + 8)
    print(f"section {name!r} raw={rsize} off={roff}")

# ASCII strings
keywords = (
    "http",
    "download",
    "install",
    "cg37",
    "simple",
    "partial",
    "config",
    "1102",
    "winhttp",
    "seven",
    "7z",
    "zip",
    "update",
    "version",
    "game",
    "cdn",
    "egio",
    ".exe",
    ".7z",
    ".zip",
    "path",
    "dir",
)
seen = set()
for m in re.finditer(rb"[\x20-\x7e]{6,}", data):
    s = m.group().decode("ascii", "ignore")
    sl = s.lower()
    if any(k in sl for k in keywords) and s not in seen:
        seen.add(s)
        print("A", s[:200])

# UTF-16LE
for i in range(0, len(data) - 8):
    if data[i] == 0 and 0x20 <= data[i + 1] < 0x7F:
        j = i
        chars = []
        while j + 1 < len(data) and data[j + 1] != 0:
            if data[j + 1] < 0x20:
                break
            chars.append(chr(data[j + 1]))
            j += 2
        s = "".join(chars)
        if len(s) >= 6 and s not in seen:
            sl = s.lower()
            if any(k in sl for k in keywords):
                seen.add(s)
                print("U", s[:200])
