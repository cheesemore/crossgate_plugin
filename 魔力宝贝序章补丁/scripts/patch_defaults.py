#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""GUI / 简单补丁 / 傻瓜补丁共用的默认组合选项。"""

# 助手面板（百科入口）+ 抓宠/烧卡 DLL 面板模式：玩法开关在面板里切
# 默认组合：不勾加速（vip / vip_non_vip / 心跳回传均关）。
#   原因：战斗倍速补丁默认连带掐断倍速检测上报（CheckTimeScaleWarning /
#   SendTimeScaleWarning 打成空方法，防检测）；用户明确默认不打加速。
#   MAC 伪装（假设备指纹）默认不开（仅 --fake-mac 显式开启）。
DEFAULT_COMBO_KWARGS = {
    "vip": False,  # 默认不打战斗倍速（加速关闭）
    "vip_non_vip": False,  # 默认不启用非VIP倍速
    "vip_scale": 5,  # 仅勾选倍速时使用
    "battle_nine_action": False,
    "battle_nine_external": False,  # 九动已停发，固定 False
    "auto_seal_external": True,
    "auto_catch_external": True,
    "auto_catch_nopet_external": True,  # 抓宠（无宠二动）：不带宠时第二动防御
    "auto_catch_sell_external": True,
    "count_farm": True,  # 计数挂机：面板战斗页互斥切换（标题 ★挂机中★ 魔石进度）
    "area_extract": True,  # 采集自动提取：面板战斗页独立开关（单格满999提取，与战斗模式共存）
    "auto_point": True,  # 一键加点：面板脚本页按钮（人物按推荐第一方案，宠物先加力量到极限）
    "auto_stall_external": True,  # 自动上架：面板脚本页「一键上架」按钮（只上架默认定价表单内装备）
    "bear_slayer_external": True,  # 刷熊男：面板脚本页「刷熊男」按钮（等杀熊者→丢欧兹那克→穿身触发战斗→循环）
    "lv1_auto_external": False,
    "auto_sell_external": False,
    "customer_gm": True,
    "customer_gm_mode": "autoskill",
    "map_sprint": False,  # 地图跑速默认关（属加速类）
    "map_sprint_scale": 8,
    "battle_longpress": True,
    "level_one_include_all": True,
    "transition_speed": False,
    "transition_speed_scale": 0.4,
    "skill_effect_speed": False,  # 技能特效加速默认关（属加速类）
    "skill_effect_scale": 2.0,
    "pet_equip_unlock": False,
    "wiki_download_res": False,
    "wiki_label": False,
    "daily_claim": True,
    "newbie_gift_code": True,
    "boss_key_fps": True,  # 切后台 / 老板键隐藏 → 30 FPS
    "wiki_fps": False,
    "wiki_test_ui": True,  # 百科 → 助手面板
    "battle_appear": False,  # 进战形象钩子：总是部署（游戏内可开），勾选=打补丁后默认开启形象
    "kill_timescale_report": True,  # 默认拦截倍速检测上报（即使加速关也掐断 Check/SendTimeScaleWarning）
    "inject_bridge": False,  # 注入精简多开桥接（登录/拉多控/一键召唤，供新序章多开器直接驱动）
    "from_orig": True,
}

# 傻瓜补丁·基底：助手面板 + 抓宠/烧卡 DLL（面板切换）。
# 融合版默认：面板含「抓宠（无宠二动）」+ 普通抓宠；九动已停发（battle_nine_external 固定 False）。
# 皮肤相关默认关闭：battle_appear（进战形象钩子/皮肤挂钩）为 False；
#   换装循环（SeqChapterWikiSkinCycle）融合版不打（傻瓜换装补丁才打）。
# 加速默认关闭（2026-08 起）：战斗倍速补丁默认连带掐断倍速检测上报
#   （CheckTimeScaleWarning / SendTimeScaleWarning 空方法），默认不打。
#   GUI 勾选「战斗加速」时才置 vip=True + vip_echo=1.5。
FOOLPROOF_COMBO_KWARGS = {
    **DEFAULT_COMBO_KWARGS,
    "battle_nine_action": False,
    "battle_nine_external": False,  # 九动版已停发，固定 False
    "auto_seal_external": True,
    "auto_catch_external": True,
    "auto_catch_nopet_external": True,
    "auto_catch_sell_external": True,
    "wiki_test_ui": True,
    "boss_key_fps": True,
    "transition_speed": False,
    "battle_appear": False,  # 皮肤挂钩（进战形象钩子）总是部署，默认不套形象；游戏内可开
    "vip_scale": 3,  # 仅在勾选战斗加速时生效（vip=True，vip_echo 由 foolproof_apply 设置）
    "map_sprint": False,  # 移动加速默认关
}

FOOLPROOF_NO_NINE_COMBO_KWARGS = {
    **FOOLPROOF_COMBO_KWARGS,
    # 融合版默认：三种抓宠方案（普通抓宠 / 抓宠不带宠 / 抓宠卖银币）同时部署，
    # 由助手面板 SetEnabled 运行时互斥切换（战斗分发钩 卖银→无宠→普通）。
    "auto_catch_external": True,
    "auto_catch_nopet_external": True,
    "auto_catch_sell_external": True,
}

# 傻瓜「烧卡档」：同面板双 DLL（抓宠+烧卡），倍速/特效取最高；无九动
FOOLPROOF_BURN_SEAL_COMBO_KWARGS = {
    **FOOLPROOF_NO_NINE_COMBO_KWARGS,
    "auto_seal_external": True,
    "auto_catch_external": True,
    "auto_catch_nopet_external": False,  # 烧卡档用普通抓宠，关掉抓宠（不带宠）
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
    "vip": False,  # 默认不打战斗倍速（加速默认关闭）
    "vip_non_vip": False,
    "vip_scale": 5,
    "battle_nine_action": False,
    "battle_nine_external": False,
    "auto_seal_external": True,
    "auto_catch_external": True,
    "customer_gm": True,
    "customer_gm_mode": "autoskill",
    "map_sprint": False,  # 地图跑速默认关
    "map_sprint_scale": 8,
    "battle_longpress": True,
    "level_one_include_all": True,
    "transition_speed": False,
    "transition_speed_scale": 0.4,
    "skill_effect_speed": False,  # 技能特效加速默认关
    "skill_effect_scale": 2.0,
    "pet_equip_unlock": False,
    "wiki_download_res": False,
    "wiki_label": False,
    "daily_claim": True,
    "newbie_gift_code": True,
    "boss_key_fps": True,  # 切后台 / 老板键隐藏 → 30 FPS
    "wiki_fps": False,
    "wiki_test_ui": True,
    "battle_appear": False,
    "from_orig": True,
}
