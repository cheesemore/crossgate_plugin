#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""GUI / 简单补丁 / 傻瓜补丁共用的默认组合选项。"""

DEFAULT_COMBO_KWARGS = {
    "vip": True,
    "vip_non_vip": True,
    "vip_scale": 5,
    "battle_nine_action": True,
    "battle_nine_external": False,
    "auto_seal_external": False,
    "auto_catch_external": False,
    "auto_sell_external": False,
    "customer_gm": True,
    "customer_gm_mode": "autoskill",
    "map_sprint": True,
    "map_sprint_scale": 8,
    "battle_longpress": True,
    "level_one_include_all": True,
    "transition_speed": False,
    "transition_speed_scale": 0.4,
    "skill_effect_speed": True,
    "skill_effect_scale": 2.0,
    "pet_equip_unlock": False,
    "wiki_download_res": False,
    "wiki_label": False,
    "inject_bridge": False,
    "from_orig": True,
}

# 傻瓜补丁：无九动、无加速过场、无自动烧卡（九动/烧卡仅 GUI 显式开启）
FOOLPROOF_COMBO_KWARGS = {
    **DEFAULT_COMBO_KWARGS,
    "battle_nine_action": False,
    "battle_nine_external": False,
    "auto_seal_external": False,
    "transition_speed": False,
}

# 无九动傻瓜包（与 FOOLPROOF_COMBO_KWARGS 相同，供 --no-nine 路径显式引用）
FOOLPROOF_NO_NINE_COMBO_KWARGS = {
    **FOOLPROOF_COMBO_KWARGS,
}

# 傻瓜补丁·自动烧卡：无九动 + 自动烧卡；战斗倍速/特效取最高（10x / 5x）
FOOLPROOF_BURN_SEAL_COMBO_KWARGS = {
    **FOOLPROOF_NO_NINE_COMBO_KWARGS,
    "auto_seal_external": True,
    "auto_catch_external": False,
    "level_one_include_all": True,
    "vip_scale": 10,
    "skill_effect_speed": True,
    "skill_effect_scale": 5.0,
}

# 傻瓜补丁·自动抓宠：无九动 + 自动抓宠（与自动烧卡互斥）
FOOLPROOF_AUTO_CATCH_COMBO_KWARGS = {
    **FOOLPROOF_NO_NINE_COMBO_KWARGS,
    "auto_seal_external": False,
    "auto_catch_external": True,
    "level_one_include_all": True,
    "vip_scale": 5,
    "skill_effect_speed": True,
    "skill_effect_scale": 2.0,
}

# 兼容旧名：捉宠 → 现指自动抓宠（不再指向烧卡）
FOOLPROOF_CATCH_PET_COMBO_KWARGS = FOOLPROOF_AUTO_CATCH_COMBO_KWARGS

# 序章多开器「启动前自动注入」使用的组合（不含客服 GM；改这里即可，勿改多开器代码）
LAUNCH_INJECT_PRESET = {
    "vip": True,
    "vip_non_vip": True,
    "vip_scale": 5,
    "battle_nine_action": False,
    "customer_gm": False,
    "map_sprint": True,
    "map_sprint_scale": 8,
    "battle_longpress": True,
    "level_one_include_all": True,
    "transition_speed": False,
    "transition_speed_scale": 0.4,
    "skill_effect_speed": True,
    "skill_effect_scale": 2.0,
    "pet_equip_unlock": False,
    "wiki_download_res": False,
    "wiki_label": False,
    "from_orig": True,
}
