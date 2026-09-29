#!/usr/bin/env python3
# -*- coding: utf-8 -*-
from __future__ import annotations

import json
import uuid
from dataclasses import asdict, dataclass
from typing import Any

from .config import ACCOUNTS_PATH, DATA_DIR


@dataclass
class AccountProfile:
    id: str
    label: str
    phone: str
    password: str
    note: str = ""
    # 游戏二级安全锁（6 位数字）；空=未配置；仅当游戏内需要验证时交易才因空码失败
    secondary_code: str = ""

    @staticmethod
    def create(
        label: str,
        phone: str,
        password: str,
        note: str = "",
        secondary_code: str = "",
    ) -> AccountProfile:
        code, err = normalize_secondary_code(secondary_code)
        if err:
            raise ValueError(err)
        return AccountProfile(
            id=uuid.uuid4().hex[:12],
            label=label.strip() or phone,
            phone=phone.strip(),
            password=password,
            note=note.strip(),
            secondary_code=code,
        )


def normalize_secondary_code(raw: Any) -> tuple[str, str]:
    """返回 (规范化后的二级码, 错误信息)。空串合法；非空须恰好 6 位数字。"""
    if raw is None:
        return "", ""
    s = str(raw).strip()
    if isinstance(raw, float) and raw == int(raw):
        s = str(int(raw))
    if not s:
        return "", ""
    if len(s) == 6 and s.isdigit():
        return s, ""
    return "", "二级码须为空或 6 位数字"


def _from_dict(item: dict[str, Any]) -> AccountProfile:
    code, _ = normalize_secondary_code(item.get("secondary_code", ""))
    return AccountProfile(
        id=str(item.get("id") or uuid.uuid4().hex[:12]),
        label=str(item.get("label") or item.get("phone") or ""),
        phone=str(item.get("phone") or "").strip(),
        password=str(item.get("password") or ""),
        note=str(item.get("note") or "").strip(),
        secondary_code=code,
    )


def load_accounts() -> list[AccountProfile]:
    if not ACCOUNTS_PATH.is_file():
        return []
    raw = json.loads(ACCOUNTS_PATH.read_text(encoding="utf-8"))
    return [_from_dict(item) for item in raw.get("accounts", [])]


def save_accounts(accounts: list[AccountProfile]) -> None:
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    payload = {"accounts": [asdict(a) for a in accounts]}
    ACCOUNTS_PATH.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
    )


def upsert_account(profile: AccountProfile) -> None:
    code, err = normalize_secondary_code(profile.secondary_code)
    if err:
        raise ValueError(err)
    profile.secondary_code = code
    accounts = load_accounts()
    for i, a in enumerate(accounts):
        if a.id == profile.id:
            accounts[i] = profile
            save_accounts(accounts)
            return
    accounts.append(profile)
    save_accounts(accounts)


def delete_account(account_id: str) -> None:
    save_accounts([a for a in load_accounts() if a.id != account_id])


def find_account_by_phone(phone: str) -> AccountProfile | None:
    key = (phone or "").strip()
    if not key:
        return None
    for a in load_accounts():
        if (a.phone or "").strip() == key:
            return a
    return None
