#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""GUI / 简单补丁 / 傻瓜补丁共用的默认组合选项。"""

# 助手面板（百科入口）+ 抓宠/烧卡 DLL 面板模式：倍速等 IL 仍走 hotfix，玩法开关在面板里切
DEFAULT_COMBO_KWARGS = {
    "vip": True,
    "vip_non_vip": True,
    "vip_scale": 5,
    "battle_nine_action": False,
    "battle_nine_external": True,
    "auto_seal_external": True,
    "auto_catch_external": True,
    "auto_catch_nopet_external": False,
    "auto_catch_sell_external": True,
    "lv1_auto_external": False,
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
    "daily_claim": True,
    "newbie_gift_code": True,
    "boss_key_fps": True,  # 切后台 / 老板键隐藏 → 10 FPS
    "wiki_fps": False,
    "wiki_test_ui": True,  # 百科 → 助手面板
    "battle_appear": True,
    "inject_bridge": False,
    "from_orig": True,
}

# 傻瓜补丁·基底：助手面板 + 抓宠/烧卡 DLL（面板切换）。
# 九动由 foolproof_apply(enable_nine) 决定：九动版加速 → 面板四选一；融合版 → 三选一。
FOOLPROOF_COMBO_KWARGS = {
    **DEFAULT_COMBO_KWARGS,
    "battle_nine_action": False,
    "battle_nine_external": False,  # 九动版加速时再置 True
    "auto_seal_external": True,
    "auto_catch_external": True,
    "wiki_test_ui": True,
    "transition_speed": False,
}

FOOLPROOF_NO_NINE_COMBO_KWARGS = {
    **FOOLPROOF_COMBO_KWARGS,
    # 融合版无九动 DLL，单独打 Magics，供面板「无宠二动」
    "player_action_magics": True,
}

# 傻瓜「烧卡档」：同面板双 DLL（抓宠+烧卡），倍速/特效取最高；无九动
FOOLPROOF_BURN_SEAL_COMBO_KWARGS = {
    **FOOLPROOF_NO_NINE_COMBO_KWARGS,
    "auto_seal_external": True,
    "auto_catch_external": True,
    "wiki_test_ui": True,
    "level_one_include_all": True,
    "vip_scale": 10,
    "skill_effect_speed": True,
    "skill_effect_scale": 5.0,
}

FOOLPROOF_BURN_SEAL_SLOW_COMBO_KWARGS = {
    **FOOLPROOF_BURN_SEAL_COMBO_KWARGS,
    "vip": False,
    "vip_non_vip": False,
    "map_sprint": False,
    "skill_effect_speed": False,
    "transition_speed": False,
}

# 傻瓜「抓宠档」：同面板能力，5x 加速
FOOLPROOF_AUTO_CATCH_COMBO_KWARGS = {
    **FOOLPROOF_NO_NINE_COMBO_KWARGS,
    "auto_seal_external": True,
    "auto_catch_external": True,
    "auto_catch_nopet_external": False,
    "auto_catch_sell_external": True,
    "wiki_test_ui": True,
    "level_one_include_all": True,
    "vip_scale": 5,
    "skill_effect_speed": True,
    "skill_effect_scale": 2.0,
}

FOOLPROOF_AUTO_CATCH_NOPET_COMBO_KWARGS = {
    **FOOLPROOF_AUTO_CATCH_COMBO_KWARGS,
    "auto_catch_external": False,
    "auto_catch_nopet_external": True,
    "auto_seal_external": True,  # 面板仍可切烧卡
}

FOOLPROOF_CATCH_PET_COMBO_KWARGS = FOOLPROOF_AUTO_CATCH_COMBO_KWARGS

LAUNCH_INJECT_PRESET = {
    "vip": True,
    "vip_non_vip": True,
    "vip_scale": 5,
    "battle_nine_action": False,
    "battle_nine_external": False,
    "auto_seal_external": True,
    "auto_catch_external": True,
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
    "daily_claim": True,
    "newbie_gift_code": True,
    "boss_key_fps": True,  # 切后台 / 老板键隐藏 → 10 FPS
    "wiki_fps": False,
    "wiki_test_ui": True,
    "battle_appear": True,
    "from_orig": True,
}
