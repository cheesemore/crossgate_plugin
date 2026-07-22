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
    "transition_speed_scale": 0.4,
    "skill_effect_speed": True,
    "skill_effect_scale": 2.0,
    "pet_equip_unlock": False,
    "inject_bridge": False,
    "from_orig": True,
}

# 傻瓜补丁：有九动（运行时 IL/DLL 择优）、无加速过场
FOOLPROOF_COMBO_KWARGS = {
    **DEFAULT_COMBO_KWARGS,
    "battle_nine_action": False,
    "battle_nine_external": False,
    "transition_speed": False,
}

# 无九动傻瓜包：其余与傻瓜补丁相同（仍无加速过场）
FOOLPROOF_NO_NINE_COMBO_KWARGS = {
    **FOOLPROOF_COMBO_KWARGS,
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
    "transition_speed_scale": 0.4,
    "skill_effect_speed": True,
    "skill_effect_scale": 2.0,
    "pet_equip_unlock": False,
    "from_orig": True,
}
