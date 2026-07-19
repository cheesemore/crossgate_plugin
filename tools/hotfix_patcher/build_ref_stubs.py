#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""编译 ref_stubs 并复制到 HotfixPatcher 输出目录。"""
from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
STUBS = ROOT / "ref_stubs"
OUT = STUBS / "bin"

PROJECTS = [
    ("UnityEngine.CoreModule.csproj", "UnityEngine.CoreModule.dll"),
    ("UnityEngine.UI.csproj", "UnityEngine.UI.dll"),
    ("UnityEngine.UIModule.csproj", "UnityEngine.UIModule.dll"),
    ("UnityEngine.TextRenderingModule.csproj", "UnityEngine.TextRenderingModule.dll"),
    ("UnityEngine.TextCoreFontEngineModule.csproj", "UnityEngine.TextCoreFontEngineModule.dll"),
    ("UnityEngine.Physics2DModule.csproj", "UnityEngine.Physics2DModule.dll"),
    ("UnityEngine.ParticleSystemModule.csproj", "UnityEngine.ParticleSystemModule.dll"),
    ("UnityEngine.InputLegacyModule.csproj", "UnityEngine.InputLegacyModule.dll"),
    ("UnityEngine.IMGUIModule.csproj", "UnityEngine.IMGUIModule.dll"),
    ("UnityEngine.AssetBundleModule.csproj", "UnityEngine.AssetBundleModule.dll"),
    ("UnityEngine.UnityWebRequestModule.csproj", "UnityEngine.UnityWebRequestModule.dll"),
    ("Unity.TextMeshPro.csproj", "Unity.TextMeshPro.dll"),
    ("Newtonsoft.Json.csproj", "Newtonsoft.Json.dll"),
    ("NativeGallery.Runtime.csproj", "NativeGallery.Runtime.dll"),
    ("Moli.Core.csproj", "Moli.Core.dll"),
    ("LZ4.csproj", "LZ4.dll"),
    ("DOTween.csproj", "DOTween.dll"),
    ("zxing.unity.csproj", "zxing.unity.dll"),
]


def _write_project(name: str, stub_file: str, assembly_name: str) -> None:
    path = STUBS / name
    path.write_text(
        f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <AssemblyName>{assembly_name}</AssemblyName>
    <Nullable>disable</Nullable>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <Deterministic>true</Deterministic>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="{stub_file}" />
  </ItemGroup>
</Project>
""",
        encoding="utf-8",
    )


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)
    for proj, dll_name in PROJECTS:
        stub = proj.replace(".csproj", ".stub.cs")
        _write_project(proj, stub, dll_name.replace(".dll", ""))

    for proj, dll_name in PROJECTS:
        proj_path = STUBS / proj
        if not proj_path.is_file():
            print(f"[SKIP] missing {proj_path.name}")
            continue
        print(f"[BUILD] {proj_path.name}")
        proc = subprocess.run(
            ["dotnet", "build", str(proj_path), "-c", "Release", "-v", "q", "-o", str(OUT)],
            cwd=str(STUBS),
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        if proc.returncode != 0:
            print(proc.stdout or proc.stderr or "build failed")
            return proc.returncode
        built = OUT / dll_name
        if not built.is_file():
            print(f"[FAIL] output missing: {built}")
            return 1

    patcher_out = ROOT / "bin" / "Release" / "net8.0" / "ref_stubs"
    packaged_out = ROOT.parents[1] / "魔力宝贝序章补丁" / "patcher" / "ref_stubs"
    for target in (patcher_out, packaged_out):
        target.mkdir(parents=True, exist_ok=True)
        for _, dll_name in PROJECTS:
            src = OUT / dll_name
            if src.is_file():
                shutil.copy2(src, target / dll_name)
        print(f"[COPY] ref_stubs -> {target}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
