#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""序章补丁「客服按钮替换」可打开的面板列表（与 CustomerBtnGmIlPatcher 一致）。"""
from __future__ import annotations

# mode: uipanel=GetUIPanel().Open() | autoskill | blindbox | boss_tower
CUSTOMER_PANELS: list[dict[str, str | int]] = [
    {"id": "gm1", "label": "GM 命令工具", "panel": "GMToolsPanel", "mode": "uipanel"},
    {"id": "gm2", "label": "GM 道具商店", "panel": "GMStorePanel", "mode": "uipanel"},
    {"id": "gm3", "label": "GM 宠物商店", "panel": "GMPetStorePanel", "mode": "uipanel"},
    {"id": "gm4", "label": "GM 宠物特效", "panel": "GMPetEffectPanel", "mode": "uipanel"},
    {"id": "gm5", "label": "GM 动画设置", "panel": "GMAnimationSettingPanel", "mode": "uipanel"},
    {"id": "blindbox", "label": "盲盒(3028)", "panel": "BlindboxDrawPanel", "mode": "blindbox"},
    {"id": "lottery", "label": "幸运秘宝(3049)", "panel": "LotteryPanel", "mode": "uipanel"},
    {"id": "challengeboss", "label": "讨伐令(3045)", "panel": "ChallengeBossPanel", "mode": "uipanel"},
    {"id": "bravetrial", "label": "英雄试炼(3047)", "panel": "BraveTrialPanel", "mode": "uipanel"},
    {"id": "crystal", "label": "水晶阁", "panel": "LuckCrystalPanel", "mode": "uipanel"},
    {"id": "boss", "label": "讨伐 Boss", "panel": "BOSSChallengePanel", "mode": "uipanel"},
    {"id": "tower", "label": "无尽之塔", "panel": "BOSSChallengePanel", "mode": "boss_tower"},
    {"id": "ruby", "label": "露比试炼", "panel": "RubyTrialPanel", "mode": "uipanel"},
    {"id": "autoskill", "label": "自动技能设置", "panel": "AutoSkillSettingPanel", "mode": "autoskill"},
]


def panel_by_id(panel_id: str) -> dict[str, str | int] | None:
    for row in CUSTOMER_PANELS:
        if row["id"] == panel_id:
            return row
    return None
