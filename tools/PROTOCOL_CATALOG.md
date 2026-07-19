# 魔力宝贝：序章 — 客户端发送协议完整目录

> 自动生成：`tools/extract_protocol_catalog.py`
> 数据源：`tools/hotfix_ilspy/hotfix.dll.bytes.decompiled.cs`

## 统计

| 项 | 数量 |
|----|-----:|
| protocol_count | 229 |
| client_send_count | 159 |
| proto_class_count | 171 |
| type_literal_count | 353 |
| send_message_calls | 245 |
| send_wrappers | 209 |

## 发包 API

```csharp
Manager<NetManager>.Instance.SendMessage(LSSPROTO opcode, IMessage proto);
// GM: LSSPROTO_TALKEX_FUNC(1000), Channel=PROTO_CHANNEL_TYPE_GM, Msg=命令文本
```

助手 IPC：`序章助手共享/assistant_common/ipc.py` → `send_proto(opcode, proto_type, fields, uid?)`

## 全量协议表（229）

| 编号 | 协议 | 发包 | 次数 | Proto_CS | Type 数量 |
|-----:|------|:----:|-----:|----------|----------:|
| 0 | `LSSPROTO_WALK_FUNC` | Y | 1 | Proto_CS_Walk | — |
| 1 | `LSSPROTO_MAP_CHECK_FUNC` | — | — | — | — |
| 2 | `LSSPROTO_MAP_EVENT_FUNC` | Y | 1 | Proto_CS_MapEvent | — |
| 3 | `LSSPROTO_MAP_FUNC` | Y | 1 | Proto_CS_MapInfo | — |
| 4 | `LSSPROTO_CREATE_BATTLE_FUNC` | — | — | — | — |
| 5 | `LSSPROTO_BATTLE_REWARD_FUNC` | — | — | — | — |
| 6 | `LSSPROTO_BATTLE_PVP_REWARD_FUNC` | — | — | — | — |
| 7 | `LSSPROTO_CREATE_BATTLE_PVP_FUNC` | Y | 1 | Proto_CS_CreatePvp | — |
| 8 | `LSSPROTO_ENVIR_ONMENT_FUNC` | Y | 1 | Proto_CS_EnvirOnment | — |
| 9 | `LSSPROTO_LOOK_BATTLE_FUNC` | Y | 1 | Proto_CS_LookBattle | — |
| 10 | `LSSPROTO_BATTLE_COMMAND_FUNC` | Y | 1 | Proto_CS_BattleCommand | 10 |
| 11 | `LSSPROTO_MISSIONEX_FUNC` | — | — | — | — |
| 12 | `LSSPROTO_ITEM_FUNC` | — | — | — | — |
| 13 | `LSSPROTO_SERVER_INFO_FUNC` | — | — | — | — |
| 14 | `LSSPROTO_ITEM_MOVE_FUNC` | — | — | — | — |
| 16 | `LSSPROTO_USE_ITEM_FUNC` | Y | 1 | Proto_CS_UseItem | — |
| 17 | `LSSPROTO_PICKUP_ITEM_FUNC` | Y | 1 | Proto_CS_PickupItem | — |
| 18 | `LSSPROTO_DROP_ITEM_FUNC` | Y | 1 | Proto_CS_DropItem | — |
| 19 | `LSSPROTO_DROP_GOLD_FUNC` | — | — | — | — |
| 20 | `LSSPROTO_DROP_PET_FUNC` | Y | 1 | Proto_CS_DropPet | — |
| 21 | `LSSPROTO_MOVE_ITEM_FUNC` | Y | 1 | Proto_CS_MoveItem | — |
| 22 | `LSSPROTO_ITEM_RECIPE_FUNC` | Y | 1 | Proto_CS_ItemRecipe | — |
| 24 | `LSSPROTO_TALK_FUNC` | Y | 1 | Proto_CS_Talk | — |
| 25 | `LSSPROTO_TOOL_TIP_FUNC` | — | — | — | — |
| 26 | `LSSPROTO_UPDATE_PLAYER_FUNC` | Y | 1 | Proto_CS_UpdatePlayer | — |
| 27 | `LSSPROTO_UPDATE_PET_FUNC` | — | — | — | — |
| 29 | `LSSPROTO_UPDATE_OBJ_FUNC` | Y | 1 | Proto_CS_UpdateObj | — |
| 30 | `LSSPROTO_UPDATE_TEAM_FUNC` | — | — | — | — |
| 31 | `LSSPROTO_OBJ_ADDITIONAL_FUNC` | — | — | — | — |
| 32 | `LSSPROTO_CLEAR_OBJ_FUNC` | — | — | — | — |
| 34 | `LSSPROTO_PLAYER_MAGIC_FUNC` | — | — | — | — |
| 35 | `LSSPROTO_UPDATE_TECH_FUNC` | — | — | — | — |
| 36 | `LSSPROTO_PET_TECH_FUNC` | — | — | — | — |
| 37 | `LSSPROTO_CONFIG_FUNC` | Y | 1 | Proto_CS_Config | 1 |
| 38 | `LSSPROTO_OPERATION_TEAM_FUNC` | — | — | — | — |
| 39 | `LSSPROTO_OPERATION_PET_FUNC` | Y | 1 | Proto_CS_OperationPet | — |
| 40 | `LSSPROTO_FRONT_CHAR_LIST_FUNC` | — | — | — | — |
| 41 | `LSSPROTO_ACTION_FUNC` | Y | 1 | Proto_CS_Action | — |
| 42 | `LSSPROTO_ACTION_SKILL_FUNC` | Y | 1 | Proto_CS_ActionSkill | — |
| 43 | `LSSPROTO_USE_TECH_FUNC` | Y | 1 | Proto_CS_UseTech | — |
| 45 | `LSSPROTO_SKILL_EXCHANGE_FUNC` | Y | 1 | Proto_CS_SkillExchange | — |
| 46 | `LSSPROTO_POS_FUNC` | Y | 1 | Proto_CS_Pos | — |
| 47 | `LSSPROTO_CHANGE_PET_NAME_FUNC` | Y | 1 | Proto_CS_ChangePetName | — |
| 48 | `LSSPROTO_WINDOWS_FUNC` | Y | 3 | Proto_CS_Windows | — |
| 49 | `LSSPROTO_MAP_EFFECT_FUNC` | — | — | — | — |
| 50 | `LSSPROTO_PALETTE_FUNC` | — | — | — | — |
| 51 | `LSSPROTO_PLAYSE_FUNC` | — | — | — | — |
| 52 | `LSSPROTO_MAP_NAME_FUNC` | — | — | — | — |
| 53 | `LSSPROTO_DUNGEON_FUNC` | — | — | — | — |
| 54 | `LSSPROTO_LOGIN_FUNC` | Y | 1 | Proto_CS_Login | — |
| 55 | `LSSPROTO_CREATECHAR_FUNC` | Y | 1 | Proto_CS_Createchar | — |
| 56 | `LSSPROTO_CHARLOGIN_FUNC` | Y | 1 | Proto_CS_Charlogin | — |
| 57 | `LSSPROTO_CHARLOGOUT_FUNC` | Y | 1 | — | — |
| 59 | `LSSPROTO_ECHO_FUNC` | — | — | — | — |
| 60 | `LSSPROTO_LOGINGATE_FUNC` | Y | 1 | — | — |
| 61 | `LSSPROTO_LOOK_NPC_FUNC` | Y | 1 | Proto_CS_LookNpc | — |
| 62 | `LSSPROTO_NEWMESSAGE_FUNC` | — | — | — | — |
| 63 | `LSSPROTO_RETURNBATTLE_FUNC` | Y | 1 | — | — |
| 64 | `LSSPROTO_MENU_FUNC` | Y | 1 | Proto_CS_Menu | — |
| 65 | `LSSPROTO_RUN_FUNC` | Y | 1 | Proto_CS_Run | — |
| 66 | `LSSPROTO_RED_DOT_FUNC` | — | — | — | — |
| 67 | `LSSPROTO_SELECTPLAYERNO_FUNC` | — | — | — | — |
| 68 | `LSSPROTO_VERIFY_FUNC` | Y | 1 | Proto_CS_Verify | — |
| 69 | `LSSPROTO_BATTLE_COMMAND_PLAYER_FUNC` | — | — | — | — |
| 70 | `LSSPROTO_BATTLE_COMMAND_CHAR_FUNC` | — | — | — | — |
| 71 | `LSSPROTO_BATTLE_COMMAND_STATUS_FUNC` | — | — | — | — |
| 72 | `LSSPROTO_BATTLE_COMMAND_EXIT_FUNC` | — | — | — | — |
| 73 | `LSSPROTO_BATTLE_COMMAND_ACTION_FUNC` | — | — | — | — |
| 74 | `LSSPROTO_BATTLE_CONFIG_FUNC` | Y | 2 | Proto_CS_AutoBattleConfig | 2 |
| 75 | `LSSPROTO_BATTLE_END_FUNC` | — | — | — | — |
| 76 | `LSSPROTO_DELAY_TEST_FUNC` | Y | 1 | — | — |
| 77 | `LSSPROTO_DELAY_TEST_GATE_FUNC` | — | — | — | — |
| 78 | `LSSPROTO_MISC_FUNC` | Y | 1 | Proto_CS_Misc | 1 |
| 1000 | `LSSPROTO_TALKEX_FUNC` | Y | 4 | — | — |
| 1001 | `LSSPROTO_FRIEND_FUNC` | Y | 1 | Proto_CS_Friend | — |
| 1002 | `LSSPROTO_FRIEND_CHANGE_OR_ADD_FUNC` | — | — | — | — |
| 1003 | `LSSPROTO_FRIEND_DEL_FUNC` | — | — | — | — |
| 1004 | `LSSPROTO_FRIEND_BLACK_DEL_FUNC` | — | — | — | — |
| 1005 | `LSSPROTO_MAIL_FUNC` | Y | 1 | Proto_CS_Mail | 6 |
| 1006 | `LSSPROTO_MAIL_CHANGE_OR_ADD_FUNC` | — | — | — | — |
| 1007 | `LSSPROTO_MAIL_OPERATION_FUNC` | — | — | — | — |
| 1008 | `LSSPROTO_AREA_FUNC` | Y | 1 | Proto_CS_Area | 5 |
| 1009 | `LSSPROTO_RESET_TASK_FUNC` | Y | 1 | — | — |
| 1010 | `LSSPROTO_RECIPE_FUNC` | Y | 1 | Proto_CS_Recipe | 3 |
| 1011 | `LSSPROTO_PRODUCTION_FUNC` | Y | 1 | Proto_CS_Production | 1 |
| 1012 | `LSSPROTO_TEAM_FUNC` | Y | 3 | Proto_CS_Team | 8 |
| 1013 | `LSSPROTO_STREET_FUNC` | Y | 11 | Proto_CS_Street | 1 |
| 1014 | `LSSPROTO_FRAME_FUNC` | Y | 1 | Proto_CS_Frame | 31 |
| 1015 | `LSSPROTO_PETBOOK_FUNC` | Y | 1 | Proto_CS_PetBook | — |
| 1016 | `LSSPROTO_TRADE_FUNC` | Y | 7 | Proto_CS_Trade, Proto_CS_Trade2 | 1 |
| 1017 | `LSSPROTO_OFFLINECOMMAND_FUNC` | Y | 1 | Proto_CS_OfflineCommand | 1 |
| 1018 | `LSSPROTO_TITLEEX_FUNC` | Y | 1 | Proto_CS_TitleEx | 4 |
| 1019 | `LSSPROTO_HPFP_FUNC` | Y | 3 | Proto_CS_HpFp | 10 |
| 1020 | `LSSPROTO_PLAYERBUFF_FUNC` | Y | 1 | Proto_CS_PlayerBuff | — |
| 1021 | `LSSPROTO_ACCOMPANY_FUNC` | Y | 1 | Proto_CS_Accompany | 3 |
| 1022 | `LSSPROTO_ALLTALK_FUNC` | Y | 1 | Proto_CS_AllTalk | 1 |
| 1023 | `LSSPROTO_SELECTOBJ_FUNC` | Y | 5 | Proto_CS_SelectObj | 2 |
| 1024 | `LSSPROTO_FAMILY_FUNC` | Y | 4 | Proto_CS_Family | 34 |
| 1025 | `LSSPROTO_VIGOR_FUNC` | Y | 2 | Proto_CS_Vigor | 1 |
| 1026 | `LSSPROTO_CRYSTAL_FUNC` | Y | 2 | Proto_CS_Crystal | 7 |
| 1027 | `LSSPROTO_DAILY_ACTIVITY_FUNC` | — | — | — | — |
| 1028 | `LSSPROTO_TRUST_FUNC` | Y | 1 | — | — |
| 1029 | `LSSPROTO_MULTI_FUNC` | Y | 2 | Proto_CS_Multi | 7 |
| 1030 | `LSSPROTO_TEAMCONFIG_FUNC` | Y | 2 | Proto_CS_TeamConfig | 1 |
| 1031 | `LSSPROTO_MESSAGE_CODE_FUNC` | Y | 1 | Proto_CS_MessageCode | — |
| 1032 | `LSSPROTO_NEARBY_FUNC` | Y | 1 | Proto_CS_Nearby | 1 |
| 1033 | `LSSPROTO_CHARACTER_CHANGE_FUNC` | Y | 1 | Proto_CS_ChangeCharacter | 2 |
| 1034 | `LSSPROTO_OTHER_CODE_FUNC` | — | — | — | — |
| 1035 | `LSSPROTO_CUSTOMER_FUNC` | Y | 1 | Proto_CS_Customer | 1 |
| 1036 | `LSSPROTO_LUCK_FUNC` | Y | 1 | Proto_CS_Luck | 1 |
| 1037 | `LSSPROTO_TASK_EXPAND_FUNC` | — | — | — | — |
| 1038 | `LSSPROTO_SHOP_FUNC` | Y | 2 | Proto_CS_Shop | 3 |
| 1039 | `LSSPROTO_BATTLE_LOOK_PLAYER_FUNC` | — | — | — | — |
| 1040 | `LSSPROTO_WATCH_BATTLE_FUNC` | Y | 2 | Proto_CS_WatchBattle | 3 |
| 1041 | `LSSPROTO_DAILY_EXPAND_FUNC` | Y | 1 | Proto_CS_DailyExpand | 2 |
| 1042 | `LSSPROTO_PLAYER_SKILLS_FUNC` | Y | 1 | Proto_CS_PlayerSkills | 1 |
| 1043 | `LSSPROTO_PK_FUNC` | Y | 1 | Proto_CS_PK | 1 |
| 1044 | `LSSPROTO_WATCH_FUNC` | Y | 1 | Proto_CS_Watch | 2 |
| 1045 | `LSSPROTO_QUERCHARACTER_FUNC` | Y | 1 | — | — |
| 1046 | `LSSPROTO_PLAYER_CONFIG_FUNC` | Y | 2 | — | — |
| 1047 | `LSSPROTO_ACTIVITY_FUNC` | Y | 10 | Proto_CS_Activity | 26 |
| 1048 | `LSSPROTO_PLAYER_POING_FUNC` | Y | 9 | Proto_CS_PlayerPoint | 8 |
| 1049 | `LSSPROTO_MESSAGEBOX_FUNC` | Y | 1 | Proto_CS_MessageBox | — |
| 1050 | `LSSPROTO_PET_RESET_FUNC` | Y | 2 | Proto_CS_PetReset | — |
| 1051 | `LSSPROTO_PET_POINT_FUNC` | Y | 2 | Proto_CS_PetPoint | 2 |
| 1052 | `LSSPROTO_DAILY_ACTIVITY_EX_FUNC` | Y | 1 | Proto_CS_DailyActivityEx | — |
| 1053 | `LSSPROTO_ITEM_USE_EFFECT_FUNC` | — | — | — | — |
| 2000 | `LSSPROTO_SKILL_FUNC` | Y | 2 | Proto_CS_Skill | — |
| 2001 | `LSSPROTO_BACKPACK_FUNC` | Y | 4 | Proto_CS_Backpack | 7 |
| 2002 | `LSSPROTO_BANK_FUNC` | Y | 1 | Proto_CS_Bank | — |
| 2003 | `LSSPROTO_ACCOUNT_BANK_FUNC` | Y | 2 | Proto_CS_Bank | — |
| 2004 | `LSSPROTO_BATTLE_SPEED_FUNC` | Y | 1 | Proto_CS_BattleSpeed | 2 |
| 3001 | `LSSPROTO_AUCTIONHOUSE_FUNC` | Y | 2 | Proto_CS_Auction | 2 |
| 3002 | `LSSPROTO_RED_POINT_FUNC` | — | — | — | — |
| 3003 | `LSSPROTO_ITEM_LIST_FUNC` | Y | 3 | Proto_CS_ItemList | 3 |
| 3004 | `LSSPROTO_FIX_POS_FUNC` | — | — | — | — |
| 3006 | `LSSPROTO_LOCK_FUNC` | Y | 2 | Proto_CS_Lock | — |
| 3007 | `LSSPROTO_FIX_DUNGEON_FUNC` | Y | 1 | Proto_CS_PlayerFixDungeon | — |
| 3008 | `LSSPROTO_MONSTER_BREED_FUNC` | Y | 1 | Proto_CS_MonsterBreed | 1 |
| 3009 | `LSSPROTO_PET_UPSTAR_FUNC` | Y | 2 | Proto_CS_PetUpStar | 1 |
| 3011 | `LSSPROTO_LAYERBOX_FUNC` | Y | 1 | Proto_CS_LayerBox | 1 |
| 3012 | `LSSPROTO_REPAIRBATTLE_FUNC` | Y | 1 | Proto_CS_RepairBattle | — |
| 3013 | `LSSPROTO_CLIENTLIMIT_FUNC` | Y | 1 | Proto_CS_ClientLimit | — |
| 3014 | `LSSPROTO_EASTEREGG_FUNC` | Y | 1 | Proto_CS_EasterEgg | — |
| 3015 | `LSSPROTO_AUTOBATTLE_FUNC` | Y | 1 | Proto_CS_AutoBattle | 6 |
| 3016 | `LSSPROTO_JOBSWITCH_FUNC` | Y | 3 | Proto_CS_JobSwitch | 5 |
| 3017 | `LSSPROTO_FLASHSALE_FUNC` | Y | 1 | Proto_CS_FlashSale | 1 |
| 3018 | `LSSPROTO_PETBOND_FUNC` | Y | 1 | Proto_CS_PetBond | — |
| 3019 | `LSSPROTO_FLASHLIMIT_FUNC` | Y | 1 | Proto_CS_FlashSale | 1 |
| 3020 | `LSSPROTO_SELECT_PET_WINDOWS_FUNC` | Y | 1 | — | — |
| 3021 | `LSSPROTO_CODE_PET_WINDOWS_FUNC` | Y | 1 | Proto_CS_PetCodeWindows | — |
| 3022 | `LSSPROTO_FIVE_CHAR_DRAW_FUNC` | Y | 1 | Proto_CS_FiveCharDraw | 2 |
| 3023 | `LSSPROTO_PET_EQUIP_FUNC` | Y | 1 | Proto_CS_PetEquip | 4 |
| 3024 | `LSSPROTO_EQUIP_FORCE_FUNC` | — | — | — | — |
| 3025 | `LSSPROTO_DUR_REPAIRE_FUNC` | Y | 1 | Proto_CS_DurRepaire | 4 |
| 3026 | `LSSPROTO_FAMILY_BATTLE_FUNC` | Y | 1 | Proto_CS_FamilyBattle | 6 |
| 3027 | `LSSPROTO_CHOOSE_PET_FUNC` | Y | 1 | Proto_CS_Choose_Pet | 1 |
| 3028 | `LSSPROTO_BLINDBOX_FUNC` | Y | 1 | Proto_CS_BlindBox | 8 |
| 3029 | `LSSPROTO_PET_RIDE_FUNC` | Y | 1 | Proto_CS_Pet_Ride | 1 |
| 3030 | `LSSPROTO_OTHER_EQUIP_FUNC` | Y | 1 | Proto_CS_OtherEquip | 3 |
| 3031 | `LSSPROTO_PET_RESETBASE_FUNC` | Y | 1 | Proto_CS_PetResetBase | 3 |
| 3032 | `LSSPROTO_PRAY_FUNC` | Y | 1 | Proto_CS_Pray | 2 |
| 3033 | `LSSPROTO_MONSTERTOWER_FUNC` | Y | 1 | Proto_CS_MonsterTower | 1 |
| 3034 | `LSSPROTO_CURRENCY_EXCHANGE_FUNC` | Y | 1 | Proto_CS_CurrencyExchange | — |
| 3035 | `LSSPROTO_MAP_UPDATE_FUNC` | — | — | — | — |
| 3036 | `LSSPROTO_ONLINE_INFO_FUNC` | Y | 1 | Proto_CS_OnLineInfo | — |
| 3037 | `LSSPROTO_TASK_WARP_FUNC` | Y | 1 | Proto_CS_TaskWarp | — |
| 3038 | `LSSPROTO_FLURRYPK_BATTLE_FUNC` | Y | 1 | Proto_CS_Flurry | 1 |
| 3039 | `LSSPROTO_FLURRYPK_RANK` | — | — | — | — |
| 3040 | `LSSPROTO_SET_SPECIAL_FLAG` | Y | 1 | Proto_CS_SetSpecialFlag | — |
| 3041 | `LSSPROTO_USE_CURRENCY_TIP` | Y | 1 | Proto_CS_UseCurrencyTip | — |
| 3042 | `LSSPROTO_SKIN_LEVELUP` | Y | 1 | Proto_CS_SkinLevelUp | 1 |
| 3043 | `LSSPROTO_CRYSTAL_HOUSE_FUNC` | Y | 1 | Proto_CS_CrystalHouse | 4 |
| 3044 | `LSSPROTO_SOLDIER_OF_HONOR_FUNC` | Y | 1 | Proto_CS_SoldierOfHonor | 1 |
| 3045 | `LSSPROTO_BOSS_SUPPRESS_TOKEN_FUNC` | Y | 1 | Proto_CS_BossSuppressToken | — |
| 3046 | `LSSPROTO_EARCH_MOUSE_LOTTERY_FUNC` | Y | 1 | Proto_CS_EarthMouseLottery | 3 |
| 3047 | `LSSPROTO_HERO_TRIALS_FUNC` | Y | 1 | Proto_CS_HeroTrials | 2 |
| 3048 | `LSSPROTO_WORLD_BOSS_FUNC` | — | — | — | — |
| 3049 | `LSSPROTO_BLIND_BOX_NEW1_FUNC` | Y | 1 | Proto_CS_BlindBoxNew | 6 |
| 3050 | `LSSPROTO_BOSS_LAND_CHALLENGE_FUNC` | Y | 4 | Proto_CS_BossLandChallenge | 5 |
| 3051 | `LSSPROTO_ENCHANT_FUNCTION` | Y | 1 | Proto_CS_EnchantFunction | 4 |
| 4000 | `LSSPROTO_PLAYER_FUNC` | Y | 1 | Proto_CS_PlayerMgr | — |
| 4001 | `LSSPROTO_RANKLIST_FUNC` | Y | 1 | — | — |
| 4002 | `LSSPROTO_PET_FUNC` | — | — | — | — |
| 4003 | `LSSPROTO_LEVEL_FUNC` | — | — | — | — |
| 4004 | `LSSPROTO_KICK_OUT` | — | — | — | — |
| 4005 | `LSSPROTO_PAY_LUA` | Y | 4 | Proto_CS_PayLua | 4 |
| 4006 | `LSSPROTO_RISK_CONTROL` | Y | 1 | Proto_CS_RiskControl | — |
| 4007 | `LSSPROTO_PLAYER_EFFECT` | — | — | — | — |
| 4008 | `LSSPROTO_NEWCOMER_7DAY` | Y | 1 | Proto_CS_Activity | — |
| 4009 | `LSSPROTO_DUNGEON_ENTER` | Y | 1 | Proto_CS_DungeonEnter | 1 |
| 4010 | `LSSPROTO_PET_SACRIFICE` | Y | 1 | Proto_CS_PetSacrifice | 1 |
| 4011 | `LSSPROTO_BOX_FRUITY` | Y | 1 | Proto_CS_FruityBox | — |
| 4012 | `LSSPROTO_GEM_FUNCTION` | Y | 1 | Proto_CS_GemFunction | 1 |
| 4013 | `LSSPROTO_SELECT_BOSS_BOX` | Y | 1 | Proto_CS_SelectBossBox | 1 |
| 4014 | `LSSPROTO_FAMILY_TRIAL` | Y | 1 | Proto_CS_FamilyTrial | — |
| 4015 | `LSSPROTO_FAMILY_BRAWL` | Y | 1 | Proto_CS_FamilyBrawl | — |
| 4016 | `LSSPROTO_FAMILY_REDPACK` | Y | 1 | Proto_CS_FamilyRedPacket | 1 |
| 4017 | `LSSPROTO_NEWYEARSDAY2025` | — | — | — | — |
| 4018 | `LSSPROTO_BATTLE_UPLOAD` | — | — | — | — |
| 4019 | `LSSPROTO_LOGIN_SWITCH` | — | — | — | — |
| 4020 | `LSSPROTO_BRAWL_RANK` | — | — | — | — |
| 4021 | `LSSPROTO_BROADCAST_ROOM` | — | — | — | — |
| 4022 | `LSSPROTO_LEGACYOFTHELOST` | Y | 1 | Proto_CS_LegacyOfTheLost | — |
| 4023 | `LSSPROTO_TRADE_BANK` | Y | 7 | Proto_CS_TradeBank | 14 |
| 4024 | `LSSPROTO_LUCK_ROCKETS` | Y | 1 | Proto_CS_LuckyRockets | — |
| 4025 | `LSSPROTO_PLAYER_ACOUSTIC` | Y | 1 | Proto_CS_PlayerAcoustic | — |
| 4026 | `LSSPROTO_PET_POTENTIAL` | — | — | — | — |
| 4027 | `LSSPROTO_SHOVEL_TREASURE` | — | — | — | — |
| 4028 | `LSSPROTO_OFFLINE_TRADE` | Y | 2 | Proto_CS_OfflineTrade | 9 |
| 4029 | `LSSPROTO_HUNDRED_LAYER_BATTLE` | Y | 1 | Proto_CS_HundredLayerBattle | 7 |
| 4030 | `LSSPROTO_PET_MAX_CREST_EFFECT` | Y | 1 | Proto_CS_PetMaxCrestEffect | — |
| 4031 | `LSSPROTO_LOOPY_TRIAL_FUNC` | Y | 1 | Proto_CS_LoopyTrial | 4 |
| 4032 | `LSSPROTO_PET_EXCHANGE_OR_MERGE` | Y | 1 | Proto_CS_PetExchangeOrMerge | — |
| 4033 | `LSSPROTO_JEWELRY_MERGE_FUNC` | Y | 1 | Proto_CS_JewelryMerge | 2 |
| 4034 | `LSSPROTO_PET_REFORM_MUTATION_FUNC` | Y | 1 | Proto_CS_ReformMutation | 2 |
| 4035 | `LSSPROTO_MONTH_CARD_PRIVILEGE_FUNC` | Y | 2 | Proto_CS_MonthCard | 3 |
| 4036 | `LSSPROTO_BATTLE_PASS_ADVENTURE` | Y | 1 | Proto_CS_BattlePassAdventure | 5 |
| 4037 | `LSSPROTO_PET_REFACTOR_DESTRUCT` | — | — | — | — |
| 4038 | `LSSPROTO_PET_RECYCLE_EXCHANGE` | Y | 1 | Proto_CS_PetRecycleExchange | 3 |
| 4039 | `LSSPROTO_DAILY_PASS_FUNC` | Y | 1 | Proto_CS_DailyPass | 4 |
| 4040 | `LSSPROTO_SKY_DORP_RED_PACK` | Y | 2 | Proto_CS_SkyDropRedPack | — |
| 10000000 | `SERVER_HEARTBEAT` | — | — | — | — |
| 10000001 | `SERVER_OTHERMSG` | — | — | — | — |
| 10000002 | `SERVER_ENCRYPTIONDATA` | — | — | — | — |
| 10000003 | `SERVER_BROADCASTENCRYPTIONDATA` | — | — | — | — |
| 10000004 | `SERVER_FD_CLOSE` | — | — | — | — |
| 10000005 | `SERVER_DECRYPTIONDATA` | — | — | — | — |
| 10000006 | `SERVER_BROADCASTDECRYPTIONDATA` | — | — | — | — |

## 已发包协议 — 字段 / Type / 封装

### 0 `LSSPROTO_WALK_FUNC`

**Proto_CS_Walk**：`X, Y, Direction, Mapid, Floor, KUid`

**Send 封装：**
- `SendWalk(int x, int y, string direction)`
  - 字段赋值：X=x, Y=y, Direction=direction, Mapid=MonoSingleton<MapManager>.instance.currentMapID, Floor=MonoSingleton<MapManager>.instance.currentFloor

### 2 `LSSPROTO_MAP_EVENT_FUNC`

**Proto_CS_MapEvent**：`Event, Seqno, X, Y, Dir, Mapid, Floor, KUid, Result`

**Send 封装：**
- `SendMapEvent(MapEventType eventid, int seqno, int x, int y, int dir)`
  - 字段赋值：Event=(int)eventid, Seqno=seqno, X=x, Y=y, Dir=dir, Mapid=MonoSingleton<MapManager>.instance.currentMapID

### 3 `LSSPROTO_MAP_FUNC`

**Proto_CS_MapInfo**：`Id, Floor, X1, Y1, X2, Y2, KUid, Titles, Objs, Events`

**Send 封装：**
- `SendMap(int id, int floor, int x1, int y1, int x2, int y2)`
  - 字段赋值：Id=id, Floor=floor, X1=x1, Y1=y1, X2=x2, Y2=y2

### 7 `LSSPROTO_CREATE_BATTLE_PVP_FUNC`

**Proto_CS_CreatePvp**：`Str, KUid, Battleindex`

**Send 封装：**
- `SendCreatePvp(string uid)`
  - 字段赋值：Str=uid

### 8 `LSSPROTO_ENVIR_ONMENT_FUNC`

**Proto_CS_EnvirOnment**：`Battleindex, KUid, Uid`

**Send 封装：**
- `SendEnvirOnment(int battleindex)`
  - 字段赋值：Battleindex=battleindex

### 9 `LSSPROTO_LOOK_BATTLE_FUNC`

**Proto_CS_LookBattle**：`Uid, KUid, Str, Count, Isauto, BattleTime`

**Send 封装：**
- `SendLookBattle(string uid)`
  - 字段赋值：Uid=uid

### 10 `LSSPROTO_BATTLE_COMMAND_FUNC`

**Proto_CS_BattleCommand**：`Str, Count, Isauto, BattleTime, KUid, Index, BpFlg, Round, Uid, ItemLimit, PlayerSkillFlag, PetSkillFlag, TransformState, MaxRound, LoopyTrialLevel`

**Type / 操作名：**
- `E`
- `G`
- `H|`
- `M|FF`
- `N`
- `P`
- `S|`
- `U`
- `W|`
- `W|FF|FF`

**Send 封装：**
- `SendBattleCommond(string commond)`
  - 字段赋值：Str=commond, Count=((!FightProcessFlag.HasFlag(FightProcessFlag.PlayerActionEnd)) ? 1 : 2), KUid=BattleDataHolder.CurrentAccount, Isauto=IsAutoBattle, BattleTime=RoundRemainTime

### 16 `LSSPROTO_USE_ITEM_FUNC`

**Proto_CS_UseItem**：`X, Y, Haveitemindex, Toindex, Usecount, Selectindex, KUid, Objindex`

**Send 封装：**
- `SendUseItem(int x, int y, int haveitemindex, string uid, int toindex = 0, int selectIndex = -1, int useNum = 1)`
  - 字段赋值：X=x, Y=y, Haveitemindex=haveitemindex, Toindex=toindex, Selectindex=selectIndex, Usecount=useNum

### 17 `LSSPROTO_PICKUP_ITEM_FUNC`

**Proto_CS_PickupItem**：`Objindex, KUid, X, Y, Itemindex, Itemnum`

**Send 封装：**
- `SendItemPickup(int objindex)`
  - 字段赋值：Objindex=objindex

### 18 `LSSPROTO_DROP_ITEM_FUNC`

**Proto_CS_DropItem**：`X, Y, Itemindex, Itemnum, KUid, Amount`

**Send 封装：**
- `SendItemDrop(int x, int y, int itemindex, string uid)`
  - 字段赋值：X=x, Y=y, Itemindex=itemindex, KUid=uid

### 20 `LSSPROTO_DROP_PET_FUNC`

**Proto_CS_DropPet**：`X, Y, Petindex, KUid, Fromindex, Toindex`

**Send 封装：**
- `SendDropPet(string uid, int index)`
  - 字段赋值：KUid=uid, X=((Vector2Int)(ref location)).x, Y=((Vector2Int)(ref location)).y, Petindex=index

### 21 `LSSPROTO_MOVE_ITEM_FUNC`

**Proto_CS_MoveItem**：`Fromindex, Toindex, Num, KUid, Haveskillindex, Str`

**Send 封装：**
- `SendItemMove(int fromindex, int toindex, string uid, int num = -1)`
  - 字段赋值：Fromindex=fromindex, Toindex=toindex, Num=num, KUid=uid

### 22 `LSSPROTO_ITEM_RECIPE_FUNC`

**Proto_CS_ItemRecipe**：`Haveskillindex, KUid, Str, Index, Message`

**Send 封装：**
- `SendItemRecipe(int haveskillindex, string uid)`
  - 字段赋值：Haveskillindex=haveskillindex, KUid=uid

### 24 `LSSPROTO_TALK_FUNC`

**Proto_CS_Talk**：`Index, Message, KUid, Ctype, Num, Operation, Grano, Name, Id`

**Send 封装：**
- `SendTalk(string str, int objindex)`
  - 字段赋值：Index=objindex, Message=str

### 26 `LSSPROTO_UPDATE_PLAYER_FUNC`

**Proto_CS_UpdatePlayer**：`Id, KUid, Pet, Type, Index, Pets, Useflg`

**Send 封装：**
- `SendPlayerUpdate(int id)`
  - 字段赋值：Id=id

### 29 `LSSPROTO_UPDATE_OBJ_FUNC`

**Proto_CS_UpdateObj**：`Objindex, KUid, Number, Data, X, Y, Act, Dir, Effect, Effectarg1, Effectarg2, Effectarg3, Effectarg4, Effectarg5, Effectarg6, Effectarg7, Index, Use, Orderid, ActiveCamp`

**Send 封装：**
- `SendUpdateObj(int objindex)`
  - 字段赋值：KUid=PlayerDataHolder.MainPlayerUid, Objindex=objindex

### 37 `LSSPROTO_CONFIG_FUNC`

**Proto_CS_Config**：`Flg, KUid, X, Y, Request`

**Type / 操作名：**
- `组队设置`

**Send 封装：**
- `SendConfig(int flg, string uid)`
  - 字段赋值：Flg=flg, KUid=uid

### 39 `LSSPROTO_OPERATION_PET_FUNC`

**Proto_CS_OperationPet**：`Pet1, Pet2, Pet3, Pet4, Pet5, KUid, Index`

**Send 封装：**
- `SendPetOperation(string uid, int status1, int status2, int status3, int status4, int status5)`
  - 字段赋值：KUid=uid, Pet1=status1, Pet2=status2, Pet3=status3, Pet4=status4, Pet5=status5

### 41 `LSSPROTO_ACTION_FUNC`

**Proto_CS_Action**：`X, Y, Actionno, KUid, Skillno`

**Send 封装：**
- `SendAction(int x, int y, int action, string kuid = "")`
  - 字段赋值：X=x, Y=y, Actionno=action, KUid=(string.IsNullOrEmpty(kuid) ? PlayerDataHolder.MainPlayerUid : kuid)

### 42 `LSSPROTO_ACTION_SKILL_FUNC`

**Proto_CS_ActionSkill**：`X, Y, Skillno, KUid, Haveskillindex, Havetechindex, Toindex, Data`

**Send 封装：**
- `SendActionSkill(int x, int y, int skillno)`
  - 字段赋值：X=x, Y=y, Skillno=skillno

### 43 `LSSPROTO_USE_TECH_FUNC`

**Proto_CS_UseTech**：`Haveskillindex, Havetechindex, Toindex, Data, KUid, Result, Type`

**Send 封装：**
- `SendUseTech(int haveskillindex, int havetechindex, int toindex, string kuid)`
  - 字段赋值：Haveskillindex=haveskillindex, Havetechindex=havetechindex, Toindex=toindex, KUid=kuid

### 45 `LSSPROTO_SKILL_EXCHANGE_FUNC`

**Proto_CS_SkillExchange**：`Srcindex, Dstindex, KUid, Pos`

**Send 封装：**
- `SendSkillExchange(int fromindex, int toindex, string uid)`
  - 字段赋值：Srcindex=fromindex, Dstindex=toindex, KUid=uid

### 46 `LSSPROTO_POS_FUNC`

**Proto_CS_Pos**：`Pos, KUid, Havepetindex, Name`

**Send 封装：**
- `SendPos(int index, string KUid)`
  - 字段赋值：Pos=index, KUid=KUid

### 47 `LSSPROTO_CHANGE_PET_NAME_FUNC`

**Proto_CS_ChangePetName**：`Havepetindex, Name, KUid, Grano, Equips, Memo, Price, Flag, Pile, Type, Level, Durable, Itemid`

**Send 封装：**
- `SendChangePetName(string uid, int index, string name)`
  - 字段赋值：Havepetindex=index, Name=name, KUid=uid

### 48 `LSSPROTO_WINDOWS_FUNC`

**Proto_CS_Windows**：`X, Y, Seqno, Objindex, Select, Data, WindowsType, Curr, KUid, ButtonType, Name, AniId, Extensions`

**Send 封装：**
- `SendWindows(int x, int y, int seqno, int objindex, int select, string data, int windowstype, string uid, PROTO_CURRENCY curr = PROTO_CURRENCY.PROTO_CURRENCY_GOLD)`
  - 字段赋值：X=x, Y=y, Seqno=seqno, Objindex=objindex, Select=select, Data=data

### 54 `LSSPROTO_LOGIN_FUNC`

**Proto_CS_Login**：`Cdkey, Passwd, Version, Plat, DeviceId, Device, ClientIp, DeviceInfo, ServerId, DeviceName, KUid`

**Send 封装：**
- `SendLogin(string cdkey, string pwd, long version)`
  - 字段赋值：Cdkey=cdkey, Passwd=pwd, Version=version, Plat=SystemUtils.GetPlatform(), DeviceId=AppManager.GetMacAddress(), Device=AppManager.GetMacInfo()

### 55 `LSSPROTO_CREATECHAR_FUNC`

**Proto_CS_Createchar**：`Charname, Imgno, Vital, Str, Tgh, Quick, Magic, Earth, Water, Fire, Wind, Job`

**Send 封装：**
- `SendCreateSuccess(string name, int imgno, int vital, int str, int tgh, int quick, int magic, int earth, int water, int fire, int wind, int job)`
  - 字段赋值：Charname=name, Imgno=imgno, Vital=vital, Str=str, Tgh=tgh, Quick=quick

### 56 `LSSPROTO_CHARLOGIN_FUNC`

**Proto_CS_Charlogin**：`IsRelogin, BattleIndex, MapId, X, Y, KUid, Result, Data, GmsvId, Cdkey`

### 57 `LSSPROTO_CHARLOGOUT_FUNC`


**Send 封装：**
- `SendLogout()`

### 60 `LSSPROTO_LOGINGATE_FUNC`


**Send 封装：**
- `SendLoginGate()`

### 61 `LSSPROTO_LOOK_NPC_FUNC`

**Proto_CS_LookNpc**：`Dir, Objindex, KUid, Message`

**Send 封装：**
- `SendLookNpc(int dir, int objindex)`
  - 字段赋值：Dir=dir, Objindex=objindex

### 63 `LSSPROTO_RETURNBATTLE_FUNC`


**Send 封装：**
- `SendReturnBattle()`

### 64 `LSSPROTO_MENU_FUNC`

**Proto_CS_Menu**：`Func, Data, Callbackfunc, KUid, Index`

**Send 封装：**
- `SendMenu(int func, string data, string Kuid = "", string callfunc = "")`
  - 字段赋值：Func=func, Data=data, Callbackfunc=callfunc, KUid=Kuid

### 65 `LSSPROTO_RUN_FUNC`

**Proto_CS_Run**：`Flg, KUid, Id1, Id2`

**Send 封装：**
- `SendRun(int flg)`
  - 字段赋值：Flg=flg

### 68 `LSSPROTO_VERIFY_FUNC`

**Proto_CS_Verify**：`Flg, Str, KUid, GraNo, Name, Memo, Level`

**Send 封装：**
- `SendVerify(int flg, string str)`
  - 字段赋值：Flg=flg, Str=str

### 74 `LSSPROTO_BATTLE_CONFIG_FUNC`

**Proto_CS_AutoBattleConfig**：`Auto, Type, ActionFlgs, KUid, Autos, Stype`

**Type / 操作名：**
- `标记`
- `自动战斗`

**Send 封装：**
- `SendFocusAndGuardData(int targetIndex, int tagType)`
  - 字段赋值：KUid=PlayerDataHolder.MainPlayerUid
- `SendBattleConfig(string uid, int type, int index)` → Type=`type`
  - 字段赋值：KUid=uid, Auto=PetsAutoConfigs[uid][index], Index=index

### 76 `LSSPROTO_DELAY_TEST_FUNC`


### 78 `LSSPROTO_MISC_FUNC`

**Proto_CS_Misc**：`Type, Id, KUid, UseFlag, Index, ObjType, ObjIndex, Player, Hp, MaxHp, Mp, MaxMp, WalkSpeed, IsAfk`

**Type / 操作名：**
- `挂机传送`

**Send 封装：**
- `SendMisc(int id)`
  - 字段赋值：Id=id

### 1000 `LSSPROTO_TALKEX_FUNC`


**Send 封装：**
- `SendFriendHistory(ChatMessageInfo info)`
  - 字段赋值：Channel=PROTO_CHANNEL_TYPE.PROTO_CHANNEL_TYPE_FRIEND, Id=info.talk.KUid, Toid=FriendNowId
- `SendMiniChatHistory(ChatMessageInfo info, PROTO_CHANNEL_TYPE channel)`
  - 字段赋值：Channel=channel, Id=PlayerDataHolder.playerData.Uid

### 1001 `LSSPROTO_FRIEND_FUNC`

**Proto_CS_Friend**：`Type, Id, KUid, Friend, ItemId, Name, Pile, Num, Level, Max`

**Send 封装：**
- `SendMiniChatHistory(ChatMessageInfo info, PROTO_CHANNEL_TYPE channel)` → Type=`type`
  - 字段赋值：Channel=channel, Id=PlayerDataHolder.playerData.Uid, Count== 0 && channel != PROTO_CHANNEL_TYPE.PROTO_CHANNEL_TYPE_GM), talk=new Proto_SC_TalkEx(), KUid=kuid, Msg=srcMsg

### 1005 `LSSPROTO_MAIL_FUNC`

**Proto_CS_Mail**：`Type, Id, KUid, Ids, Mail`

**Type / 操作名：**
- `clearplayerbag`
- `delpet all`
- `delstreet `
- `superman`
- `warp 0 `
- `warp 1 `

**Send 封装：**
- `SendChatMessage(string msg, PROTO_CHANNEL_TYPE channel, string uid, bool recordToHistory)` → Type=`type`
  - 字段赋值：Id=id, KUid=uid

### 1008 `LSSPROTO_AREA_FUNC`

**Proto_CS_Area**：`Type, Id, Num, Haveitemindex, Flg, KUid, Level, Exp, Nexpexp, Earthnum, Waternum, Firenum, Windnum, Du, Dumax, Earth, Water, Fire, Wind, Attack, Def, Quick, Magic, Poison, Sleep, Stone, Drunk, Confusion, Amnesia, Critical, Counter, Hitrate, Avoid, Hp, Mp, ResetPoint, Neednum, Mogong, Recovery`

**Type / 操作名：**
- `停止采集`
- `删除物品`
- `开始采集`
- `放入需要道具`
- `获取数据`

**Send 封装：**
- `SendArea(string title, string uid, int id = 0, int num = 0, int haveitemindex = -1, int flg = 0)` → Type=`title`
  - 字段赋值：Id=id, Num=num, Haveitemindex=haveitemindex, Flg=flg, KUid=uid

### 1009 `LSSPROTO_RESET_TASK_FUNC`


### 1010 `LSSPROTO_RECIPE_FUNC`

**Proto_CS_Recipe**：`Type, Id, Haveitemindex, Num, ItemCostType, MakeStoreType, QualityItemIndex, FirstGoodIndex, GetQuality, KUid, Data1, Data2, Item`

**Type / 操作名：**
- `制造道具`
- `获取数据`
- `镶嵌宝石`

**Send 封装：**
- `SendRecipe(string type, string KUid, int id = 0, int haveitemindex = -1, int num = 0)` → Type=`type`
  - 字段赋值：Id=id, Haveitemindex=haveitemindex, Num=num, ItemCostType=((!IsUseBag) ? 1 : 0), MakeStoreType=((!IsPutInBag) ? 1 : 0), KUid=KUid

### 1011 `LSSPROTO_PRODUCTION_FUNC`

**Proto_CS_Production**：`Type, Haveitemindex, Haveitemindex2, Haveitemindex3, Flg, KUid, Level, Exp, Nextexp, Gold, Enchants, Lucks, Error, Crystal, Rate, Status, PerfectGold`

**Type / 操作名：**
- `鉴定道具`

**Send 封装：**
- `SendProduction(string type, string KUid, int haveitemindex = -1, int haveitemindex2 = -1, int haveitemindex3 = -1, int flg = 0)` → Type=`type`
  - 字段赋值：Haveitemindex=haveitemindex, Haveitemindex2=haveitemindex2, Haveitemindex3=haveitemindex3, KUid=KUid

### 1012 `LSSPROTO_TEAM_FUNC`

**Proto_CS_Team**：`Type, Id, IsAfk, KUid, FamilyFlag, FriendFlag, CustomizeFlag, NameList`

**Type / 操作名：**
- `创建队伍`
- `加入队伍`
- `离开队伍`
- `解散队伍`
- `踢出队伍`
- `转让队伍`
- `重叠站位`
- `队伍召集`

**Send 封装：**
- `SendOperation(string type, string id = "")` → Type=`type`
  - 字段赋值：Id=id

### 1013 `LSSPROTO_STREET_FUNC`

**Proto_CS_Street**：`Type, List, Buylist, Id, Page, KUid, Status, Info, Loginfo, SkinList, Time, Pric, Name, Owner, Pages, SearchList`

**Type / 操作名：**
- `购买商品`

**Send 封装：**
- `SendStallMessage(string uid, string type, string name = "", int page = 1)` → Type=`type`
  - 字段赋值：KUid=uid, Id=name, Page=page
- `SendStallMarketBuy(string owner, string itemUid, int num)` → Type=`type`
  - 字段赋值：OwnerCdKey=owner, Num=num, ItemUid=itemUid, Id=id.ToString(), KUid=uid, Page=page

### 1014 `LSSPROTO_FRAME_FUNC`

**Proto_CS_Frame**：`Type, Id, KUid, Data, Usedata, Skin, Heads, HeadUseIndex, Barras, BarrasUseIndex, Btchaboxs, BattleChatBoxUseIndex, RideSkin, RideUseIndex, RoleHalo, HaloUseIndex, Wings, WingUseIndex`

**Type / 操作名：**
- `使用头像框`
- `使用聊天框`
- `使用观战弹幕框`
- `卸下头饰`
- `卸下皮肤`
- `卸下翅膀`
- `卸下观战弹幕框`
- `卸下角色光环`
- `卸下骑宠皮肤`
- `取消道具变身`
- `头饰列表`
- `翅膀列表`
- `获取头像框数据`
- `获取皮肤数据`
- `获取聊天框数据`
- `获取观战弹幕框`
- `装备头饰`
- `装备皮肤`
- `装备翅膀`
- `装备角色光环`
- `装备骑宠皮肤`
- `角色光环列表`
- `购买头像框`
- `购买头饰`
- `购买皮肤`
- `购买翅膀`
- `购买聊天框`
- `购买观战弹幕框`
- `购买角色光环`
- `购买骑宠皮肤`
- `骑宠皮肤列表`

**Send 封装：**
- `SendFrame(string title, string KUid, int id = 0)` → Type=`title`
  - 字段赋值：Id=id, KUid=KUid

### 1015 `LSSPROTO_PETBOOK_FUNC`

**Proto_CS_PetBook**：`Type, Id, KUid, Data1, Data2, Data3`

**Send 封装：**
- `SendPetBook(string uid, string type, int id = 0)` → Type=`type`
  - 字段赋值：Id=id, KUid=uid

### 1016 `LSSPROTO_TRADE_FUNC`

**Proto_CS_Trade**：`Type, Id, List, KUid, Name, Role, Objdata, Cdkey, SameAccount, TradeItemLimit`
**Proto_CS_Trade2**

**Type / 操作名：**
- `开始交易`

**Send 封装：**
- `SendTradeMessage(string type, string cdKey)` → Type=`type`
  - 字段赋值：Id=cdKey

### 1017 `LSSPROTO_OFFLINECOMMAND_FUNC`

**Proto_CS_OfflineCommand**：`Type, KUid, Id, Name, AnimationDataID, Flg, Level`

**Type / 操作名：**
- `设置离线命令`

**Send 封装：**
- `SendOfflineBattle(string uid)`
  - 字段赋值：KUid=uid

### 1018 `LSSPROTO_TITLEEX_FUNC`

**Proto_CS_TitleEx**：`Type, Id, Name, KUid, Use, Data, Custom`

**Type / 操作名：**
- `使用称号`
- `修改自定义称号`
- `关闭称号显示`
- `获取称号数据`

**Send 封装：**
- `SendTitleEx(string title, string KUid, int id = 0, string name = "")` → Type=`title`
  - 字段赋值：Id=id, Name=name, KUid=KUid

### 1019 `LSSPROTO_HPFP_FUNC`

**Proto_CS_HpFp**：`Type, ItemId, BagType, Pile, Rate, KUid, Time, Hp, Fp, Count, CurHp, CurFp, Cdkey`

**Type / 操作名：**
- `关闭血池`
- `关闭魔池`
- `开启血池`
- `开启魔池`
- `获取血池道具`
- `获取魔池道具`
- `血池阈值`
- `血魔池状态`
- `血魔池设置`
- `魔池阈值`

**Send 封装：**
- `SendHpFp(string type, int itemId, int num, int bagType, string uid)` → Type=`type`
  - 字段赋值：ItemId=itemId, BagType=bagType, Pile=num, KUid=uid
- `SendHpFp(string type, string uid, int rate = 0)` → Type=`type`
  - 字段赋值：Rate=rate, KUid=uid

### 1020 `LSSPROTO_PLAYERBUFF_FUNC`

**Proto_CS_PlayerBuff**：`Type, KUid, Info, Gold, Fervertype, Fervertime, Moshi, BattleSpeedTime, HpfpHp, HpfpFp, HpfpMaxhp, HpfpMaxfp, HpfpStatus, HpStatus, FpStatus`

### 1021 `LSSPROTO_ACCOMPANY_FUNC`

**Proto_CS_Accompany**：`Type, Id, Index, KUid, HeadInfoList, PlayerInfo`

**Type / 操作名：**
- `休息`
- `出战`
- `头像数据`

**Send 封装：**
- `SendMercenary(string type, int id = 0)` → Type=`type`
  - 字段赋值：Id=id

### 1022 `LSSPROTO_ALLTALK_FUNC`

**Proto_CS_AllTalk**：`Type, Data, KUid, Name, Str, Talktype, Talkcnt, Point`

**Type / 操作名：**
- `发送喇叭`

**Send 封装：**
- `SendBroadcast(string uid, string msg)`
  - 字段赋值：Data=msg, KUid=uid

### 1023 `LSSPROTO_SELECTOBJ_FUNC`

**Proto_CS_SelectObj**：`Type, Index, Append1, Append2, KUid, Id, Time, Buff, Pric, Flg, PricType, ItemType, Item, Pet`

**Type / 操作名：**
- `NPC全体治疗`
- `NPC单体治疗`

**Send 封装：**
- `SendSelectObj(string type, string index, string append1, string append2)` → Type=`type`
  - 字段赋值：Index=index, Append1=append1, Append2=append2
- `SendCureFromNpc(string type, string index, string uid)` → Type=`type`
  - 字段赋值：Index=index, KUid=uid

### 1024 `LSSPROTO_FAMILY_FUNC`

**Proto_CS_Family**：`Type, CreateFamilyInfo, Id, Code, Memo, NowPage, SubmitIndexs, AutoJoinLevel, ExperienceAddPoints, KUid`

**Type / 操作名：**
- `NPC传送`
- `一键加入`
- `修改图标`
- `修改工会介绍`
- `停止招募`
- `免审等级`
- `切换历练分页`
- `创建家族`
- `历练信息`
- `历练加点`
- `大厅信息`
- `审核成员`
- `家族信息`
- `家族列表`
- `建筑升级`
- `成员列表`
- `招募成员`
- `挑战BOSS`
- `捐献`
- `捐献奖励`
- `提交资金费`
- `申请加入`
- `研究所信息`
- `科技升级`
- `解散工会`
- `设为副会长`
- `设为骨干`
- `购买历练分页`
- `购买历练点`
- `踢出工会`
- `转移会长`
- `退出工会`
- `重置历练分页`
- `领取BOSS奖励`

**Send 封装：**
- `SendFamily(string type, int nowpage = 0, string id = "", string code = "", string memo = "", int autoJoinLevel = 0)` → Type=`type`
  - 字段赋值：NowPage=nowpage, Id=id, Code=code, Memo=memo, AutoJoinLevel=autoJoinLevel
- `SendFamily2(string type, int badge, string name, string memo)` → Type=`type`
  - 字段赋值：Badge=badge, Name=name, Memo=memo, CreateFamilyInfo=proto_CreateFamilyInfo
- `SendExperiencePoint(string type, int page, Dictionary<int, int> point)` → Type=`type`
  - 字段赋值：NowPage=page

### 1025 `LSSPROTO_VIGOR_FUNC`

**Proto_CS_Vigor**：`Type, KUid, Vigor, Crystal, Message`

**Type / 操作名：**
- `活力信息`

**Send 封装：**
- `SendOpenVigor(string uid)`
  - 字段赋值：KUid=uid

### 1026 `LSSPROTO_CRYSTAL_FUNC`

**Proto_CS_Crystal**：`Type, Earth, Water, Fire, Wind, ExpItemType, KUid, Min, Max`

**Type / 操作名：**
- `使用经验道具`
- `修复耐久`
- `水晶数据`
- `水晶配置`
- `注入碎片`
- `调整属性`
- `重置属性`

**Send 封装：**
- `SendCrystal(string type, string uid, int earth = 0, int water = 0, int fire = 0, int wind = 0)` → Type=`type`
  - 字段赋值：Earth=earth, Water=water, Fire=fire, Wind=wind, KUid=uid
- `SendCrystal(string type, int expIndex, string uid)` → Type=`type`
  - 字段赋值：ExpItemType=expIndex, KUid=uid

### 1028 `LSSPROTO_TRUST_FUNC`


**Send 封装：**
- `SendBountyOfferedC2S(Proto_CS_Trust rInfo)`

### 1029 `LSSPROTO_MULTI_FUNC`

**Proto_CS_Multi**：`Type, Id, Mapid, Floor, X, Y, KUid, Players`

**Type / 操作名：**
- `一键召唤`
- `切换角色`
- `召唤角色`
- `头像切换角色`
- `登出角色`
- `登陆角色`
- `获取多控`

**Send 封装：**
- `SendMulti(string type, int mapId, int floorId, Vector2Int location, string id = "")` → Type=`type`
  - 字段赋值：Id=id, Mapid=mapId, Floor=floorId, X=((Vector2Int)(ref location)).x, Y=((Vector2Int)(ref location)).y, KUid=PlayerDataHolder.MainPlayerUid

### 1030 `LSSPROTO_TEAMCONFIG_FUNC`

**Proto_CS_TeamConfig**：`Type, Config, KUid, Hp, Fp, Atk, Def, Quick, Poison, Sleep, Stone, Drunk, Confusion, Amnesia, Critical, Counter, Hit, Avoid`

**Type / 操作名：**
- `组队设置`

**Send 封装：**
- `SendConfig(string type, Proto_TeamConfig config)` → Type=`type`
  - 字段赋值：Config=config

### 1031 `LSSPROTO_MESSAGE_CODE_FUNC`

**Proto_CS_MessageCode**：`Type, Code, KUid, Title, Data, Submitname, Cancelname`

### 1032 `LSSPROTO_NEARBY_FUNC`

**Proto_CS_Nearby**：`Type, Ids, KUid, Job, Teamnum, Trade, Battle, Watch, Objindex`

**Type / 操作名：**
- `对象数据`

**Send 封装：**
- `SendNearby(string type, List<int> ids = null)` → Type=`type`

### 1033 `LSSPROTO_CHARACTER_CHANGE_FUNC`

**Proto_CS_ChangeCharacter**：`Type, Grano, KUid, Buff`

**Type / 操作名：**
- `更改形像魔币`
- `更改形像魔晶`

**Send 封装：**
- `SendChangeCharacter(string type, int grano, string uid)` → Type=`type`
  - 字段赋值：KUid=uid, Grano=grano

### 1035 `LSSPROTO_CUSTOMER_FUNC`

**Proto_CS_Customer**：`Type, KUid, Url, Name, Id`

**Type / 操作名：**
- `客服地址`

**Send 封装：**
- `SendCustomer(string type)` → Type=`type`

### 1036 `LSSPROTO_LUCK_FUNC`

**Proto_CS_Luck**：`Type, Index, Num, KUid, Icon, Log, Infos, Items, Message, Pages, PageIndexs, Iteminfo, Luck, Grano`

**Type / 操作名：**
- `列表信息`

**Send 封装：**
- `SendLuck(string type, int index = 0, int num = 0)` → Type=`type`
  - 字段赋值：Index=index, Num=num

### 1038 `LSSPROTO_SHOP_FUNC`

**Proto_CS_Shop**：`Type, Infos, TabIndex, Curr, KUid, Name, Index, CurrencyType, IsSpecial`

**Type / 操作名：**
- `商品列表`
- `商店列表`
- `购买商品`

**Send 封装：**
- `SendBuy(string type, int index, int itemindex, int itemcount, string uid, PROTO_CURRENCY curr)` → Type=`type`
  - 字段赋值：TabIndex=index, Infos=new Proto_BuyShopData(), Index=itemindex, Num=itemcount, Curr=curr, KUid=uid
- `SendGetStoreList(string type, string uid, int tabIndex = 1)` → Type=`type`
  - 字段赋值：TabIndex=tabIndex, KUid=uid

### 1040 `LSSPROTO_WATCH_BATTLE_FUNC`

**Proto_CS_WatchBattle**：`Type, Objindex, Index, Msg, KUid, Watchs, GraNo`

**Type / 操作名：**
- `观战`
- `观战信息`
- `观战弹幕`

**Send 封装：**
- `SendWatch(string type, int battleindex = 0, int objindex = 0)` → Type=`type`
  - 字段赋值：Index=battleindex, Objindex=objindex
- `SendWatchBullet(string msg)`
  - 字段赋值：Msg=msg

### 1041 `LSSPROTO_DAILY_EXPAND_FUNC`

**Proto_CS_DailyExpand**：`Type, Id, KUid, RecommendId, Weeks, Dailys`

**Type / 操作名：**
- `列表信息`
- `活动列表`

**Send 封装：**
- `SendDaily(string type, int id = 0)` → Type=`type`
  - 字段赋值：Id=id

### 1042 `LSSPROTO_PLAYER_SKILLS_FUNC`

**Proto_CS_PlayerSkills**：`Type, KUid, Buff`

**Type / 操作名：**
- `新增技能栏`

**Send 封装：**
- `SendPlayerSkills(string type, string uid)` → Type=`type`
  - 字段赋值：KUid=uid

### 1043 `LSSPROTO_PK_FUNC`

**Proto_CS_PK**：`Type, KUid, Context, Teams, Gamename, Id, Time, Teamname, Name16S, Name8S, Name4S, Name2S, Name1, Jiangli`

**Type / 操作名：**
- `打开PK比赛`

**Send 封装：**
- `SendPk(string type)` → Type=`type`

### 1044 `LSSPROTO_WATCH_FUNC`

**Proto_CS_Watch**：`Type, Floor, Page, KUid, Name1, Name2, Time, File`

**Type / 操作名：**
- `观战`
- `观战信息`

**Send 封装：**
- `SendWatch(string type, int page, int floor)` → Type=`type`
  - 字段赋值：Page=page, Floor=floor

### 1045 `LSSPROTO_QUERCHARACTER_FUNC`


### 1046 `LSSPROTO_PLAYER_CONFIG_FUNC`


### 1047 `LSSPROTO_ACTIVITY_FUNC`

**Proto_CS_Activity**：`Type, ActivityId, Id, Code, Index, BoxIndexs, TeamName, Shopid, PVPShopNum, DrawCount, DrawSelect, TargetX, TargetY, Pid, Tid, KUid`

**Type / 操作名：**
- `CDKey兑换`
- `giftCode`
- `兑换奖励`
- `分享排行榜`
- `开关掉落`
- `批量取出`
- `抽取宠物`
- `指定目标`
- `每日奖励`
- `每日签到`
- `活动信息`
- `活动列表`
- `瞬间移动`
- `累充奖励领取`
- `累计在线奖励领取`
- `练级达人奖励领取`
- `许愿`
- `远程个人仓库`
- `远程个人宠物仓库`
- `远程个人道具仓库`
- `远程出售魔石`
- `远程账号宠物仓库`
- `远程账号道具仓库`
- `领取每日奖励`
- `领取许愿奖励`
- `领取集字纳福奖励`

**Send 封装：**
- `SendActivity(string type, string KUid, int id = 0, int activityId = 0, string code = "", int index = 0)` → Type=`type`
  - 字段赋值：Id=id, ActivityId=activityId, Code=code, Index=index, KUid=KUid
- `SendActivityXY(string type, string kuid, int targetX, int targetY)` → Type=`type`
  - 字段赋值：ActivityId=19, KUid=kuid, TargetX=targetX, TargetY=targetY
- `SendOpenChest(string uid, string type, int boxId, int times)` → Type=`type`
  - 字段赋值：ActivityId=boxId, Id=times, KUid=uid
- `SendSpaceMsg(string type, string uid, int num = 0)` → Type=`type`
  - 字段赋值：KUid=KUid, Count=num, Status== 1 && fullData), Item== null && dictionary.ContainsKey(proto_LotteryBag.Index)), ActivityId=activityId
- `SendPVPSkyLadder(string type, int activityId, string kuid, string teamName = "", int num = 0, int id = 0)` → Type=`type`
  - 字段赋值：ActivityId=activityId, TeamName=teamName, Shopid=id, PVPShopNum=num, KUid=kuid
- `SendTreasureHunt(string type, int activityId, string kuid)` → Type=`type`
  - 字段赋值：ActivityId=activityId, KUid=kuid
- `SendTreasureHuntWish(string type, int activityId, string kuid, int wishId)` → Type=`type`
  - 字段赋值：ActivityId=activityId, KUid=kuid, Id=wishId
- `SendTreasureHuntExchage(string type, int activityId, string kuid, int exchangeId)` → Type=`type`
  - 字段赋值：ActivityId=activityId, KUid=kuid, Id=exchangeId
- `SendPetAstralBondMsg(string type, int pool, string uid, int activityId, int petId = -1)` → Type=`type`
  - 字段赋值：Pid=pool, Tid=petId, KUid=uid, ActivityId=activityId

### 1048 `LSSPROTO_PLAYER_POING_FUNC`

**Proto_CS_PlayerPoint**：`Type, Vital, Str, Tgh, Quick, Magic, Passwd, Bpindex, Name, Earth, Water, Fire, Wind, KUid`

**Type / 操作名：**
- `修改BP名字`
- `修改名字`
- `切换BP`
- `加点`
- `开通BP`
- `获取BP名字`
- `资源下载`
- `重置BP`

**Send 封装：**
- `SendResetBp(string rType, string KUid)`
  - 字段赋值：KUid=KUid
- `SendAddOrCutBp(string rType, int vital, int str, int tgh, int quick, int magic, string KUid)`
  - 字段赋值：Vital=vital, Str=str, Tgh=tgh, Quick=quick, Magic=magic, KUid=KUid
- `SendResetAttr(string rType, int rEarth, int rWater, int rFire, int rWind, string KUid)`
  - 字段赋值：KUid=KUid, Earth=rEarth, Water=rWater, Fire=rFire, Wind=rWind
- `SendQieHuanBP(string rType, int rIndex, string KUid)`
  - 字段赋值：Bpindex=rIndex, KUid=KUid
- `SendKaiTongBP(string rType, string KUid)`
  - 字段赋值：KUid=KUid
- `SendChangeName(string rType, string rName, string KUid)`
  - 字段赋值：Name=rName, KUid=KUid
- `SendChongZhiBP(string rType, string KUid)`
  - 字段赋值：KUid=KUid
- `SendGetBPName(string KUid)`
  - 字段赋值：KUid=KUid
- `SendChangeBPName(string kuid, int index, string name)`
  - 字段赋值：Bpindex=index, Name=name, KUid=kuid

### 1049 `LSSPROTO_MESSAGEBOX_FUNC`

**Proto_CS_MessageBox**：`Type, Btntype, Select, KUid, Index, VitalLock, StrLock, TghLock, QuickLock, MagicLock, CostType, VitalAuto, StrAuto, TghAuto, QuickAuto, MagicAuto, CostIndex, AttrIndex`

**Send 封装：**
- `SendMessageBox(string uid, string messageType, int btntype, int select = 0)`
  - 字段赋值：Btntype=btntype, Select=select, KUid=uid

### 1050 `LSSPROTO_PET_RESET_FUNC`

**Proto_CS_PetReset**：`Type, Index, VitalLock, StrLock, TghLock, QuickLock, MagicLock, CostType, VitalAuto, StrAuto, TghAuto, QuickAuto, MagicAuto, CostIndex, AttrIndex, KUid`

**Send 封装：**
- `SendResetFile(string uid, string type, int index, bool vital_lock = false, bool str_lock = false, bool tgh_lock = false, bool quick_lock = false, bool magic_lock = false, int vital_auto = -2, int str_auto = -2, int tgh_auto = -2, int quick_auto = -2, int magic_auto = -2)` → Type=`type`
  - 字段赋值：KUid=uid, Index=index, VitalLock=vital_lock, StrLock=str_lock, TghLock=tgh_lock, QuickLock=quick_lock
- `SendResetFileT(string uid, string type, int index, int costIndex = 0, int attrInsex = 0)` → Type=`type`
  - 字段赋值：KUid=uid, Index=index, CostIndex=costIndex, AttrIndex=attrInsex

### 1051 `LSSPROTO_PET_POINT_FUNC`

**Proto_CS_PetPoint**：`Type, Havepetindex, Vital, Str, Tgh, Quick, Magic, Fromindex, Toindex, KUid, Msg`

**Type / 操作名：**
- `宠物技能换位`
- `宠物换位`

**Send 封装：**
- `SendResetPoint(string uid, string type, int index, int vital = 0, int str = 0, int tgh = 0, int quick = 0, int magic = 0)` → Type=`type`
  - 字段赋值：KUid=uid, Havepetindex=index, Vital=vital, Str=str, Tgh=tgh, Quick=quick
- `SendPetHeadExchange(string type, int fromIndex, int toIndex, string uid, int haveIndex = -1)` → Type=`type`
  - 字段赋值：Fromindex=fromIndex, Toindex=toIndex, KUid=uid, Havepetindex=haveIndex

### 1052 `LSSPROTO_DAILY_ACTIVITY_EX_FUNC`

**Proto_CS_DailyActivityEx**：`Type, AwardId, KUid, Id, FinishTimes, Value, ResetType`

**Send 封装：**
- `SendDailyTaskMsg(string uid, int awardId)`
  - 字段赋值：AwardId=awardId, KUid=uid

### 2000 `LSSPROTO_SKILL_FUNC`

**Proto_CS_Skill**：`Func, Type, Petindex, Skillindex, Objindex, Buyindex, KUid, Index, Name, CurrencyType, Gold, Slot, Info, Icon, Level, Mp`

**Send 封装：**
- `SendSkillMessage(string type, int skillIndex, int objIndex, int buyIndex, int petIndex)` → Type=`type`
  - 字段赋值：Func=2000, Petindex=petIndex, Skillindex=skillIndex, Objindex=objIndex, Buyindex=buyIndex
- `SendSkillMessage(string type, int skillIndex, int objIndex, int petIndex)` → Type=`type`
  - 字段赋值：Func=2000, Petindex=petIndex, Skillindex=skillIndex, Objindex=objIndex

### 2001 `LSSPROTO_BACKPACK_FUNC`

**Proto_CS_Backpack**：`Func, Type, Haveitemindex, Objindex, Num, Pass, KUid, Name, Lv, Hp, Maxhp, Fp, Maxfp, Injury, Id, AnimationDataID, Headid`

**Type / 操作名：**
- `一键鉴定`
- `丢弃道具`
- `使用道具到指定对象`
- `修理单件`
- `增加道具栏`
- `拆分道具`
- `整理背包`

**Send 封装：**
- `SendBackPackMessage(string type, string uid)` → Type=`type`
  - 字段赋值：KUid=uid
- `SendBackPackMessage(string type, int index, int num, string uid)` → Type=`type`
  - 字段赋值：Haveitemindex=index, Num=num, KUid=uid
- `SendRepairOneMessage(int index, string uid)`
  - 字段赋值：KUid=uid, Haveitemindex=index
- `SendUseItemToTargetMessage(string type, int index, int targetIndex, string uid)` → Type=`type`
  - 字段赋值：Haveitemindex=index, Objindex=targetIndex, KUid=uid

### 2002 `LSSPROTO_BANK_FUNC`

**Proto_CS_Bank**：`Func, Type, Num, Index, IndexList, KUid, GraNo, Pile, Flg, Sort, Quality, Itemid`

**Send 封装：**
- `SendBankMessage(BANK_TYPE banktype, string uid, string type, int index = 0, int num = 0, List<int> indexList = null)` → Type=`type`
  - 字段赋值：Index=index, Num=num, KUid=uid

### 2003 `LSSPROTO_ACCOUNT_BANK_FUNC`

**Proto_CS_Bank**：`Func, Type, Num, Index, IndexList, KUid, GraNo, Pile, Flg, Sort, Quality, Itemid`

**Send 封装：**
- `SendBankMessage(BANK_TYPE banktype, string uid, string type, int index = 0, int num = 0, List<int> indexList = null)` → Type=`type`
  - 字段赋值：Index=index, Num=num, KUid=uid

### 2004 `LSSPROTO_BATTLE_SPEED_FUNC`

**Proto_CS_BattleSpeed**：`Type, KUid, MinRank, MaxRank`

**Type / 操作名：**
- `关闭加速`
- `开启加速`

**Send 封装：**
- `SendSetSpeedFlg(string type, string uid)` → Type=`type`
  - 字段赋值：KUid=uid

### 3001 `LSSPROTO_AUCTIONHOUSE_FUNC`

**Proto_CS_Auction**：`Type, MainType, SubType, KeyWord, OwnerCdKey, ItemUid, Num, PageIndex, KUid, KeyWordList, Page, List`

**Type / 操作名：**
- `获取数据`
- `购买商品`

**Send 封装：**
- `SendStallMarketMessage(string type, int mainType, int subType, int page, string keyWord = "")` → Type=`type`
  - 字段赋值：MainType=mainType, SubType=subType, PageIndex=page, KeyWord=keyWord
- `SendStallMarketBuy(string owner, string itemUid, int num)`
  - 字段赋值：OwnerCdKey=owner, Num=num, ItemUid=itemUid

### 3003 `LSSPROTO_ITEM_LIST_FUNC`

**Proto_CS_ItemList**：`Type, ListType, PetIndex, BagIndex, KUid, List`

**Type / 操作名：**
- `使用道具`
- `使用道具到指定对象`
- `装备标记`

**Send 封装：**
- `SendUseItemToTargetMessage(string type, int index, int targetIndex, string uid)` → Type=`type`
  - 字段赋值：Haveitemindex=index, Objindex=targetIndex, KUid=uid, ListType=subTtype, PetIndex=petIndex
- `SendChoseItemUse(string mainType, int subTtype, string uid, int petIndex, int bagIndex)`
  - 字段赋值：ListType=subTtype, PetIndex=petIndex, BagIndex=bagIndex, KUid=uid
- `SendMarkEquipMsg(int bagIndex, string uid)`
  - 字段赋值：BagIndex=bagIndex, KUid=uid

### 3006 `LSSPROTO_LOCK_FUNC`

**Proto_CS_Lock**：`Type, PassWd, Index, Code, KUid, Plat, Name, Ip, Timestamp`

**Send 封装：**
- `SendSecurityInfo(Proto_CS_Lock info)`
- `SendCentralControlCode(string type, string uid, int paswd = 0)` → Type=`type`
  - 字段赋值：PassWd=paswd, KUid=uid

### 3007 `LSSPROTO_FIX_DUNGEON_FUNC`

**Proto_CS_PlayerFixDungeon**：`MapId, Floor, X, Y, KUid, Type, ItemId`

**Send 封装：**
- `SendEnvirOnment(int battleindex)`
  - 字段赋值：Battleindex=battleindex, MapId=MonoSingleton<MapManager>.instance.currentMapID, Floor=MonoSingleton<MapManager>.instance.currentFloor, X=((Vector2Int)(ref location)).x, Y=((Vector2Int)(ref location)).y

### 3008 `LSSPROTO_MONSTER_BREED_FUNC`

**Proto_CS_MonsterBreed**：`Type, ItemId, KUid, Value, Max, List, Msg`

**Type / 操作名：**
- `获取数据`

**Send 封装：**
- `SendMonsterBreedMsg(string type, string uid, int itemId = -1)` → Type=`type`
  - 字段赋值：KUid=uid, ItemId=itemId

### 3009 `LSSPROTO_PET_UPSTAR_FUNC`

**Proto_CS_PetUpStar**：`Type, Index, KUid, List, PassWd, Code`

**Type / 操作名：**
- `升星卡合成`

**Send 封装：**
- `SendPetUpStarMessage(string type, int index, string uid)` → Type=`type`
  - 字段赋值：Index=index, KUid=uid

### 3011 `LSSPROTO_LAYERBOX_FUNC`

**Proto_CS_LayerBox**：`Type, Single, KUid, Layer, EndTime, List`

**Type / 操作名：**
- `开启宝匣`

**Send 封装：**
- `SendLayerBox(string kuid, int single)`
  - 字段赋值：Single=single, KUid=kuid

### 3012 `LSSPROTO_REPAIRBATTLE_FUNC`

**Proto_CS_RepairBattle**：`KUid, Players, Time, List`

**Send 封装：**
- `SendBattleData()`
  - 字段赋值：battleModeFlag== BATTLE_TYPE.WATCH || BattleDataHolder.battleModeFlag == BATTLE_TYPE.PVP_WATCH), KUid=PlayerDataHolder.MainPlayerUid

### 3013 `LSSPROTO_CLIENTLIMIT_FUNC`

**Proto_CS_ClientLimit**：`List, Type, Id, KUid, Times, BuyTimes`

### 3014 `LSSPROTO_EASTEREGG_FUNC`

**Proto_CS_EasterEgg**：`Type, Index, Nums, KUid, Layer, EndTime, List, Draw, Init, Shop, RetLayer`

**Send 封装：**
- `SendEasterEgg(string kuid, string type, int index, int nums)` → Type=`type`
  - 字段赋值：Index=index, Nums=nums, KUid=kuid

### 3015 `LSSPROTO_AUTOBATTLE_FUNC`

**Proto_CS_AutoBattle**：`Type, KUid, Exp, Item, StartTime, RewardList`

**Type / 操作名：**
- `停止挂机`
- `奖励查看`
- `开始挂机`
- `暂停挂机`
- `结束计时`
- `继续挂机`

**Send 封装：**
- `SendAutoBattle(string type, string uid)` → Type=`type`
  - 字段赋值：KUid=uid

### 3016 `LSSPROTO_JOBSWITCH_FUNC`

**Proto_CS_JobSwitch**：`Type, Index, PlayerAutoSkills, PetsAutoSkills, PlayerAutoSkillFlg, PetAutoSkillFlg, KUid, Jbid, Sort, Del`

**Type / 操作名：**
- `删除职业`
- `开通储存`
- `职业切换`
- `自动技能设置`
- `请求数据`

**Send 封装：**
- `SendAllSetting(string uid)`
  - 字段赋值：PlayerAutoSkillFlg=m_AutoState[uid][0], PetAutoSkillFlg=m_AutoState[uid][1], KUid=uid
- `SendChangeJob(string type, string uid, int job = -1)` → Type=`type`
  - 字段赋值：KUid=uid, Index=job

### 3017 `LSSPROTO_FLASHSALE_FUNC`

**Proto_CS_FlashSale**：`Type, Index, KUid, BuyType, OrgPrice, NowPrice, Nums, EndTime, GoodsType, Money, ChargeId, Item, GoodsId, StartTime, PayTime`

**Type / 操作名：**
- `商品列表`

**Send 封装：**
- `SendSuperSellMsg(string type, string uid, int index = -1)` → Type=`type`
  - 字段赋值：KUid=uid, Index=index

### 3018 `LSSPROTO_PETBOND_FUNC`

**Proto_CS_PetBond**：`Type, Index, BondPetIndex, CostPetIndex, Level, KUid`

**Send 封装：**
- `SendPetBond(string uid, string type, int petIndex, int bondPetIndex, int costPetIndex, int level)` → Type=`type`
  - 字段赋值：KUid=uid, Index=petIndex, CostPetIndex=costPetIndex, BondPetIndex=bondPetIndex, Level=level

### 3019 `LSSPROTO_FLASHLIMIT_FUNC`

**Proto_CS_FlashSale**：`Type, Index, KUid, BuyType, OrgPrice, NowPrice, Nums, EndTime, GoodsType, Money, ChargeId, Item, GoodsId, StartTime, PayTime`

**Type / 操作名：**
- `商品列表`

**Send 封装：**
- `SendSuperSellSingle(string type, string uid, int index = -1)` → Type=`type`
  - 字段赋值：KUid=uid, Index=index

### 3020 `LSSPROTO_SELECT_PET_WINDOWS_FUNC`


### 3021 `LSSPROTO_CODE_PET_WINDOWS_FUNC`

**Proto_CS_PetCodeWindows**：`Type, KUid, Title, PetIndex, Awards`

**Send 封装：**
- `SendPetRecycle(string type)` → Type=`type`

### 3022 `LSSPROTO_FIVE_CHAR_DRAW_FUNC`

**Proto_CS_FiveCharDraw**：`Type, Id, Code, KUid, EndTime, DrawItemId, LeftPools, DrawInfos, WishLists, DrawIndex, IsCode`

**Type / 操作名：**
- `手机验证`
- `活动信息`

**Send 封装：**
- `SendFiveBlessing(string type, string uid, int wishId = 0, string code = "")` → Type=`type`
  - 字段赋值：Id=wishId, Code=code, KUid=uid

### 3023 `LSSPROTO_PET_EQUIP_FUNC`

**Proto_CS_PetEquip**：`Type, EquipPos, Havepetindex, AutoDecompose, KUid`

**Type / 操作名：**
- `分解装备`
- `升级装备`
- `卸下装备`
- `穿戴装备`

**Send 封装：**
- `SendPetEquip(string type, int itemIndex, int petIndex, string uid)` → Type=`type`
  - 字段赋值：EquipPos=itemIndex, Havepetindex=petIndex, KUid=uid

### 3025 `LSSPROTO_DUR_REPAIRE_FUNC`

**Proto_CS_DurRepaire**：`Type, Gird, KUid, Price`

**Type / 操作名：**
- `一键修理`
- `一键修理价格`
- `单个修理`
- `单个修理价格`

**Send 封装：**
- `SendRepair(string type, string uid, int index = -1)` → Type=`type`
  - 字段赋值：Gird=index, KUid=uid

### 3026 `LSSPROTO_FAMILY_BATTLE_FUNC`

**Proto_CS_FamilyBattle**：`Type, CustomTimeIndex, Zone, AssistCdkey, TarCdkey, TaxRateIndex, FamilyId, OccupyFamilyId, OccupyTime, TodayTax, TaxRate, TotalTax, DamageDown, DefenceDown, OccupyFamily, List, SignedFamilyList, BattleTime, BattleFamily, Role, ACampCount, BCampCount`

**Type / 操作名：**
- `PK切磋`
- `传送管理员`
- `外援邀请`
- `接受邀请`
- `比赛报名`
- `设置税率`

**Send 封装：**
- `SendFamilyBattle(string type, int customTime = -1, int zone = 0, string assistCdkey = "", string tarCdkey = "", int taxRateIndex = 0, int familyID = 0)` → Type=`type`
  - 字段赋值：CustomTimeIndex=customTime, Zone=zone, AssistCdkey=assistCdkey, TarCdkey=tarCdkey, TaxRateIndex=taxRateIndex, FamilyId=familyID

### 3027 `LSSPROTO_CHOOSE_PET_FUNC`

**Proto_CS_Choose_Pet**：`Type, PetId, KUid, PetList, ClientIndice, BoxIndexs`

**Type / 操作名：**
- `选择宠物`

**Send 封装：**
- `SendChoosePetCallback(int petId)`
  - 字段赋值：PetId=petId, KUid=PlayerDataHolder.MainPlayerUid

### 3028 `LSSPROTO_BLINDBOX_FUNC`

**Proto_CS_BlindBox**：`Type, ClientIndice, BoxIndexs, KUid, Index, Item, List, DrawCount`

**Type / 操作名：**
- `全部取出`
- `开始抽奖`
- `批量删除`
- `批量取出`
- `抽奖`
- `整理卡仓`
- `获取数据`
- `重置抽奖`

**Send 封装：**
- `SendBlindboxDraw(string type, string uid, List<int> ints, List<int> boxIndexs = null)` → Type=`type`
  - 字段赋值：KUid=uid

### 3029 `LSSPROTO_PET_RIDE_FUNC`

**Proto_CS_Pet_Ride**：`Type, Tribe, Index, KUid, PetRideMap`

**Type / 操作名：**
- `改变骑乘状态`

**Send 封装：**
- `SendRidePetMsg(string type, string uid, int tribe = -2, int petIndex = -1)` → Type=`type`
  - 字段赋值：Tribe=tribe, Index=petIndex, KUid=uid

### 3030 `LSSPROTO_OTHER_EQUIP_FUNC`

**Proto_CS_OtherEquip**：`Type, Gird, KUid, Item`

**Type / 操作名：**
- `卸下装备`
- `穿戴装备`
- `获取数据`

**Send 封装：**
- `SendOtherEquip(string type, string uid, int index)` → Type=`type`
  - 字段赋值：Gird=index, KUid=uid

### 3031 `LSSPROTO_PET_RESETBASE_FUNC`

**Proto_CS_PetResetBase**：`Type, Index, VitalAuto, StrAuto, TghAuto, QuickAuto, MagicAuto, KUid, PetId, Level`

**Type / 操作名：**
- `宠物洗档`
- `替换洗档`
- `设置连续`

**Send 封装：**
- `SendResetBaseAttr(string type, int index, string uid, int vitalSet = 0, int strSet = 0, int tghSet = 0, int quickSet = 0, int magicSet = 0)` → Type=`type`
  - 字段赋值：Index=index, KUid=uid, VitalAuto=vitalSet, StrAuto=strSet, TghAuto=tghSet, QuickAuto=quickSet

### 3032 `LSSPROTO_PRAY_FUNC`

**Proto_CS_Pray**：`Type, Id, KUid, PrayInfoMap, UpdateData, Point, PrayNum`

**Type / 操作名：**
- `祈愿升级`
- `获取数据`

**Send 封装：**
- `SendPrayMsg(string type, string uid, int attrId = -1)` → Type=`type`
  - 字段赋值：Id=attrId, KUid=uid

### 3033 `LSSPROTO_MONSTERTOWER_FUNC`

**Proto_CS_MonsterTower**：`Type, Layer, KUid, Times, Cycle, RewardType, Items, Cayer`

**Type / 操作名：**
- `获取信息`

**Send 封装：**
- `SendBountyOfferedC2S(Proto_CS_Trust rInfo)` → Type=`type`
  - 字段赋值：KUid=kuid, Id=id, eUIState== 3), awardType=1, Layer=floor

### 3034 `LSSPROTO_CURRENCY_EXCHANGE_FUNC`

**Proto_CS_CurrencyExchange**：`Type, CurrencyType, ExchangeAmount, KUid, ExchangedCurrencyType, ExchangedCurrencyAmount, TargetCurrencyType, Rate, RateDescribe`

**Send 封装：**
- `SendCurrencyExchange(string uid, string type, PROTO_CURRENCY currency_type, int num)` → Type=`type`
  - 字段赋值：KUid=uid, CurrencyType=currency_type, ExchangeAmount=num

### 3036 `LSSPROTO_ONLINE_INFO_FUNC`

**Proto_CS_OnLineInfo**：`LineId, KUid, WarpId, Result`

**Send 封装：**
- `SendSwitchServer(string uid, int id)`
  - 字段赋值：KUid=uid, LineId=id

### 3037 `LSSPROTO_TASK_WARP_FUNC`

**Proto_CS_TaskWarp**：`WarpId, KUid, Result, Type, Cdkey`

**Send 封装：**
- `SendAutoWarp(int id, string uid)`
  - 字段赋值：KUid=uid, WarpId=id

### 3038 `LSSPROTO_FLURRYPK_BATTLE_FUNC`

**Proto_CS_Flurry**：`Type, Cdkey, KUid, Status, TopGoods, JoinGoods`

**Type / 操作名：**
- `比赛报名`

**Send 封装：**
- `SendFlurryPKMsg(string type, string uid)` → Type=`type`
  - 字段赋值：KUid=uid

### 3040 `LSSPROTO_SET_SPECIAL_FLAG`

**Proto_CS_SetSpecialFlag**：`KUid, CurrencyType, Gold, Coins`

**Send 封装：**
- `SendSpecialFlag(string uid)`
  - 字段赋值：KUid=uid

### 3041 `LSSPROTO_USE_CURRENCY_TIP`

**Proto_CS_UseCurrencyTip**：`CurrencyType, KUid, Gold, Coins, ModeName, TickTimes, TotalCost, MinCost, MaxCost, NoSleepTimes, IsTotal, Gcsize, Gccount, RunCnt, Gcmen`

**Send 封装：**
- `SendUseCurrencyTip(string uid, int currType)`
  - 字段赋值：CurrencyType=currType, KUid=uid

### 3042 `LSSPROTO_SKIN_LEVELUP`

**Proto_CS_SkinLevelUp**：`Type, Index, KUid, Skin, Times, StoreIndexs`

**Type / 操作名：**
- `皮肤升级`

**Send 封装：**
- `SendUpLevelSkin(int index, string kuid)`
  - 字段赋值：Index=index, KUid=kuid

### 3043 `LSSPROTO_CRYSTAL_HOUSE_FUNC`

**Proto_CS_CrystalHouse**：`Type, Times, StoreIndexs, KUid, Index, Item, Clear, Items`

**Type / 操作名：**
- `仓库信息`
- `仓库删除`
- `仓库取出`
- `抽奖`

**Send 封装：**
- `SendLuckCrystalMsg(string type, string uid, int times = 0, List<int> whTakeOutList = null)` → Type=`type`
  - 字段赋值：Times=times, KUid=uid

### 3044 `LSSPROTO_SOLDIER_OF_HONOR_FUNC`

**Proto_CS_SoldierOfHonor**：`Type, KUid, Id, PriceType, Team, BossIndex`

**Type / 操作名：**
- `荣耀升级`

**Send 封装：**
- `SendUpGradeHonour(string type, string uid)` → Type=`type`
  - 字段赋值：KUid=uid

### 3045 `LSSPROTO_BOSS_SUPPRESS_TOKEN_FUNC`

**Proto_CS_BossSuppressToken**：`Type, PriceType, Team, BossIndex, KUid, X, Y`

### 3046 `LSSPROTO_EARCH_MOUSE_LOTTERY_FUNC`

**Proto_CS_EarthMouseLottery**：`Type, KUid, Name, Rank, Title`

**Type / 操作名：**
- `请求数据`
- `请求日志`
- `购买彩票`

**Send 封装：**
- `SendDiglettLotteryMsg(string type, string uid)` → Type=`type`
  - 字段赋值：KUid=uid

### 3047 `LSSPROTO_HERO_TRIALS_FUNC`

**Proto_CS_HeroTrials**：`Type, Team, BossIndex, KUid, ExchangeId, CostItemId, CostCount, GiveItemId, GiveCount, HoldCount`

**Type / 操作名：**
- `挑战BOSS`
- `购买次数`

**Send 封装：**
- `SendBraveTrialMsg(string type, int team, int bossIndex, string uid)` → Type=`type`
  - 字段赋值：Team=team, BossIndex=bossIndex, KUid=uid

### 3049 `LSSPROTO_BLIND_BOX_NEW1_FUNC`

**Proto_CS_BlindBoxNew**

**Type / 操作名：**
- `全部取出`
- `批量删除`
- `批量取出`
- `抽奖`
- `整理卡仓`
- `获取数据`

**Send 封装：**
- `SendLottery(string type, string uid, int times = 0, List<int> indexs = null)` → Type=`type`
  - 字段赋值：Times=times, KUid=uid

### 3050 `LSSPROTO_BOSS_LAND_CHALLENGE_FUNC`

**Proto_CS_BossLandChallenge**：`Type, DungeonId, ExchangeId, Count, PriceType, BuyIndex, LayerId, KUid, WeekTimes, WeekUsed, BuyTimes, DayTimes, DayUsed, Exchanges, WeekBuyLimit, OpenLevel, NextBuyTier, BuyPrice1Tiers, BuyPrice2Tiers, WeekRemain, TodayRemain, Cycle`

**Type / 操作名：**
- `兑换`
- `兑换列表`
- `挑战BOSS`
- `获取数据`
- `购买次数`

**Send 封装：**
- `SendCrystalAndSwMsg(string type, int dungeonId, int layerId, string uid)` → Type=`type`
  - 字段赋值：DungeonId=dungeonId, LayerId=layerId, KUid=uid
- `SendBuyCrystalAndSwMsg(string type, int dungeonId, int priceType, int buyIndex, string uid)` → Type=`type`
  - 字段赋值：DungeonId=dungeonId, PriceType=priceType, BuyIndex=buyIndex, KUid=uid
- `SendBossLandExchangeListMsg(int dungeonId, string uid)`
  - 字段赋值：DungeonId=dungeonId, KUid=uid
- `SendBossLandExchangeDoMsg(int dungeonId, int exchangeId, int count, string uid)`
  - 字段赋值：DungeonId=dungeonId, ExchangeId=exchangeId, Count=count, KUid=uid

### 3051 `LSSPROTO_ENCHANT_FUNCTION`

**Proto_CS_EnchantFunction**：`Type, Index, Index2, Index3, Punishment, Count, KUid, Success, CostCount1, CostCount2, Enchants, Lucks, Error, Status, EnchantLevel, MergeShop`

**Type / 操作名：**
- `装备附魔`
- `选择幸运符`
- `选择装备`
- `选择附魔石`

**Send 封装：**
- `SendEnchant(string type, string uid, int index = 0, int index2 = 0, int index3 = 0, bool punishment = false)` → Type=`type`
  - 字段赋值：Index=index, Index2=index2, Index3=index3, Punishment=punishment, KUid=uid

### 4000 `LSSPROTO_PLAYER_FUNC`

**Proto_CS_PlayerMgr**：`Type, Vital, Str, Tgh, Quick, Magic, Pass, Code, Func`

**Send 封装：**
- `SendSafeCodeMessage(string type, string pwd, string uid)` → Type=`type`
  - 字段赋值：Pass=pwd

### 4001 `LSSPROTO_RANKLIST_FUNC`


**Send 封装：**
- `SendGetRankingData(Proto_CS_GetRank rInfo)`

### 4005 `LSSPROTO_PAY_LUA`

**Proto_CS_PayLua**：`Type, Mac, Plat, Id, Rmb, KUid, BuyCnt, LimitDate`

**Type / 操作名：**
- `创建订单`
- `自定订单`
- `自定配置`
- `获取配置`

**Send 封装：**
- `SendC2SRecharge(string m_Uid, string plat, int selectID)`
  - 字段赋值：Mac="pc", Plat=plat, Id=selectID, KUid=m_Uid
- `SendC2SRechargeMagicCore(string m_Uid, string plat, int rmb)`
  - 字段赋值：Mac="pc", Plat=plat, Rmb=rmb, KUid=m_Uid
- `SendS2CGetPlats(string m_Uid, RechargeTip data)`
  - 字段赋值：Id=data.ID, KUid=m_Uid
- `SendS2CGetPlatsMagicCore(string m_Uid, RechargeTip data)`
  - 字段赋值：KUid=m_Uid

### 4006 `LSSPROTO_RISK_CONTROL`

**Proto_CS_RiskControl**：`Type, Code, Phone, Status, ItemUid, OwnerCdKey, Item, Pet, Pric, PricType`

**Send 封装：**
- `SendOtherCode(string type, string code)` → Type=`type`
  - 字段赋值：Code=code

### 4008 `LSSPROTO_NEWCOMER_7DAY`

**Proto_CS_Activity**：`Type, ActivityId, Id, Code, Index, BoxIndexs, TeamName, Shopid, PVPShopNum, DrawCount, DrawSelect, TargetX, TargetY, Pid, Tid, KUid`

**Send 封装：**
- `SendSevenDay(string type, string kUid, int id)` → Type=`type`
  - 字段赋值：Id=id, KUid=kUid

### 4009 `LSSPROTO_DUNGEON_ENTER`

**Proto_CS_DungeonEnter**：`Type, Id, KUid, Times, BuyTimes`

**Type / 操作名：**
- `获取信息`

**Send 封装：**
- `SendBountyOfferedC2S(Proto_CS_Trust rInfo)`
  - 字段赋值：KUid=kuid, Id=id

### 4010 `LSSPROTO_PET_SACRIFICE`

**Proto_CS_PetSacrifice**：`Type, Index1, Index2, KUid, Petids, Status, Pet`

**Type / 操作名：**
- `获取数据`

**Send 封装：**
- `SendMonsterSacrifice(string type, string uid, int pointIndex = -1, int petIndex = -1)` → Type=`type`
  - 字段赋值：Index1=pointIndex, Index2=petIndex, KUid=uid

### 4011 `LSSPROTO_BOX_FRUITY`

**Proto_CS_FruityBox**：`Type, Coin, Count, Id, Bets, KUid, Score, BetCoin, WinScore, Shop, Icons, EndTime`

**Send 封装：**
- `SendFruityBox(string type, string uid, int coinCount = 0, int exchangeCount = 0, int exchangeId = 0, MapField<string, int> bets = null)` → Type=`type`
  - 字段赋值：KUid=uid, Coin=coinCount, Count=exchangeCount, Id=exchangeId

### 4012 `LSSPROTO_GEM_FUNCTION`

**Proto_CS_GemFunction**：`Type, Id, KUid`

**Type / 操作名：**
- `宝石升阶`

**Send 封装：**
- `SendGemMessage(string type, int gemId, string uid)` → Type=`type`
  - 字段赋值：KUid=uid, Id=gemId

### 4013 `LSSPROTO_SELECT_BOSS_BOX`

**Proto_CS_SelectBossBox**：`Type, Index, Awards1, Awards2, Awards3`

**Type / 操作名：**
- `选择宝箱`

**Send 封装：**
- `SendWorldBoss(int index)`
  - 字段赋值：Index=index

### 4014 `LSSPROTO_FAMILY_TRIAL`

**Proto_CS_FamilyTrial**：`Type, KUid, Boss, Left, Limit`

**Send 封装：**
- `SendFamilyTrial(string type)` → Type=`type`

### 4015 `LSSPROTO_FAMILY_BRAWL`

**Proto_CS_FamilyBrawl**：`Cdkey, KUid, Result, FamilyId, RankIndex, RankSocre, FamilyName`

**Send 封装：**
- `SendFamilyBrawl(string cdkey = "")`
  - 字段赋值：Cdkey=cdkey

### 4016 `LSSPROTO_FAMILY_REDPACK`

**Proto_CS_FamilyRedPacket**：`Type, Id, KUid, PetIndex, List`

**Type / 操作名：**
- `开启红包`

**Send 封装：**
- `SendRedPacketMsg(int id)`
  - 字段赋值：Id=id, KUid=PlayerDataHolder.MainPlayerUid

### 4022 `LSSPROTO_LEGACYOFTHELOST`

**Proto_CS_LegacyOfTheLost**：`Type, MinRank, MaxRank, KUid, Tops, Prize, Alist, Free, Join, Time, MyRank, DrawCount`

**Send 封装：**
- `SendMagicalRuins(string kuid, string type, int minRank = 0, int maxRank = 0)` → Type=`type`
  - 字段赋值：MinRank=minRank, MaxRank=maxRank, KUid=kuid

### 4023 `LSSPROTO_TRADE_BANK`

**Proto_CS_TradeBank**：`Type, ItemMainType, ItemSubType, PetMainType, PetSubType, ItemId, PetId, Page, PageNum, Orderid, BuyNum, ItemGrid, PetGrid, Price, Tcost, Count, SortPrice, KUid`

**Type / 操作名：**
- `上架宠物`
- `上架道具`
- `下架商品`
- `交易记录`
- `宠物搜索`
- `宠物统计`
- `宠物详情`
- `开启格子`
- `我的商品`
- `购买商品`
- `道具搜索`
- `道具统计`
- `道具详情`
- `重新上架`

**Send 封装：**
- `SendExchangeItemMsg(string type, int mainType, int subType, string kuid, int page, int sort)` → Type=`type`
  - 字段赋值：ItemMainType=mainType, ItemSubType=subType, Page=page, SortPrice=sort, KUid=kuid
- `SendExchangePetMsg(string type, int petMainType, int petSubType, string uid, int page, int sort)` → Type=`type`
  - 字段赋值：PetMainType=petMainType, PetSubType=petSubType, Page=page, SortPrice=sort, KUid=uid
- `SendExchangeSearch(string type, string uid, int page, int itemId, int petId, int sort)` → Type=`type`
  - 字段赋值：ItemId=itemId, PetId=petId, SortPrice=sort, Page=page, KUid=uid
- `SendExchangeBuyMsg(string type, string uid, long id, int num, int price)` → Type=`type`
  - 字段赋值：Orderid=id, BuyNum=num, KUid=uid, Price=price
- `SendExchangeSellMsg(string type, string uid, int num, int price, int curr, int itemIndex = -1, int petIndex = -1)` → Type=`type`
  - 字段赋值：Count=num, Price=price, ItemGrid=itemIndex, PetGrid=petIndex, Tcost=curr, KUid=uid
- `SendExchangeReSell(string type, string uid, long orderId, int price, int curr)` → Type=`type`
  - 字段赋值：Price=price, Orderid=orderId, Tcost=price, KUid=uid
- `SendExchangeNormalMsg(string type, string uid)` → Type=`type`
  - 字段赋值：KUid=uid

### 4024 `LSSPROTO_LUCK_ROCKETS`

**Proto_CS_LuckyRockets**：`Type, Count, KUid, EndTime, OneCost, Prize, Index, Lucks`

**Send 封装：**
- `SendSpaceMsg(string type, string uid, int num = 0)` → Type=`type`
  - 字段赋值：KUid=uid, Count=num

### 4025 `LSSPROTO_PLAYER_ACOUSTIC`

**Proto_CS_PlayerAcoustic**：`Msg, Id, KUid, Name`

**Send 封装：**
- `SendAcoustic(string uid, string msg, int propId)`
  - 字段赋值：KUid=uid, Msg=msg, Id=propId

### 4028 `LSSPROTO_OFFLINE_TRADE`

**Proto_CS_OfflineTrade**：`Type, Price, Items, Pets, CrystalCoin, Qq, PlayerName, AlipayAccount, Token, OrderId, KUid`

**Type / 操作名：**
- `出售记录`
- `删除订单`
- `客服介入`
- `支付成功`
- `查询订单`
- `消除红点`
- `确认发货`
- `购买记录`
- `锁定订单`

**Send 封装：**
- `SendOfflineTradeMsg(Proto_CS_OfflineTrade info)`
- `SendOfflineTradeMsg(string type, string uid, string token = "", long orderid = 0L)` → Type=`type`
  - 字段赋值：Token=token, OrderId=orderid, KUid=uid

### 4029 `LSSPROTO_HUNDRED_LAYER_BATTLE`

**Proto_CS_HundredLayerBattle**：`Type, Star, Nums, Raid, KUid, Layer, Lstar, Ltrun, Mayer, DayStar, WeekStar, LayStar, DayGets, WekGets, PassPirze, StarPirze, StarServer`

**Type / 操作名：**
- `开始挑战`
- `本周星数奖励领取`
- `本日星数奖励领取`
- `百人扫荡`
- `百人道场`
- `碎片兑换`
- `退出道场`

**Send 封装：**
- `SendHunderdDoJoMsg(string type, string uid, int star = 0, int num = 0, int raid = 0)` → Type=`type`
  - 字段赋值：KUid=uid, Star=star, Nums=num, Raid=raid

### 4030 `LSSPROTO_PET_MAX_CREST_EFFECT`

**Proto_CS_PetMaxCrestEffect**：`Type, Index, Id, KUid`

**Send 封装：**
- `SendUsePetEffect(string uid, string type, int petIndex, int Id)` → Type=`type`
  - 字段赋值：KUid=uid, Index=petIndex, Id=Id

### 4031 `LSSPROTO_LOOPY_TRIAL_FUNC`

**Proto_CS_LoopyTrial**：`Type, Name, Rank, Aid, KUid, Score`

**Type / 操作名：**
- `创建战队`
- `挑战试炼`
- `查看战队`
- `领取奖励`

**Send 封装：**
- `SendRubyTrialMsg(string type, string uid, int rank = -1, string teamName = "", int aid = 0)` → Type=`type`
  - 字段赋值：Rank=rank, Name=teamName, Aid=aid, KUid=uid

### 4032 `LSSPROTO_PET_EXCHANGE_OR_MERGE`

**Proto_CS_PetExchangeOrMerge**：`Type, Id, Cost, KUid, Idx`

**Send 封装：**
- `SendPetExchange(string uid, string type, int id, List<int> cost)` → Type=`type`
  - 字段赋值：KUid=uid, Id=id

### 4033 `LSSPROTO_JEWELRY_MERGE_FUNC`

**Proto_CS_JewelryMerge**：`Type, Id, KUid, PriceType, Team, BossIndex`

**Type / 操作名：**
- `合并首饰`
- `首饰维修`

**Send 封装：**
- `SendJewelryMergeMsg(string type, string uid, int id)` → Type=`type`
  - 字段赋值：Id=id, KUid=uid

### 4034 `LSSPROTO_PET_REFORM_MUTATION_FUNC`

**Proto_CS_ReformMutation**：`Type, Gird, Backup, KUid`

**Type / 操作名：**
- `宠物改造`
- `替换改造`

**Send 封装：**
- `SendRrfrom(string type, int gird, string uid, bool backUp)` → Type=`type`
  - 字段赋值：Gird=gird, Backup=backUp, KUid=uid

### 4035 `LSSPROTO_MONTH_CARD_PRIVILEGE_FUNC`

**Proto_CS_MonthCard**：`Type, TotalStatus, DestroyIndexMap, IsDestroyCard, IsDestroyCrystal, RepairThreshold, KUid`

**Type / 操作名：**
- `更新权益状态`
- `请求自动维修`
- `请求自动销毁`

**Send 封装：**
- `SendMonthCardStatus(string type, string uid, List<bool> status = null)` → Type=`type`
  - 字段赋值：KUid=uid
- `SendMonthCardMsg(Proto_CS_MonthCard info)`

### 4036 `LSSPROTO_BATTLE_PASS_ADVENTURE`

**Proto_CS_BattlePassAdventure**：`Type, Id, PageType, KUid, SeaId, GodId, Level, Exp, Begintime, Endtime, AdvBuy, GetsFree, GetsBuys, Tprogres, Tgstatus, TopPanel, Enable, Needlv`

**Type / 操作名：**
- `全部经验`
- `战令冒险`
- `购买等级`
- `领取奖励`
- `领取经验`

**Send 封装：**
- `SendAdventurerMsg(string type, string uid, int index = 0, string pageType = "")` → Type=`type`
  - 字段赋值：KUid=uid, Id=index, PageType=pageType

### 4038 `LSSPROTO_PET_RECYCLE_EXCHANGE`

**Proto_CS_PetRecycleExchange**：`Type, PetIndex, ItemGrid, UseCount, KUid, SuccessCount, PetRewardMap, PetShopHashs`

**Type / 操作名：**
- `使用经验丹`
- `打开收藏界面`
- `收藏宠物`

**Send 封装：**
- `SendPetCollectMsg(string type, string uid, int petIndex, int itemIndex = -1, int useItemNum = 0)` → Type=`type`
  - 字段赋值：PetIndex=petIndex, ItemGrid=itemIndex, UseCount=useItemNum, KUid=uid

### 4039 `LSSPROTO_DAILY_PASS_FUNC`

**Proto_CS_DailyPass**：`Type, Level, KUid, PassLevel, PassExp, NextLevelExp, WeekExp, WeekExpCap, PeriodEnd, AdvBuy, MonthCardOpen, GetsMonth, GetsPass, Ret, TaskInfo`

**Type / 操作名：**
- `购买等级`
- `通行证`
- `领取月卡奖励`
- `领取通行证奖励`

**Send 封装：**
- `SendDailyPass(string type, string uid, int level)` → Type=`type`
  - 字段赋值：KUid=uid, Level=level

### 4040 `LSSPROTO_SKY_DORP_RED_PACK`

**Proto_CS_SkyDropRedPack**：`Type, Id, KUid, Got, Ret, Map, OnSec, OffSec, NowSec, OnLevel`

## Proto_CS 全字段索引

- **Proto_CS_Accompany**：`Type, Id, Index, KUid, HeadInfoList, PlayerInfo`
- **Proto_CS_Action**：`X, Y, Actionno, KUid, Skillno`
- **Proto_CS_ActionSkill**：`X, Y, Skillno, KUid, Haveskillindex, Havetechindex, Toindex, Data`
- **Proto_CS_Activity**：`Type, ActivityId, Id, Code, Index, BoxIndexs, TeamName, Shopid, PVPShopNum, DrawCount, DrawSelect, TargetX, TargetY, Pid, Tid, KUid`
- **Proto_CS_AllTalk**：`Type, Data, KUid, Name, Str, Talktype, Talkcnt, Point`
- **Proto_CS_Area**：`Type, Id, Num, Haveitemindex, Flg, KUid, Level, Exp, Nexpexp, Earthnum, Waternum, Firenum, Windnum, Du, Dumax, Earth, Water, Fire, Wind, Attack, Def, Quick, Magic, Poison, Sleep, Stone, Drunk, Confusion, Amnesia, Critical, Counter, Hitrate, Avoid, Hp, Mp, ResetPoint, Neednum, Mogong, Recovery`
- **Proto_CS_Auction**：`Type, MainType, SubType, KeyWord, OwnerCdKey, ItemUid, Num, PageIndex, KUid, KeyWordList, Page, List`
- **Proto_CS_AutoBattle**：`Type, KUid, Exp, Item, StartTime, RewardList`
- **Proto_CS_AutoBattleConfig**：`Auto, Type, ActionFlgs, KUid, Autos, Stype`
- **Proto_CS_Backpack**：`Func, Type, Haveitemindex, Objindex, Num, Pass, KUid, Name, Lv, Hp, Maxhp, Fp, Maxfp, Injury, Id, AnimationDataID, Headid`
- **Proto_CS_Bank**：`Func, Type, Num, Index, IndexList, KUid, GraNo, Pile, Flg, Sort, Quality, Itemid`
- **Proto_CS_BattleCommand**：`Str, Count, Isauto, BattleTime, KUid, Index, BpFlg, Round, Uid, ItemLimit, PlayerSkillFlag, PetSkillFlag, TransformState, MaxRound, LoopyTrialLevel`
- **Proto_CS_BattlePassAdventure**：`Type, Id, PageType, KUid, SeaId, GodId, Level, Exp, Begintime, Endtime, AdvBuy, GetsFree, GetsBuys, Tprogres, Tgstatus, TopPanel, Enable, Needlv`
- **Proto_CS_BattleSpeed**：`Type, KUid, MinRank, MaxRank`
- **Proto_CS_BlindBox**：`Type, ClientIndice, BoxIndexs, KUid, Index, Item, List, DrawCount`
- **Proto_CS_BlindBoxNew1**：`Type, Times, Indexs, KUid, Show, Bag, Awards, Luck, Draw, OnceLuck, DoneLuck, MaxBag, Clear, Msg`
- **Proto_CS_BossLandChallenge**：`Type, DungeonId, ExchangeId, Count, PriceType, BuyIndex, LayerId, KUid, WeekTimes, WeekUsed, BuyTimes, DayTimes, DayUsed, Exchanges, WeekBuyLimit, OpenLevel, NextBuyTier, BuyPrice1Tiers, BuyPrice2Tiers, WeekRemain, TodayRemain, Cycle`
- **Proto_CS_BossSuppressToken**：`Type, PriceType, Team, BossIndex, KUid, X, Y`
- **Proto_CS_ChangeCharacter**：`Type, Grano, KUid, Buff`
- **Proto_CS_ChangePetName**：`Havepetindex, Name, KUid, Grano, Equips, Memo, Price, Flag, Pile, Type, Level, Durable, Itemid`
- **Proto_CS_Charlogin**：`IsRelogin, BattleIndex, MapId, X, Y, KUid, Result, Data, GmsvId, Cdkey`
- **Proto_CS_Charlogout**：`KUid, Result, Data, Test, Speed`
- **Proto_CS_Choose_Pet**：`Type, PetId, KUid, PetList, ClientIndice, BoxIndexs`
- **Proto_CS_ClientLimit**：`List, Type, Id, KUid, Times, BuyTimes`
- **Proto_CS_Config**：`Flg, KUid, X, Y, Request`
- **Proto_CS_CreatePvp**：`Str, KUid, Battleindex`
- **Proto_CS_Createchar**：`Charname, Imgno, Vital, Str, Tgh, Quick, Magic, Earth, Water, Fire, Wind, Job`
- **Proto_CS_Crystal**：`Type, Earth, Water, Fire, Wind, ExpItemType, KUid, Min, Max`
- **Proto_CS_CrystalHouse**：`Type, Times, StoreIndexs, KUid, Index, Item, Clear, Items`
- **Proto_CS_CurrencyExchange**：`Type, CurrencyType, ExchangeAmount, KUid, ExchangedCurrencyType, ExchangedCurrencyAmount, TargetCurrencyType, Rate, RateDescribe`
- **Proto_CS_Customer**：`Type, KUid, Url, Name, Id`
- **Proto_CS_DailyActivity**：`Type, Id, Id1, KUid, LeftInfo, DailyInfo, LimitListInfo, Mappoint`
- **Proto_CS_DailyActivityEx**：`Type, AwardId, KUid, Id, FinishTimes, Value, ResetType`
- **Proto_CS_DailyExpand**：`Type, Id, KUid, RecommendId, Weeks, Dailys`
- **Proto_CS_DailyPass**：`Type, Level, KUid, PassLevel, PassExp, NextLevelExp, WeekExp, WeekExpCap, PeriodEnd, AdvBuy, MonthCardOpen, GetsMonth, GetsPass, Ret, TaskInfo`
- **Proto_CS_Deletechar**：`Cdkey, Codes, Del, Deltime, Err, Tip`
- **Proto_CS_DropGold**：`X, Y, Amount, KUid, Petindex`
- **Proto_CS_DropItem**：`X, Y, Itemindex, Itemnum, KUid, Amount`
- **Proto_CS_DropPet**：`X, Y, Petindex, KUid, Fromindex, Toindex`
- **Proto_CS_DungeonEnter**：`Type, Id, KUid, Times, BuyTimes`
- **Proto_CS_DurRepaire**：`Type, Gird, KUid, Price`
- **Proto_CS_EarthMouseLottery**：`Type, KUid, Name, Rank, Title`
- **Proto_CS_EasterEgg**：`Type, Index, Nums, KUid, Layer, EndTime, List, Draw, Init, Shop, RetLayer`
- **Proto_CS_Echo**：`Test, Speed, KUid, Hoge, Time, Maxlevel`
- **Proto_CS_EnchantFunction**：`Type, Index, Index2, Index3, Punishment, Count, KUid, Success, CostCount1, CostCount2, Enchants, Lucks, Error, Status, EnchantLevel, MergeShop`
- **Proto_CS_EnvirOnment**：`Battleindex, KUid, Uid`
- **Proto_CS_EquipForce**：`Type, Gird, KUid`
- **Proto_CS_Family**：`Type, CreateFamilyInfo, Id, Code, Memo, NowPage, SubmitIndexs, AutoJoinLevel, ExperienceAddPoints, KUid`
- **Proto_CS_FamilyBattle**：`Type, CustomTimeIndex, Zone, AssistCdkey, TarCdkey, TaxRateIndex, FamilyId, OccupyFamilyId, OccupyTime, TodayTax, TaxRate, TotalTax, DamageDown, DefenceDown, OccupyFamily, List, SignedFamilyList, BattleTime, BattleFamily, Role, ACampCount, BCampCount`
- **Proto_CS_FamilyBrawl**：`Cdkey, KUid, Result, FamilyId, RankIndex, RankSocre, FamilyName`
- **Proto_CS_FamilyRedPacket**：`Type, Id, KUid, PetIndex, List`
- **Proto_CS_FamilyTrial**：`Type, KUid, Boss, Left, Limit`
- **Proto_CS_FiveCharDraw**：`Type, Id, Code, KUid, EndTime, DrawItemId, LeftPools, DrawInfos, WishLists, DrawIndex, IsCode`
- **Proto_CS_FlashSale**：`Type, Index, KUid, BuyType, OrgPrice, NowPrice, Nums, EndTime, GoodsType, Money, ChargeId, Item, GoodsId, StartTime, PayTime`
- **Proto_CS_Flurry**：`Type, Cdkey, KUid, Status, TopGoods, JoinGoods`
- **Proto_CS_FlurryPkRank**：`RankTeamList, SelfLeaderUid, MatchEndtime, KUid`
- **Proto_CS_Frame**：`Type, Id, KUid, Data, Usedata, Skin, Heads, HeadUseIndex, Barras, BarrasUseIndex, Btchaboxs, BattleChatBoxUseIndex, RideSkin, RideUseIndex, RoleHalo, HaloUseIndex, Wings, WingUseIndex`
- **Proto_CS_Friend**：`Type, Id, KUid, Friend, ItemId, Name, Pile, Num, Level, Max`
- **Proto_CS_FrontCharList**：`Index, KUid, Data, X, Y, Actionno`
- **Proto_CS_FruityBox**：`Type, Coin, Count, Id, Bets, KUid, Score, BetCoin, WinScore, Shop, Icons, EndTime`
- **Proto_CS_GemFunction**：`Type, Id, KUid`
- **Proto_CS_GetRank**：`Id, Petid, RankId, Type, Index, KUid, Func, Haveitemindex, Objindex, Num, Pass`
- **Proto_CS_HeroTrials**：`Type, Team, BossIndex, KUid, ExchangeId, CostItemId, CostCount, GiveItemId, GiveCount, HoldCount`
- **Proto_CS_HpFp**：`Type, ItemId, BagType, Pile, Rate, KUid, Time, Hp, Fp, Count, CurHp, CurFp, Cdkey`
- **Proto_CS_HundredLayerBattle**：`Type, Star, Nums, Raid, KUid, Layer, Lstar, Ltrun, Mayer, DayStar, WeekStar, LayStar, DayGets, WekGets, PassPirze, StarPirze, StarServer`
- **Proto_CS_ItemList**：`Type, ListType, PetIndex, BagIndex, KUid, List`
- **Proto_CS_ItemRecipe**：`Haveskillindex, KUid, Str, Index, Message`
- **Proto_CS_ItemUseEffect**：`Type, Gird, KUid, List`
- **Proto_CS_JewelryMerge**：`Type, Id, KUid, PriceType, Team, BossIndex`
- **Proto_CS_JobSwitch**：`Type, Index, PlayerAutoSkills, PetsAutoSkills, PlayerAutoSkillFlg, PetAutoSkillFlg, KUid, Jbid, Sort, Del`
- **Proto_CS_LayerBox**：`Type, Single, KUid, Layer, EndTime, List`
- **Proto_CS_LegacyOfTheLost**：`Type, MinRank, MaxRank, KUid, Tops, Prize, Alist, Free, Join, Time, MyRank, DrawCount`
- **Proto_CS_Lock**：`Type, PassWd, Index, Code, KUid, Plat, Name, Ip, Timestamp`
- **Proto_CS_Login**：`Cdkey, Passwd, Version, Plat, DeviceId, Device, ClientIp, DeviceInfo, ServerId, DeviceName, KUid`
- **Proto_CS_LoginSwitch**：`Type, Gmsv, KUid, Succ, Error`
- **Proto_CS_Logingate**：`KUid, Dir, Objindex, Message`
- **Proto_CS_LookBattle**：`Uid, KUid, Str, Count, Isauto, BattleTime`
- **Proto_CS_LookNpc**：`Dir, Objindex, KUid, Message`
- **Proto_CS_LoopyTrial**：`Type, Name, Rank, Aid, KUid, Score`
- **Proto_CS_Luck**：`Type, Index, Num, KUid, Icon, Log, Infos, Items, Message, Pages, PageIndexs, Iteminfo, Luck, Grano`
- **Proto_CS_LuckyRockets**：`Type, Count, KUid, EndTime, OneCost, Prize, Index, Lucks`
- **Proto_CS_Mail**：`Type, Id, KUid, Ids, Mail`
- **Proto_CS_MapEvent**：`Event, Seqno, X, Y, Dir, Mapid, Floor, KUid, Result`
- **Proto_CS_MapInfo**：`Id, Floor, X1, Y1, X2, Y2, KUid, Titles, Objs, Events`
- **Proto_CS_Menu**：`Func, Data, Callbackfunc, KUid, Index`
- **Proto_CS_MessageBox**：`Type, Btntype, Select, KUid, Index, VitalLock, StrLock, TghLock, QuickLock, MagicLock, CostType, VitalAuto, StrAuto, TghAuto, QuickAuto, MagicAuto, CostIndex, AttrIndex`
- **Proto_CS_MessageCode**：`Type, Code, KUid, Title, Data, Submitname, Cancelname`
- **Proto_CS_Misc**：`Type, Id, KUid, UseFlag, Index, ObjType, ObjIndex, Player, Hp, MaxHp, Mp, MaxMp, WalkSpeed, IsAfk`
- **Proto_CS_MonsterBreed**：`Type, ItemId, KUid, Value, Max, List, Msg`
- **Proto_CS_MonsterTower**：`Type, Layer, KUid, Times, Cycle, RewardType, Items, Cayer`
- **Proto_CS_MonthCard**：`Type, TotalStatus, DestroyIndexMap, IsDestroyCard, IsDestroyCrystal, RepairThreshold, KUid`
- **Proto_CS_MoveItem**：`Fromindex, Toindex, Num, KUid, Haveskillindex, Str`
- **Proto_CS_Multi**：`Type, Id, Mapid, Floor, X, Y, KUid, Players`
- **Proto_CS_Nearby**：`Type, Ids, KUid, Job, Teamnum, Trade, Battle, Watch, Objindex`
- **Proto_CS_OfflineCommand**：`Type, KUid, Id, Name, AnimationDataID, Flg, Level`
- **Proto_CS_OfflineTrade**：`Type, Price, Items, Pets, CrystalCoin, Qq, PlayerName, AlipayAccount, Token, OrderId, KUid`
- **Proto_CS_OnLineInfo**：`LineId, KUid, WarpId, Result`
- **Proto_CS_OperationPet**：`Pet1, Pet2, Pet3, Pet4, Pet5, KUid, Index`
- **Proto_CS_OperationTeam**：`X, Y, Request, KUid, Result`
- **Proto_CS_OtherCode**：`Type, KUid, Index, Grano, Pile`
- **Proto_CS_OtherEquip**：`Type, Gird, KUid, Item`
- **Proto_CS_PK**：`Type, KUid, Context, Teams, Gamename, Id, Time, Teamname, Name16S, Name8S, Name4S, Name2S, Name1, Jiangli`
- **Proto_CS_PayLua**：`Type, Mac, Plat, Id, Rmb, KUid, BuyCnt, LimitDate`
- **Proto_CS_PetBond**：`Type, Index, BondPetIndex, CostPetIndex, Level, KUid`
- **Proto_CS_PetBook**：`Type, Id, KUid, Data1, Data2, Data3`
- **Proto_CS_PetCodeWindows**：`Type, KUid, Title, PetIndex, Awards`
- **Proto_CS_PetEquip**：`Type, EquipPos, Havepetindex, AutoDecompose, KUid`
- **Proto_CS_PetExchangeOrMerge**：`Type, Id, Cost, KUid, Idx`
- **Proto_CS_PetMaxCrestEffect**：`Type, Index, Id, KUid`
- **Proto_CS_PetMgr**：`Type, Pass, Havepetindex, Vital, Str, Tgh, Quick, Magic, Index, Lock`
- **Proto_CS_PetMutation**：`Type, Index, BagIndex, KUid, List, Critical, Counter, Hitrate, Avoid`
- **Proto_CS_PetPoint**：`Type, Havepetindex, Vital, Str, Tgh, Quick, Magic, Fromindex, Toindex, KUid, Msg`
- **Proto_CS_PetRecycleExchange**：`Type, PetIndex, ItemGrid, UseCount, KUid, SuccessCount, PetRewardMap, PetShopHashs`
- **Proto_CS_PetRefactorDestruct**：`Type, Id, KUid, PetIndex, ItemGrid, UseCount`
- **Proto_CS_PetReset**：`Type, Index, VitalLock, StrLock, TghLock, QuickLock, MagicLock, CostType, VitalAuto, StrAuto, TghAuto, QuickAuto, MagicAuto, CostIndex, AttrIndex, KUid`
- **Proto_CS_PetResetBase**：`Type, Index, VitalAuto, StrAuto, TghAuto, QuickAuto, MagicAuto, KUid, PetId, Level`
- **Proto_CS_PetSacrifice**：`Type, Index1, Index2, KUid, Petids, Status, Pet`
- **Proto_CS_PetSelectWindows**：`Type, PetIndex, KUid, List`
- **Proto_CS_PetUpStar**：`Type, Index, KUid, List, PassWd, Code`
- **Proto_CS_Pet_Ride**：`Type, Tribe, Index, KUid, PetRideMap`
- **Proto_CS_PickupItem**：`Objindex, KUid, X, Y, Itemindex, Itemnum`
- **Proto_CS_PlayerAcoustic**：`Msg, Id, KUid, Name`
- **Proto_CS_PlayerBuff**：`Type, KUid, Info, Gold, Fervertype, Fervertime, Moshi, BattleSpeedTime, HpfpHp, HpfpFp, HpfpMaxhp, HpfpMaxfp, HpfpStatus, HpStatus, FpStatus`
- **Proto_CS_PlayerEffect**：`Type, KUid, Ids, Lives, MapId, Floor, X, Y`
- **Proto_CS_PlayerFixDungeon**：`MapId, Floor, X, Y, KUid, Type, ItemId`
- **Proto_CS_PlayerMgr**：`Type, Vital, Str, Tgh, Quick, Magic, Pass, Code, Func`
- **Proto_CS_PlayerPoint**：`Type, Vital, Str, Tgh, Quick, Magic, Passwd, Bpindex, Name, Earth, Water, Fire, Wind, KUid`
- **Proto_CS_PlayerSkills**：`Type, KUid, Buff`
- **Proto_CS_Pos**：`Pos, KUid, Havepetindex, Name`
- **Proto_CS_Potential**：`Type, Havepetindex, Index1, Index2, KUid, Dire`
- **Proto_CS_Pray**：`Type, Id, KUid, PrayInfoMap, UpdateData, Point, PrayNum`
- **Proto_CS_Production**：`Type, Haveitemindex, Haveitemindex2, Haveitemindex3, Flg, KUid, Level, Exp, Nextexp, Gold, Enchants, Lucks, Error, Crystal, Rate, Status, PerfectGold`
- **Proto_CS_Recipe**：`Type, Id, Haveitemindex, Num, ItemCostType, MakeStoreType, QualityItemIndex, FirstGoodIndex, GetQuality, KUid, Data1, Data2, Item`
- **Proto_CS_ReformMutation**：`Type, Gird, Backup, KUid`
- **Proto_CS_RepairBattle**：`KUid, Players, Time, List`
- **Proto_CS_ResetTask**：`Type, Id, KUid, Name`
- **Proto_CS_Returnbattle**：`KUid, Func, Data, Callbackfunc, Index`
- **Proto_CS_RiskControl**：`Type, Code, Phone, Status, ItemUid, OwnerCdKey, Item, Pet, Pric, PricType`
- **Proto_CS_Run**：`Flg, KUid, Id1, Id2`
- **Proto_CS_SelectBossBox**：`Type, Index, Awards1, Awards2, Awards3`
- **Proto_CS_SelectObj**：`Type, Index, Append1, Append2, KUid, Id, Time, Buff, Pric, Flg, PricType, ItemType, Item, Pet`
- **Proto_CS_SetSpecialFlag**：`KUid, CurrencyType, Gold, Coins`
- **Proto_CS_Shop**：`Type, Infos, TabIndex, Curr, KUid, Name, Index, CurrencyType, IsSpecial`
- **Proto_CS_ShovelTreasure**：`Type, Dire, KUid, PetId`
- **Proto_CS_Skill**：`Func, Type, Petindex, Skillindex, Objindex, Buyindex, KUid, Index, Name, CurrencyType, Gold, Slot, Info, Icon, Level, Mp`
- **Proto_CS_SkillExchange**：`Srcindex, Dstindex, KUid, Pos`
- **Proto_CS_SkinLevelUp**：`Type, Index, KUid, Skin, Times, StoreIndexs`
- **Proto_CS_SkyDropRedPack**：`Type, Id, KUid, Got, Ret, Map, OnSec, OffSec, NowSec, OnLevel`
- **Proto_CS_SoldierOfHonor**：`Type, KUid, Id, PriceType, Team, BossIndex`
- **Proto_CS_Street**：`Type, List, Buylist, Id, Page, KUid, Status, Info, Loginfo, SkinList, Time, Pric, Name, Owner, Pages, SearchList`
- **Proto_CS_Talk**：`Index, Message, KUid, Ctype, Num, Operation, Grano, Name, Id`
- **Proto_CS_TaskWarp**：`WarpId, KUid, Result, Type, Cdkey`
- **Proto_CS_Team**：`Type, Id, IsAfk, KUid, FamilyFlag, FriendFlag, CustomizeFlag, NameList`
- **Proto_CS_TeamConfig**：`Type, Config, KUid, Hp, Fp, Atk, Def, Quick, Poison, Sleep, Stone, Drunk, Confusion, Amnesia, Critical, Counter, Hit, Avoid`
- **Proto_CS_TestUID**：`KUid, Channel, Vitalbase, Strbase, Tghbase, Quickbase, Magicbase, Luck, UseFlag, VitalLock, StrLock, TghLock, QuickLock, MagicLock, UnlockNum, IsReset, VitalAuto, StrAuto, TghAuto, QuickAuto, MagicAuto, GroomCostType, GroomCostId, GroomCostCount, SupplementLevel, SupplementCost, GuaranteeFullRemain`
- **Proto_CS_TitleEx**：`Type, Id, Name, KUid, Use, Data, Custom`
- **Proto_CS_Trade**：`Type, Id, List, KUid, Name, Role, Objdata, Cdkey, SameAccount, TradeItemLimit`
- **Proto_CS_TradeBank**：`Type, ItemMainType, ItemSubType, PetMainType, PetSubType, ItemId, PetId, Page, PageNum, Orderid, BuyNum, ItemGrid, PetGrid, Price, Tcost, Count, SortPrice, KUid`
- **Proto_CS_Trust**：`Type, RefreshTaskIdx, RefreshType, RewardIdx, PetIdx, BagIndexs, KUid, Star, Finished, GoAward, Awards, Petidx, Itemidx, Npcidx, FastIds`
- **Proto_CS_UpdateObj**：`Objindex, KUid, Number, Data, X, Y, Act, Dir, Effect, Effectarg1, Effectarg2, Effectarg3, Effectarg4, Effectarg5, Effectarg6, Effectarg7, Index, Use, Orderid, ActiveCamp`
- **Proto_CS_UpdatePlayer**：`Id, KUid, Pet, Type, Index, Pets, Useflg`
- **Proto_CS_UseCurrencyTip**：`CurrencyType, KUid, Gold, Coins, ModeName, TickTimes, TotalCost, MinCost, MaxCost, NoSleepTimes, IsTotal, Gcsize, Gccount, RunCnt, Gcmen`
- **Proto_CS_UseItem**：`X, Y, Haveitemindex, Toindex, Usecount, Selectindex, KUid, Objindex`
- **Proto_CS_UseTech**：`Haveskillindex, Havetechindex, Toindex, Data, KUid, Result, Type`
- **Proto_CS_Verify**：`Flg, Str, KUid, GraNo, Name, Memo, Level`
- **Proto_CS_Vigor**：`Type, KUid, Vigor, Crystal, Message`
- **Proto_CS_Walk**：`X, Y, Direction, Mapid, Floor, KUid`
- **Proto_CS_Watch**：`Type, Floor, Page, KUid, Name1, Name2, Time, File`
- **Proto_CS_WatchBattle**：`Type, Objindex, Index, Msg, KUid, Watchs, GraNo`
- **Proto_CS_Windows**：`X, Y, Seqno, Objindex, Select, Data, WindowsType, Curr, KUid, ButtonType, Name, AniId, Extensions`
- **Proto_CS_WorldBoss**：`Type, KUid, RankStart, RankEnd, Title, Names, Damage`

## 全局 Type / 操作名字符串

- `CDKey兑换`
- `E`
- `G`
- `H|`
- `M|FF`
- `N`
- `NPC传送`
- `NPC全体治疗`
- `NPC单体治疗`
- `P`
- `PK切磋`
- `S|`
- `U`
- `W|`
- `W|FF|FF`
- `clearplayerbag`
- `delpet all`
- `delstreet `
- `giftCode`
- `superman`
- `warp 0 `
- `warp 1 `
- `一键修理`
- `一键修理价格`
- `一键加入`
- `一键召唤`
- `一键鉴定`
- `一键领取`
- `上架宠物`
- `上架摊位`
- `上架道具`
- `下架商品`
- `丢弃道具`
- `交易记录`
- `仓库信息`
- `仓库删除`
- `仓库取出`
- `任务刷星`
- `任务奖励`
- `任务扫荡`
- `休息`
- `传送管理员`
- `使用头像框`
- `使用称号`
- `使用经验丹`
- `使用经验道具`
- `使用聊天框`
- `使用观战弹幕框`
- `使用道具`
- `使用道具到指定对象`
- `修复耐久`
- `修改BP名字`
- `修改名字`
- `修改商品`
- `修改图标`
- `修改工会介绍`
- `修改自定义称号`
- `修理单件`
- `停止招募`
- `停止挂机`
- `停止采集`
- `免审等级`
- `兑换`
- `兑换列表`
- `兑换奖励`
- `全部取出`
- `全部经验`
- `关闭交易`
- `关闭加速`
- `关闭称号显示`
- `关闭血池`
- `关闭银行`
- `关闭魔池`
- `出售记录`
- `出战`
- `分享排行榜`
- `分解装备`
- `切换BP`
- `切换历练分页`
- `切换角色`
- `列表信息`
- `创建家族`
- `创建战队`
- `创建订单`
- `创建队伍`
- `删除商品`
- `删除好友`
- `删除物品`
- `删除职业`
- `删除订单`
- `删除邮件`
- `制造道具`
- `加入队伍`
- `加点`
- `升星卡合成`
- `升级装备`
- `单个修理`
- `单个修理价格`
- `卸下头饰`
- `卸下皮肤`
- `卸下翅膀`
- `卸下装备`
- `卸下观战弹幕框`
- `卸下角色光环`
- `卸下骑宠皮肤`
- `历练信息`
- `历练加点`
- `发送喇叭`
- `取消道具变身`
- `召唤角色`
- `合并首饰`
- `同意好友`
- `商品列表`
- `商店列表`
- `增加商品`
- `增加道具栏`
- `外援邀请`
- `大厅信息`
- `头像切换角色`
- `头像数据`
- `头饰列表`
- `奖励查看`
- `宝石升阶`
- `宠物技能换位`
- `宠物换位`
- `宠物排行宠物信息`
- `宠物排行排名列表`
- `宠物搜索`
- `宠物改造`
- `宠物洗档`
- `宠物统计`
- `宠物详情`
- `审核成员`
- `客服介入`
- `客服地址`
- `家族信息`
- `家族列表`
- `对象数据`
- `建筑升级`
- `开关掉落`
- `开启加速`
- `开启宝匣`
- `开启格子`
- `开启红包`
- `开启血池`
- `开启魔池`
- `开始交易`
- `开始抽奖`
- `开始挂机`
- `开始挑战`
- `开始采集`
- `开通BP`
- `开通储存`
- `忘记密码`
- `成员列表`
- `我的商品`
- `战令冒险`
- `手机验证`
- `打开PK比赛`
- `打开收藏界面`
- `批量删除`
- `批量取出`
- `抽取宠物`
- `抽奖`
- `拆分道具`
- `拉黑好友`
- `招募成员`
- `挂机传送`
- `指定目标`
- `挑战BOSS`
- `挑战试炼`
- `捐献`
- `捐献奖励`
- `排行信息`
- `排行列表`
- `接受邀请`
- `提交任务`
- `提交宠物`
- `提交资金费`
- `搜索摆摊`
- `摊位状态`
- `支付成功`
- `收藏宠物`
- `改变骑乘状态`
- `放入需要道具`
- `整理卡仓`
- `整理背包`
- `新增技能栏`
- `暂停挂机`
- `更改密码`
- `更改形像魔币`
- `更改形像魔晶`
- `更新权益状态`
- `更新自动维修`
- `更新自动销毁`
- `替换改造`
- `替换洗档`
- `月卡治疗`
- `本周星数奖励领取`
- `本日星数奖励领取`
- `查看战队`
- `查看摊位`
- `查询红包`
- `查询订单`
- `标记`
- `每日奖励`
- `每日签到`
- `比赛报名`
- `水晶数据`
- `水晶配置`
- `注入碎片`
- `活力信息`
- `活动信息`
- `活动列表`
- `消除红点`
- `添加好友`
- `玩家BUFF数据`
- `申请加入`
- `登出角色`
- `登陆角色`
- `百人扫荡`
- `百人道场`
- `皮肤升级`
- `瞬间移动`
- `短信验证`
- `研究所信息`
- `确认交易`
- `确认发货`
- `碎片兑换`
- `祈愿升级`
- `离开队伍`
- `科技升级`
- `穿戴装备`
- `累充奖励领取`
- `累计在线奖励领取`
- `练级达人奖励领取`
- `组队设置`
- `结束计时`
- `继续挂机`
- `翅膀列表`
- `职业切换`
- `自动战斗`
- `自动技能设置`
- `自定订单`
- `自定配置`
- `荣耀升级`
- `获取BP名字`
- `获取信息`
- `获取多控`
- `获取头像框数据`
- `获取数据`
- `获取皮肤数据`
- `获取称号数据`
- `获取聊天框数据`
- `获取血池道具`
- `获取观战弹幕框`
- `获取配置`
- `获取魔池道具`
- `血池阈值`
- `血魔池状态`
- `血魔池记录`
- `血魔池设置`
- `装备头饰`
- `装备标记`
- `装备皮肤`
- `装备翅膀`
- `装备角色光环`
- `装备附魔`
- `装备骑宠皮肤`
- `观战`
- `观战信息`
- `观战弹幕`
- `角色光环列表`
- `解散工会`
- `解散队伍`
- `许愿`
- `设为副会长`
- `设为骨干`
- `设置密码`
- `设置离线命令`
- `设置税率`
- `设置连续`
- `请求交易`
- `请求数据`
- `请求日志`
- `请求组队设置`
- `请求自动技能设置`
- `请求自动维修`
- `请求自动销毁`
- `读取邮件`
- `调整属性`
- `购买历练分页`
- `购买历练点`
- `购买商品`
- `购买头像框`
- `购买头饰`
- `购买彩票`
- `购买次数`
- `购买皮肤`
- `购买等级`
- `购买翅膀`
- `购买聊天框`
- `购买观战弹幕框`
- `购买角色光环`
- `购买记录`
- `购买骑宠皮肤`
- `资源下载`
- `踢出工会`
- `踢出队伍`
- `转移会长`
- `转让队伍`
- `远程个人仓库`
- `远程个人宠物仓库`
- `远程个人道具仓库`
- `远程出售魔石`
- `远程账号宠物仓库`
- `远程账号道具仓库`
- `退出工会`
- `退出道场`
- `选择宝箱`
- `选择宠物`
- `选择幸运符`
- `选择装备`
- `选择附魔石`
- `通行证`
- `道具搜索`
- `道具统计`
- `道具详情`
- `邀请组队`
- `重叠站位`
- `重新上架`
- `重置BP`
- `重置历练分页`
- `重置属性`
- `重置抽奖`
- `鉴定道具`
- `锁定交易`
- `锁定订单`
- `镶嵌宝石`
- `队伍召集`
- `领取BOSS奖励`
- `领取奖励`
- `领取月卡奖励`
- `领取每日奖励`
- `领取经验`
- `领取许愿奖励`
- `领取通行证奖励`
- `领取邮件`
- `领取集字纳福奖励`
- `首饰维修`
- `验证密码`
- `骑宠皮肤列表`
- `魔池阈值`
