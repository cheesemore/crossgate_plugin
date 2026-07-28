# SeqChapterPluginHost

**状态：实验搁置。** 百科自绘面板方案实机不可行，主线勿依赖本 DLL。  
设计存档见：`tools/hotfix_patcher/MULTI_DLL_HOST_PLAN.md`

Pause 唯一加载入口 + 侧栏百科打开自绘功能面板（一期骨架，未作为发布默认）。

## 补丁命令

```bat
HotfixPatcher plugin-host-patch --hotfix <orig> --output <hotfix>
HotfixPatcher plugin-host-patch --hotfix <hotfix> --detect
```

GUI：「战斗扩展」→「插件 Host·实验」（与其它扩展 DLL 互斥）
