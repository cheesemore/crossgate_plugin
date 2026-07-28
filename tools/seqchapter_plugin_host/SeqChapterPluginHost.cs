// 序章插件 Host：Pause 唯一加载入口；百科打开自绘面板。
// 编译只引用 hotfixdata 内 mscorlib/system；Unity / UI 全部运行时反射。
// 部署为 hotfixdata/SeqChapterPluginHost.dll.bytes

using System;
using System.Collections.Generic;
using System.Reflection;

/// <summary>
/// Plugin Host 第一期：加载自身 + 打开最高层级自绘面板（占位勾选 + hangup 互斥）。
/// </summary>
public static class SeqChapterPluginHost
{
    public const string AssetPath = "hotfixdata/SeqChapterPluginHost.dll.bytes";

    private const string HangupGroup = "hangup";
    private const int CanvasSortOrder = 32767;

    private static bool _bootstrapped;
    private static object _rootGo; // UnityEngine.GameObject
    private static bool _visible;
    private static readonly Dictionary<string, bool> _enabled = new Dictionary<string, bool>(StringComparer.Ordinal);
    private static readonly Dictionary<string, object> _toggles = new Dictionary<string, object>(StringComparer.Ordinal);
    private static object _cachedFont;

    private static readonly FeatureDef[] Features =
    {
        new FeatureDef("seal", "自动烧卡", HangupGroup, "二期接入：烧卡逻辑"),
        new FeatureDef("catch", "自动抓宠", HangupGroup, "二期接入：抓宠逻辑"),
        new FeatureDef("sell", "盗贼辅助", HangupGroup, "二期接入：退战卖魔石"),
        new FeatureDef("nine", "神奇九动·DLL", null, "二期接入：由 Host 加载九动 DLL"),
    };

    private sealed class FeatureDef
    {
        public readonly string Id;
        public readonly string Title;
        public readonly string ConflictGroup; // null = 无组冲突
        public readonly string PlaceholderTip;

        public FeatureDef(string id, string title, string conflictGroup, string placeholderTip)
        {
            Id = id;
            Title = title;
            ConflictGroup = conflictGroup;
            PlaceholderTip = placeholderTip;
        }
    }

    public static void Bootstrap()
    {
        if (_bootstrapped)
        {
            return;
        }

        _bootstrapped = true;
        foreach (var f in Features)
        {
            _enabled[f.Id] = false;
        }

        Tip("序章插件 Host 已加载（点百科打开面板）");
    }

    /// <summary>MapSidebarPanel.OnClickWiki：打开/关闭插件面板。</summary>
    public static void OnWikiClick()
    {
        try
        {
            Bootstrap();
            if (_visible && IsRootAlive())
            {
                SetPanelVisible(false);
                return;
            }

            EnsurePanel();
            SetPanelVisible(true);
        }
        catch (Exception ex)
        {
            Tip("插件面板打开失败: " + ex.GetType().Name + ": " + RootMessage(ex));
        }
    }

    public static bool IsEnabled(string id)
    {
        bool on;
        return _enabled.TryGetValue(id, out on) && on;
    }

    /// <summary>面板勾选入口；冲突组内自动关旧的。</summary>
    public static bool SetEnabled(string id, bool on)
    {
        FeatureDef def = null;
        for (var i = 0; i < Features.Length; i++)
        {
            if (Features[i].Id == id)
            {
                def = Features[i];
                break;
            }
        }

        if (def == null)
        {
            return false;
        }

        if (on && !string.IsNullOrEmpty(def.ConflictGroup))
        {
            for (var i = 0; i < Features.Length; i++)
            {
                var other = Features[i];
                if (other.Id == id || other.ConflictGroup != def.ConflictGroup)
                {
                    continue;
                }

                if (IsEnabled(other.Id))
                {
                    _enabled[other.Id] = false;
                    SyncToggleUi(other.Id, false);
                    Tip("已关闭「" + other.Title + "」（与「" + def.Title + "」互斥）");
                }
            }
        }

        _enabled[id] = on;
        SyncToggleUi(id, on);
        if (on)
        {
            Tip(def.PlaceholderTip);
        }
        else
        {
            Tip("已关闭「" + def.Title + "」");
        }

        return true;
    }

    private static void EnsurePanel()
    {
        if (IsRootAlive())
        {
            return;
        }

        _rootGo = null;
        _toggles.Clear();
        _visible = false;

        try
        {
            EnsurePanelCore();
        }
        catch (Exception ex)
        {
            _rootGo = null;
            _toggles.Clear();
            _visible = false;
            throw;
        }
    }

    private static void EnsurePanelCore()
    {
        var goType = RequireType("UnityEngine.GameObject");
        var objectType = RequireType("UnityEngine.Object");

        // 先普通 GO，再挂 Canvas（Unity 会把 Transform 升级成 RectTransform）
        _rootGo = Activator.CreateInstance(goType, new object[] { "SeqChapterPluginHostPanel" });
        CallStatic(objectType, "DontDestroyOnLoad", new[] { objectType }, new[] { _rootGo });

        var canvas = AddComponent(_rootGo, "UnityEngine.Canvas");
        SetProp(canvas, "renderMode", EnumValue("UnityEngine.RenderMode", "ScreenSpaceOverlay", 0));
        SetProp(canvas, "overrideSorting", true);
        SetProp(canvas, "sortingOrder", CanvasSortOrder);

        AddComponent(_rootGo, "UnityEngine.UI.CanvasScaler");
        AddComponent(_rootGo, "UnityEngine.UI.GraphicRaycaster");
        var group = AddComponent(_rootGo, "UnityEngine.CanvasGroup");
        SetProp(group, "blocksRaycasts", true);
        SetProp(group, "interactable", true);

        var rootRt = RequireRect(_rootGo, "root");
        StretchFull(rootRt);

        // 半透明遮罩
        var dim = CreateUiChild(_rootGo, "Dim");
        var dimImg = AddComponent(dim, "UnityEngine.UI.Image");
        SetColor(dimImg, 0f, 0f, 0f, 0.55f);
        StretchFull(RequireRect(dim, "dim"));
        BindButton(dim, dimImg, () => SetPanelVisible(false));

        // 中心面板
        var panel = CreateUiChild(_rootGo, "Panel");
        SetAnchoredCenter(RequireRect(panel, "panel"), 420f, 460f);
        var panelImg = AddComponent(panel, "UnityEngine.UI.Image");
        SetColor(panelImg, 0.12f, 0.14f, 0.18f, 0.96f);

        var title = CreateUiChild(panel, "Title");
        SetAnchoredTop(RequireRect(title, "title"), 0f, -16f, 360f, 36f);
        ConfigureText(AddComponent(title, "UnityEngine.UI.Text"), "序章插件", 22, true);

        var close = CreateUiChild(panel, "Close");
        SetAnchoredTopRight(RequireRect(close, "close"), -12f, -12f, 56f, 32f);
        var closeImg = AddComponent(close, "UnityEngine.UI.Image");
        SetColor(closeImg, 0.35f, 0.2f, 0.2f, 1f);
        var closeLabelGo = CreateUiChild(close, "Label");
        StretchFull(RequireRect(closeLabelGo, "closeLabel"));
        ConfigureText(AddComponent(closeLabelGo, "UnityEngine.UI.Text"), "关闭", 16, true);
        BindButton(close, closeImg, () => SetPanelVisible(false));

        var hint = CreateUiChild(panel, "Hint");
        SetAnchoredTop(RequireRect(hint, "hint"), 0f, -56f, 380f, 40f);
        ConfigureText(
            AddComponent(hint, "UnityEngine.UI.Text"),
            "烧卡 / 抓宠 / 盗贼 不能同时开启（互斥）",
            14,
            false);

        float y = -110f;
        for (var i = 0; i < Features.Length; i++)
        {
            var f = Features[i];
            var row = CreateUiChild(panel, "Row_" + f.Id);
            SetAnchoredTop(RequireRect(row, "row:" + f.Id), 0f, y, 380f, 36f);
            var rowImg = AddComponent(row, "UnityEngine.UI.Image");
            SetColor(rowImg, 0.18f, 0.2f, 0.24f, 1f);

            var labelGo = CreateUiChild(row, "Label");
            StretchFull(RequireRect(labelGo, "rowLabel:" + f.Id));
            var label = AddComponent(labelGo, "UnityEngine.UI.Text");
            _toggles[f.Id] = label; // 复用字典：存行标题 Text，便于刷新开关状态
            ConfigureText(label, FormatFeatureLabel(f), 16, false);

            var captured = f;
            BindButton(row, rowImg, () => SetEnabled(captured.Id, !IsEnabled(captured.Id)));

            y -= 44f;
        }

        var foot = CreateUiChild(panel, "Foot");
        SetAnchoredTop(RequireRect(foot, "foot"), 0f, y - 8f, 380f, 48f);
        ConfigureText(
            AddComponent(foot, "UnityEngine.UI.Text"),
            "第一期骨架：点百科开关本面板；点行切换功能（二期接逻辑）。",
            13,
            false);
    }

    private static string FormatFeatureLabel(FeatureDef f)
    {
        var on = IsEnabled(f.Id) ? "开" : "关";
        var suffix = string.IsNullOrEmpty(f.ConflictGroup) ? "" : " [互斥]";
        return "[" + on + "] " + f.Title + suffix;
    }

    private static void SetPanelVisible(bool visible)
    {
        if (!IsRootAlive())
        {
            _visible = false;
            return;
        }

        _visible = visible;
        var m = _rootGo.GetType().GetMethod("SetActive", BindingFlags.Instance | BindingFlags.Public);
        if (m != null)
        {
            m.Invoke(_rootGo, new object[] { visible });
        }
    }

    private static bool IsRootAlive()
    {
        if (_rootGo == null)
        {
            return false;
        }

        try
        {
            // Unity fake-null: destroyed objects compare equal to null via op_Inequality
            var objectType = FindType("UnityEngine.Object");
            if (objectType == null)
            {
                return false;
            }

            var op = objectType.GetMethod(
                "op_Inequality",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { objectType, objectType },
                null);
            if (op != null)
            {
                return (bool)op.Invoke(null, new object[] { _rootGo, null });
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SyncToggleUi(string id, bool on)
    {
        object label;
        if (!_toggles.TryGetValue(id, out label) || label == null)
        {
            return;
        }

        FeatureDef def = null;
        for (var i = 0; i < Features.Length; i++)
        {
            if (Features[i].Id == id)
            {
                def = Features[i];
                break;
            }
        }

        if (def == null)
        {
            return;
        }

        // on 已写入 _enabled，直接按当前状态刷新文案
        ConfigureText(label, FormatFeatureLabel(def), 16, false);
    }

    private static void BindButton(object go, object targetGraphic, Action action)
    {
        var btn = AddComponent(go, "UnityEngine.UI.Button");
        if (targetGraphic != null)
        {
            SetProp(btn, "targetGraphic", targetGraphic);
        }

        var onClick = GetProp(btn, "onClick");
        if (onClick == null)
        {
            throw new InvalidOperationException("Button.onClick 为空");
        }

        var actionType = RequireType("UnityEngine.Events.UnityAction");
        var holder = new ClickHolder(action);
        var del = Delegate.CreateDelegate(actionType, holder, "Invoke");
        var add = onClick.GetType().GetMethod("AddListener", new[] { actionType })
            ?? throw new InvalidOperationException("找不到 UnityEvent.AddListener");
        add.Invoke(onClick, new object[] { del });
    }

    private sealed class ClickHolder
    {
        private readonly Action _action;

        public ClickHolder(Action action)
        {
            _action = action;
        }

        public void Invoke()
        {
            try
            {
                _action();
            }
            catch (Exception ex)
            {
                Tip("面板按钮异常: " + RootMessage(ex));
            }
        }
    }

    private static object CreateUiChild(object parent, string name)
    {
        var goType = RequireType("UnityEngine.GameObject");
        var child = Activator.CreateInstance(goType, new object[] { name });
        var transform = GetProp(child, "transform")
            ?? throw new InvalidOperationException("子节点 transform 为空:" + name);
        var parentTransform = GetProp(parent, "transform")
            ?? throw new InvalidOperationException("父节点 transform 为空");
        var setParent = transform.GetType().GetMethod(
            "SetParent",
            new[] { RequireType("UnityEngine.Transform"), typeof(bool) });
        if (setParent == null)
        {
            throw new InvalidOperationException("找不到 Transform.SetParent");
        }

        setParent.Invoke(transform, new object[] { parentTransform, false });

        var localScale = FindType("UnityEngine.Vector3");
        if (localScale != null)
        {
            var one = localScale.GetField("one", BindingFlags.Public | BindingFlags.Static);
            if (one != null)
            {
                SetProp(transform, "localScale", one.GetValue(null));
            }
        }

        return child;
    }

    private static object RequireRect(object go, string tag)
    {
        var rt = GetComp(go, "UnityEngine.RectTransform");
        if (rt != null)
        {
            return rt;
        }

        // 挂到 Canvas 下后，有时仅能通过 transform 拿到已升级的 RectTransform
        var tr = GetProp(go, "transform");
        if (tr != null && tr.GetType().Name.IndexOf("RectTransform", StringComparison.Ordinal) >= 0)
        {
            return tr;
        }

        throw new InvalidOperationException("没有 RectTransform:" + tag);
    }

    private static Type RequireType(string fullName)
    {
        return FindType(fullName) ?? throw new InvalidOperationException("找不到类型 " + fullName);
    }

    private static object Vec2(float x, float y)
    {
        var v2 = RequireType("UnityEngine.Vector2");
        return Activator.CreateInstance(v2, new object[] { x, y });
    }

    private static object Vec2Zero()
    {
        var v2 = RequireType("UnityEngine.Vector2");
        var f = v2.GetField("zero", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Vector2.zero 缺失");
        return f.GetValue(null);
    }

    private static object Vec2One()
    {
        var v2 = RequireType("UnityEngine.Vector2");
        var f = v2.GetField("one", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Vector2.one 缺失");
        return f.GetValue(null);
    }

    private static void StretchFull(object rt)
    {
        if (rt == null)
        {
            throw new InvalidOperationException("StretchFull rt 为空");
        }

        SetProp(rt, "anchorMin", Vec2Zero());
        SetProp(rt, "anchorMax", Vec2One());
        SetProp(rt, "offsetMin", Vec2Zero());
        SetProp(rt, "offsetMax", Vec2Zero());
        SetProp(rt, "pivot", Vec2(0.5f, 0.5f));
    }

    private static void SetAnchoredCenter(object rt, float w, float h)
    {
        if (rt == null)
        {
            throw new InvalidOperationException("SetAnchoredCenter rt 为空");
        }

        var half = Vec2(0.5f, 0.5f);
        SetProp(rt, "anchorMin", half);
        SetProp(rt, "anchorMax", half);
        SetProp(rt, "pivot", half);
        SetProp(rt, "sizeDelta", Vec2(w, h));
        SetProp(rt, "anchoredPosition", Vec2Zero());
    }

    private static void SetAnchoredTop(object rt, float x, float y, float w, float h)
    {
        if (rt == null)
        {
            throw new InvalidOperationException("SetAnchoredTop rt 为空");
        }

        SetProp(rt, "anchorMin", Vec2(0.5f, 1f));
        SetProp(rt, "anchorMax", Vec2(0.5f, 1f));
        SetProp(rt, "pivot", Vec2(0.5f, 1f));
        SetProp(rt, "sizeDelta", Vec2(w, h));
        SetProp(rt, "anchoredPosition", Vec2(x, y));
    }

    private static void SetAnchoredTopRight(object rt, float x, float y, float w, float h)
    {
        if (rt == null)
        {
            throw new InvalidOperationException("SetAnchoredTopRight rt 为空");
        }

        SetProp(rt, "anchorMin", Vec2(1f, 1f));
        SetProp(rt, "anchorMax", Vec2(1f, 1f));
        SetProp(rt, "pivot", Vec2(1f, 1f));
        SetProp(rt, "sizeDelta", Vec2(w, h));
        SetProp(rt, "anchoredPosition", Vec2(x, y));
    }

    private static void SetAnchoredLeft(object rt, float x, float y, float w, float h)
    {
        if (rt == null)
        {
            throw new InvalidOperationException("SetAnchoredLeft rt 为空");
        }

        SetProp(rt, "anchorMin", Vec2(0f, 0.5f));
        SetProp(rt, "anchorMax", Vec2(0f, 0.5f));
        SetProp(rt, "pivot", Vec2(0f, 0.5f));
        SetProp(rt, "sizeDelta", Vec2(w, h));
        SetProp(rt, "anchoredPosition", Vec2(x, y));
    }

    private static void ConfigureText(object text, string content, int fontSize, bool bold)
    {
        SetProp(text, "text", content);
        SetProp(text, "fontSize", fontSize);
        SetProp(text, "alignment", EnumValue("UnityEngine.TextAnchor", "MiddleCenter", 4));
        SetProp(text, "color", MakeColor(0.95f, 0.95f, 0.95f, 1f));
        SetProp(text, "horizontalOverflow", EnumValue("UnityEngine.HorizontalWrapMode", "Wrap", 0));
        SetProp(text, "verticalOverflow", EnumValue("UnityEngine.VerticalWrapMode", "Truncate", 1));
        if (bold)
        {
            SetProp(text, "fontStyle", EnumValue("UnityEngine.FontStyle", "Bold", 1));
        }

        var font = ResolveFont();
        if (font != null)
        {
            SetProp(text, "font", font);
        }
    }

    private static object ResolveFont()
    {
        if (_cachedFont != null)
        {
            return _cachedFont;
        }

        try
        {
            var resources = FindType("UnityEngine.Resources");
            var fontType = FindType("UnityEngine.Font");
            if (resources != null && fontType != null)
            {
                var getBuiltin = resources.GetMethod(
                    "GetBuiltinResource",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(Type), typeof(string) },
                    null);
                if (getBuiltin != null)
                {
                    _cachedFont = getBuiltin.Invoke(null, new object[] { fontType, "Arial.ttf" });
                    if (_cachedFont != null)
                    {
                        return _cachedFont;
                    }
                }
            }

            // 从场景已有 Text 偷字体
            var objectType = FindType("UnityEngine.Object");
            var textType = FindType("UnityEngine.UI.Text");
            if (objectType != null && textType != null)
            {
                var find = objectType.GetMethod(
                    "FindObjectsOfType",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(Type) },
                    null);
                if (find != null)
                {
                    var arr = find.Invoke(null, new object[] { textType }) as Array;
                    if (arr != null && arr.Length > 0)
                    {
                        _cachedFont = GetProp(arr.GetValue(0), "font");
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return _cachedFont;
    }

    private static void SetColor(object graphic, float r, float g, float b, float a)
    {
        SetProp(graphic, "color", MakeColor(r, g, b, a));
    }

    private static object MakeColor(float r, float g, float b, float a)
    {
        var colorType = FindType("UnityEngine.Color");
        return Activator.CreateInstance(colorType, new object[] { r, g, b, a });
    }

    private static object AddComponent(object go, string typeName)
    {
        var t = FindType(typeName);
        if (t == null)
        {
            if (typeName.EndsWith("RectTransform", StringComparison.Ordinal))
            {
                return GetComp(go, typeName);
            }

            throw new InvalidOperationException("找不到类型 " + typeName);
        }

        var existing = GetComp(go, typeName);
        if (existing != null)
        {
            return existing;
        }

        // 禁止对已有 Transform 的物体再 Add RectTransform（Unity 会抛错）
        if (typeName.EndsWith("RectTransform", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "对象已有 Transform，无法 Add RectTransform；请用 CreateUiGameObject 创建");
        }

        var m = go.GetType().GetMethod("AddComponent", new[] { typeof(Type) });
        if (m == null)
        {
            throw new MissingMethodException(go.GetType().FullName, "AddComponent");
        }

        try
        {
            return m.Invoke(go, new object[] { t });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("AddComponent(" + typeName + ") 失败: " + RootMessage(ex), ex);
        }
    }

    private static object GetComp(object go, string typeName)
    {
        var t = FindType(typeName);
        if (t == null || go == null)
        {
            return null;
        }

        var m = go.GetType().GetMethod("GetComponent", new[] { typeof(Type) });
        return m.Invoke(go, new object[] { t });
    }

    private static object GetProp(object obj, string name)
    {
        if (obj == null)
        {
            return null;
        }

        var p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        return p != null ? p.GetValue(obj, null) : null;
    }

    private static void SetProp(object obj, string name, object value)
    {
        if (obj == null)
        {
            return;
        }

        var p = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (p != null && p.CanWrite)
        {
            p.SetValue(obj, value, null);
        }
    }

    private static object EnumValue(string enumTypeName, string name, int fallback)
    {
        var t = FindType(enumTypeName);
        if (t != null && t.IsEnum)
        {
            try
            {
                return Enum.Parse(t, name);
            }
            catch
            {
                return Enum.ToObject(t, fallback);
            }
        }

        return fallback;
    }

    private static object CallStatic(Type type, string name, Type[] argTypes, object[] args)
    {
        var m = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, argTypes, null);
        if (m == null)
        {
            throw new MissingMethodException(type.FullName, name);
        }

        return m.Invoke(null, args);
    }

    private static Type FindType(string fullOrSimple)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = null;
            try
            {
                t = asm.GetType(fullOrSimple, false, false);
            }
            catch
            {
                // ignore
            }

            if (t != null)
            {
                return t;
            }

            if (fullOrSimple.IndexOf('.') < 0)
            {
                try
                {
                    foreach (var candidate in asm.GetTypes())
                    {
                        if (candidate.Name == fullOrSimple)
                        {
                            return candidate;
                        }
                    }
                }
                catch
                {
                    // ReflectionTypeLoadException
                }
            }
        }

        return null;
    }

    private static void Tip(string msg)
    {
        try
        {
            var notify = GetManagerInstance("NotifyManager");
            if (notify == null)
            {
                return;
            }

            MethodInfo tip = null;
            foreach (var m in notify.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "Tip")
                {
                    continue;
                }

                var ps = m.GetParameters();
                if (ps.Length == 2 && ps[0].ParameterType == typeof(string))
                {
                    tip = m;
                    break;
                }
            }

            if (tip != null)
            {
                tip.Invoke(notify, new object[] { msg, false });
            }
        }
        catch
        {
            // ignore
        }
    }

    private static object GetManagerInstance(string managerName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type mgrType = null;
            try
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == managerName)
                    {
                        mgrType = t;
                        break;
                    }
                }
            }
            catch
            {
                continue;
            }

            if (mgrType == null)
            {
                continue;
            }

            foreach (var p in mgrType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (p.Name == "Instance" || p.Name == "instance")
                {
                    try
                    {
                        return p.GetValue(null, null);
                    }
                    catch
                    {
                        // continue
                    }
                }
            }
        }

        // Manager<T>.Instance
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type open = null;
            try
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == "Manager`1" && t.IsGenericTypeDefinition)
                    {
                        open = t;
                        break;
                    }
                }
            }
            catch
            {
                continue;
            }

            if (open == null)
            {
                continue;
            }

            Type arg = null;
            foreach (var asm2 in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm2.GetTypes())
                    {
                        if (t.Name == managerName)
                        {
                            arg = t;
                            break;
                        }
                    }
                }
                catch
                {
                    // ignore
                }

                if (arg != null)
                {
                    break;
                }
            }

            if (arg == null)
            {
                continue;
            }

            try
            {
                var closed = open.MakeGenericType(arg);
                var p = closed.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (p != null)
                {
                    return p.GetValue(null, null);
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static string RootMessage(Exception ex)
    {
        var cur = ex;
        while (cur.InnerException != null)
        {
            cur = cur.InnerException;
        }

        return cur.Message;
    }
}
