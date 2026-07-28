# SeqChapterPluginHost

Pause 唯一加载入口 + 侧栏百科打开自绘功能面板。

设计见：`tools/hotfix_patcher/MULTI_DLL_HOST_PLAN.md`

## 补丁命令

```bat
HotfixPatcher plugin-host-patch --hotfix <orig> --output <hotfix>
HotfixPatcher plugin-host-patch --hotfix <hotfix> --detect
```

GUI：「战斗扩展」→「插件 Host·实验」

## 第一期范围

- 加载 `SeqChapterPluginHost.dll.bytes`
- 点百科 → 最高层级 UGUI 面板（占位勾选 + hangup 互斥 Tip）
- 与其它扩展 DLL 仍互斥（二期再多加载）
