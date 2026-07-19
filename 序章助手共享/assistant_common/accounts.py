#!/usr/bin/env python3
# -*- coding: utf-8 -*-
from __future__ import annotations

import json
import uuid
from dataclasses import asdict, dataclass, field
from typing import Any

from .config import ACCOUNTS_PATH, DATA_DIR


@dataclass
class AccountProfile:
    id: str
    label: str
    phone: str
    password: str
    note: str = ""

    @staticmethod
    def create(label: str, phone: str, password: str, note: str = "") -> AccountProfile:
        return AccountProfile(
            id=uuid.uuid4().hex[:12],
            label=label.strip() or phone,
            phone=phone.strip(),
            password=password,
            note=note.strip(),
        )


def load_accounts() -> list[AccountProfile]:
    if not ACCOUNTS_PATH.is_file():
        return []
    raw = json.loads(ACCOUNTS_PATH.read_text(encoding="utf-8"))
    return [AccountProfile(**item) for item in raw.get("accounts", [])]


def save_accounts(accounts: list[AccountProfile]) -> None:
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    payload = {"accounts": [asdict(a) for a in accounts]}
    ACCOUNTS_PATH.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")


def upsert_account(profile: AccountProfile) -> None:
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
