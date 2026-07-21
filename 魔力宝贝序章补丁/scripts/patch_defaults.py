#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""GUI / 简单补丁 / 傻瓜补丁共用的默认组合选项。"""

DEFAULT_COMBO_KWARGS = {
    "vip": True,
    "vip_non_vip": True,
    "vip_scale": 5,
    "battle_nine_action": True,
    "customer_gm": True,
    "customer_gm_mode": "autoskill",
    "map_sprint": True,
    "map_sprint_scale": 8,
    "battle_longpress": True,
    "transition_speed": True,
    "transition_speed_scale": 0.2,
    "skill_effect_speed": True,
    "skill_effect_scale": 2.0,
    "pet_equip_unlock": False,
    "inject_bridge": False,
    "from_orig": True,
}

# 傻瓜补丁 = 与 GUI 默认勾选一致；九动由运行时在 IL/DLL 间择优（默认倾向 IL）
FOOLPROOF_COMBO_KWARGS = {
    **DEFAULT_COMBO_KWARGS,
    "battle_nine_action": False,
    "battle_nine_external": False,
}

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
    "transition_speed": False,
    "transition_speed_scale": 0.2,
    "skill_effect_speed": True,
    "skill_effect_scale": 2.0,
    "pet_equip_unlock": False,
    "from_orig": True,
}
