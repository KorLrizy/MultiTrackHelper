using ADOFAI;
using HarmonyLib;
using MultiTrackHelper.Features;
using MultiTrackHelper.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;

namespace MultiTrackHelper.Patches
{
    /// <summary>
    /// 把本模组标签页的文本接进游戏的文本系统。
    /// 游戏给枚举生成的键会带程序集限定名（含 Version/Culture/PublicKeyToken），
    /// 这里先剥掉限定名，再查我们自己的 Localizations.json。
    /// 我们的键都带 "mth." / "enum.MultiTrackHelper" 前缀，不会抢占别的 mod 的文本。
    /// </summary>
    [HarmonyPatch(typeof(RDString), "GetWithCheck")]
    internal static class LocalizedTextPatch
    {
        internal static void Postfix(ref string __result, ref string key, ref bool exists, ref Dictionary<string, object> parameters)
        {
            if (key == null)
                return;

            key = StripAssemblyQualifier(key);

            string value;
            if (Main.Localizations != null && Main.Localizations.Get(key, out value, parameters))
            {
                exists = true;
                __result = value;
            }
        }

        /// <summary>把带程序集限定名的枚举键还原成纯键名（与旧实现的切法完全一致）。</summary>
        private static string StripAssemblyQualifier(string key)
        {
            if (!key.Contains("enum.MultiTrackHelper") || !key.Contains("Version")
                || !key.Contains("Culture") || !key.Contains("PublicKeyToken"))
            {
                return key;
            }

            int firstComma = key.IndexOf(',');
            if (firstComma < 0)
                return key;

            return key.Substring(0, firstComma) + "." + key.Substring(key.LastIndexOf('.') + 1);
        }
    }

    /// <summary>让关卡文件里的 "MultiTrackHelper" 事件类型字符串能解析回 (LevelEventType)810。</summary>
    [HarmonyPatch]
    internal static class RDUtilsParseEnumPatch
    {
        internal static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(RDUtils), "ParseEnum", null, null)
                .MakeGenericMethod(new Type[] { typeof(LevelEventType) });
        }

        internal static bool Prefix(string str, ref LevelEventType defaultValue, ref LevelEventType __result)
        {
            if (str != Main.MultiTrackEventName)
                return true;
            __result = (LevelEventType)Main.MultiTrackEventType;
            return false;
        }
    }

    /// <summary>让编辑器把 810 当成"设置型"事件类型对待（设置型不进砖块事件列表）。</summary>
    [HarmonyPatch(typeof(EditorConstants), "IsSetting")]
    internal static class EditorConstantsIsSettingPatch
    {
        internal static void Postfix(LevelEventType type, ref bool __result)
        {
            if ((int)type == Main.MultiTrackEventType)
                __result = true;
        }
    }

    /// <summary>没有选中砖块时不要抛异常：直接给空列表。</summary>
    [HarmonyPatch(typeof(scnEditor), "GetSelectedFloorEvents")]
    internal static class scnEditorGetSelectedFloorEventsPatch
    {
        internal static bool Prefix(LevelEventType eventType, ref List<LevelEvent> __result)
        {
            scnEditor editor = scnEditor.instance;
            bool noFloorSelected = editor == null || editor.selectedFloors == null || editor.selectedFloors.Count == 0;
            if (noFloorSelected || (MultiTrackScope.IsOurType(eventType) && __result == null))
            {
                __result = new List<LevelEvent>();
                return false;
            }
            return true;
        }
    }

    /// <summary>标签页选中/取消时回调 CustomTab 的进入/离开钩子。</summary>
    [HarmonyPatch(typeof(InspectorTab), "SetSelected")]
    internal static class InspectorTabSetSelectedPatch
    {
        internal static void Postfix(InspectorTab __instance, bool selected)
        {
            if (__instance == null || Main.MultiTrack == null || !MultiTrackScope.IsOurType(__instance.levelEventType))
                return;

            if (selected)
                Main.MultiTrack.Focused();
            else
                Main.MultiTrack.Unfocused();
        }
    }

    /// <summary>
    /// 编辑器 Awake 时把 "MultiTrackHelper" 注入成一个原生关卡事件类型：
    /// 注册 LevelEventInfo / 图标 / 标题，之后游戏 InspectorPanel.Init 会依 GCS.settingsInfo
    /// 自动实例化面板与标签页。
    ///
    /// 时机必须在游戏自己的布局流程之前（Awake Prefix）：注册只用到 GCS 静态表，
    /// 而 InspectorPanel.Init 随后会遍历 GCS.settingsInfo 替我们把面板与标签页建出来。
    /// </summary>
    [HarmonyPatch(typeof(scnEditor), "Awake")]
    internal static class scnEditorAwakePatch
    {
        internal static void Prefix()
        {
            ResetRuntimeState();
            Inject();
        }

        /// <summary>关卡（重新）加载：清空插入功能的静态状态，避免上一关的区间/行数据泄漏。</summary>
        /// <remarks>
        /// 这里刻意不动 OnToggle 之外的任何跨关卡缓存：
        /// · PasteSync 的剪贴板元数据必须留着——复制后换关卡再粘贴是正常用法；
        /// · <see cref="InspectorPanelShowPanelPatch.saves"/> 也不清，靠 <see cref="Inject"/> 把它们指回新建的 LevelEventInfo。
        /// </remarks>
        internal static void ResetRuntimeState()
        {
            InsertBpmMultiplierFeature.ResetForLevel();
            Main.ResetInsertSpacers();
            // 上一轮编辑器里的输入框即将随场景销毁，先按引用摘掉插进去的委托
            TextEndEditPatch.ReleaseAll();
            Main.InsertUiInjected = false;
        }

        /// <summary>
        /// 注册事件类型。每次都重建而不是"已存在就跳过"：GCS 是静态的、跨场景存活，
        /// 而 PropertyInfo 里挂着本次进程新建的按钮委托与按当前语言求值的标签，复用旧对象会带着旧语言和旧委托。
        /// </summary>
        internal static void Inject()
        {
            if (Main.MultiTrack == null || GCS.levelEventsInfo == null || GCS.settingsInfo == null)
                return;

            EnsureIconTable();

            LevelEventInfo levelEventInfo = BuildLevelEventInfo();
            if (levelEventInfo == null)
                return;

            GCS.levelEventTypeString[Main.MultiTrack.EventType] = Main.MultiTrack.name;
            GCS.levelEventIcons[Main.MultiTrack.EventType] = Main.MultiTrack.icon;
            GCS.settingsInfo[Main.MultiTrack.name] = levelEventInfo;

            // 缓存的 LevelEvent 还挂着上一轮注入的 LevelEventInfo（里面的按钮委托与标签都是当时求值的）。
            // info 是普通公开字段，改指向是安全的：事件数据本身不动，只是换成新一份属性定义。
            InspectorPanelShowPanelPatch.RebindCachedEvents(levelEventInfo);
        }

        /// <summary>图标表正常情况下由游戏自己填好；万一为空就按资源名补一遍（游戏原本的做法）。</summary>
        private static void EnsureIconTable()
        {
            if (GCS.levelEventIcons != null)
                return;

            GCS.levelEventIcons = new Dictionary<LevelEventType, Sprite>();
            foreach (object value in Enum.GetValues(typeof(LevelEventType)))
            {
                Sprite sprite = Resources.Load<Sprite>("LevelEditor/LevelEvents/" + value);
                if (sprite != null)
                    GCS.levelEventIcons[(LevelEventType)value] = sprite;
            }
        }

        private static LevelEventInfo BuildLevelEventInfo()
        {
            var levelEventInfo = new LevelEventInfo
            {
                categories = new List<LevelEventCategory>(),
                executionTime = LevelEventExecutionTime.Special,
                name = Main.MultiTrack.name,
                propertiesInfo = new Dictionary<string, ADOFAI.PropertyInfo>(),
                type = Main.MultiTrack.EventType
            };

            if (Main.MultiTrack.properties == null)
                return levelEventInfo;

            foreach (Dictionary<string, object> dictionary in Main.MultiTrack.properties)
            {
                if (dictionary == null)
                    continue;

                var propertyInfo = new ADOFAI.PropertyInfo(dictionary, levelEventInfo);
                object rawType;
                if (dictionary.TryGetValue("type", out rawType) && rawType as string == "Export")
                {
                    object rawDefault;
                    if (dictionary.TryGetValue("default", out rawDefault) && rawDefault is UnityAction)
                        propertyInfo.value_default = rawDefault;
                }

                // 逐行的字面标签（倍频器行号等）无法用固定本地化键表达，构建时按当前语言求值
                Func<string> customLabel;
                if (PrefabProperties.CustomLabels.TryGetValue(propertyInfo.name, out customLabel) && customLabel != null)
                    propertyInfo.customLabel = customLabel();

                propertyInfo.order = 0;
                levelEventInfo.propertiesInfo[propertyInfo.name] = propertyInfo;
            }
            return levelEventInfo;
        }

        /// <summary>
        /// 兜底：正常由游戏 InspectorPanel.Init 依 GCS.settingsInfo 建出面板/标签页。
        ///
        /// 这里的重点是“标签页和面板各自独立检查、各自补齐”：旧实现只要找到标签页就整体 return，
        /// 标签页在、面板却没建出来时（注册晚于 InspectorPanel.Init，或面板被销毁）就永远不会补。
        /// 幂等：两者都在时只做认领，不重复实例化；只有真的缺一个才建那一个。
        /// 注册丢失（GCS 里没有我们的 LevelEventInfo）时先重试一次 Inject 再取。
        /// </summary>
        internal static void EnsureUiExists()
        {
            InspectorPanel settingsPanel = scnEditor.instance != null ? scnEditor.instance.settingsPanel : null;
            if (settingsPanel == null || Main.MultiTrack == null)
                return;

            LevelEventInfo levelEventInfo = ResolveLevelEventInfo();
            if (levelEventInfo == null)
            {
                // 注册表被重建/清掉，或者注入本来就晚了一步：补注册后重试一次
                Inject();
                levelEventInfo = ResolveLevelEventInfo();
                if (levelEventInfo == null)
                {
                    Main.Logger?.Log("EnsureUiExists: GCS 里没有本模组的事件注册，跳过 UI 兜底");
                    return;
                }
            }

            InspectorTab tab = FindOurTab(settingsPanel);
            PropertiesPanel panel = FindOurPanel(settingsPanel);
            if (tab != null && panel != null)
            {
                Main.InsertUiInjected = true;
                return;
            }

            if (panel == null)
            {
                panel = CreatePanel(settingsPanel, levelEventInfo);
                if (panel == null)
                    return;
                Main.Logger?.Log("EnsureUiExists: 面板缺失，已按原生 prefab 补建并登记进 panelsList");
            }

            if (tab == null)
            {
                tab = CreateTab(settingsPanel);
                if (tab == null)
                    return;
                Main.Logger?.Log("EnsureUiExists: 标签页缺失，已按原生 prefab 补建");
            }

            Main.InsertUiInjected = true;
        }

        private static LevelEventInfo ResolveLevelEventInfo()
        {
            LevelEventInfo levelEventInfo;
            if (GCS.settingsInfo == null
                || !GCS.settingsInfo.TryGetValue(Main.MultiTrack.name, out levelEventInfo))
            {
                return null;
            }
            return levelEventInfo;
        }

        /// <summary>补建面板：顺序与 InspectorPanel.Init 一致（先挂到 panels 下→登记→最后 Init）。</summary>
        private static PropertiesPanel CreatePanel(InspectorPanel settingsPanel, LevelEventInfo levelEventInfo)
        {
            GameObject panelObject = UnityEngine.Object.Instantiate(RDConstants.data.prefab_propertiesPanel);
            // 必须挂到面板容器下（与 InspectorPanel.Init 一致），否则根对象会被拉伸成整屏
            panelObject.transform.SetParent(settingsPanel.panels, false);
            panelObject.name = Main.MultiTrack.name;
            PropertiesPanel panel = panelObject.GetComponent<PropertiesPanel>();
            if (panel == null)
            {
                UnityEngine.Object.Destroy(panelObject);
                return null;
            }

            panel.levelEventType = Main.MultiTrack.EventType;
            panel.gameObject.SetActive(false);

            // 完整初始化后才登记，失败时不能把半建成面板留给下一轮的存在性检查。
            try
            {
                panel.Init(settingsPanel, levelEventInfo);
                if (settingsPanel.panelsList == null)
                    settingsPanel.panelsList = new List<PropertiesPanel>();
                if (!settingsPanel.panelsList.Contains(panel))
                    settingsPanel.panelsList.Add(panel);
                return panel;
            }
            catch (Exception error)
            {
                Main.Logger?.Log("EnsureUiExists: 面板初始化失败，已回收未完成实例：" + error);
                panelObject.SetActive(false);
                panelObject.transform.SetParent(null, false);
                UnityEngine.Object.Destroy(panelObject);
                return null;
            }
        }

        /// <summary>
        /// 补建标签页。顺序与原生一致：先挂到 tabs 下（worldPositionStays: false），再 Init/取消选中，
        /// 最后按“原生布局序号”排 Y——原生是 -68 * 序号（序号 = 该键在 GCS.settingsInfo 里的次序，
        /// 跳过不生成标签页的 EventSettings），旧实现用的 8 - 68 * 子对象数是另一个坐标系，会错位。
        /// </summary>
        private static InspectorTab CreateTab(InspectorPanel settingsPanel)
        {
            GameObject tabObject = UnityEngine.Object.Instantiate(RDConstants.data.prefab_tab);
            tabObject.transform.SetParent(settingsPanel.tabs, false);
            InspectorTab tab = tabObject.GetComponent<InspectorTab>();
            if (tab == null)
            {
                UnityEngine.Object.Destroy(tabObject);
                return null;
            }

            tab.Init(Main.MultiTrack.EventType, settingsPanel);
            tab.SetSelected(false);
            tab.GetComponent<RectTransform>().AnchorPosY(-InspectorPanel.tabHeight * NativeTabIndex());
            return tab;
        }

        /// <summary>本模组在原生标签页布局里的序号（与 InspectorPanel.Init 的 num 同口径）。</summary>
        private static int NativeTabIndex()
        {
            if (GCS.settingsInfo == null)
                return 0;

            string skipped = LevelEventType.EventSettings.ToString();
            int index = 0;
            foreach (string key in GCS.settingsInfo.Keys)
            {
                if (key == skipped)
                    continue;
                if (key == Main.MultiTrack.name)
                    return index;
                index++;
            }
            return index;
        }

        private static PropertiesPanel FindOurPanel(InspectorPanel settingsPanel)
        {
            List<PropertiesPanel> panels = settingsPanel.panelsList;
            if (panels != null)
            {
                for (int i = 0; i < panels.Count; i++)
                {
                    PropertiesPanel candidate = panels[i];
                    if (candidate != null && MultiTrackScope.IsOurType(candidate.levelEventType))
                        return candidate;
                }
            }

            // panelsList 还没建出来（游戏 Init 没跑完）时按对象名在 panels 容器里找一遍
            if (settingsPanel.panels != null)
            {
                for (int i = 0; i < settingsPanel.panels.childCount; i++)
                {
                    PropertiesPanel candidate = settingsPanel.panels.GetChild(i).GetComponent<PropertiesPanel>();
                    if (candidate == null || !MultiTrackScope.IsOurType(candidate.levelEventType))
                        continue;

                    // 容器里有、panelsList 里没有：说明它是上一轮漏登记的，顺手补登
                    if (panels != null && !panels.Contains(candidate))
                        panels.Add(candidate);
                    return candidate;
                }
            }
            return null;
        }

        private static InspectorTab FindOurTab(InspectorPanel panel)
        {
            for (int i = 0; i < panel.tabs.childCount; i++)
            {
                InspectorTab tab = panel.tabs.GetChild(i).GetComponent<InspectorTab>();
                if (tab != null && MultiTrackScope.IsOurType(tab.levelEventType))
                    return tab;
            }
            return null;
        }

        /// <summary>
        /// 关闭模组时移除注入到 GCS 的静态数据。这些字典跨场景重载仍然存在，
        /// 不清掉的话即使编辑器重启，我们的"关卡事件类型/标题/图标"仍然残留。
        /// </summary>
        internal static void RemoveInjection()
        {
            if (Main.MultiTrack == null)
                return;

            if (GCS.settingsInfo != null)
                GCS.settingsInfo.Remove(Main.MultiTrack.name);
            if (GCS.levelEventTypeString != null)
                GCS.levelEventTypeString.Remove(Main.MultiTrack.EventType);
            if (GCS.levelEventIcons != null)
                GCS.levelEventIcons.Remove(Main.MultiTrack.EventType);
        }
    }

    /// <summary>
    /// 兜底：正常由游戏 InspectorPanel.Init 建出面板/标签页。
    /// Start 时只在“还没认领”的情况下跑一次 EnsureUiExists，由它分别检查标签页与面板、只补缺的那一个。
    /// </summary>
    [HarmonyPatch(typeof(scnEditor), "Start")]
    internal static class scnEditorStartPatch
    {
        internal static void Postfix()
        {
            if (!Main.InsertUiInjected)
                scnEditorAwakePatch.EnsureUiExists();
        }
    }

    /// <summary>
    /// 面板切换到 810 时接管 InspectorPanel.ShowPanel，维护本模组自己的 LevelEvent 数据。
    ///
    /// 之所以整套接管而不是只补一点：游戏的 ShowPanel 是按 levelEventType 找 PropertiesPanel、
    /// 再从关卡里取事件，而我们的"事件"不在关卡里，必须换成缓存的那一份。
    /// </summary>
    [HarmonyPatch(typeof(InspectorPanel), "ShowPanel")]
    internal static class InspectorPanelShowPanelPatch
    {
        /// <summary>
        /// 按事件类型缓存的本模组面板事件（纯内存：不进存档、不写磁盘，编辑器重载后重建，
        /// 关闭模组时清空）。刷新的是同一份实例，所以用户切换面板再回来值还在。
        /// </summary>
        internal static readonly Dictionary<LevelEventType, LevelEvent> saves = new Dictionary<LevelEventType, LevelEvent>();

        private static readonly FieldInfo showingPanelField = AccessTools.Field(typeof(InspectorPanel), "showingPanel");

        internal static bool Prefix(InspectorPanel __instance, LevelEventType eventType, int eventIndex = 0)
        {
            if (!MultiTrackScope.IsOurType(eventType))
                return true;

            scnEditor editor = scnEditor.instance;
            if (editor == null || __instance == null)
                return false;

            bool statePushed = false;
            try
            {
                SetShowingPanel(__instance, true);
                // 切模组参数页不改关卡，因此不再裸调 editor.SaveState：
                // PACL 接管撤销栈时会压一个永不收尾的撤销点（撤到它报错并卡死整条栈），
                // 原版下也会凭空多出一个空撤销点。redo 也一律不动。
                // 只保留 changingState 计数：面板搭建期间的写入不算关卡改动，也不去清 redo。
                editor.changingState++;
                statePushed = true;

                ShowOurPanel(__instance, eventType);
            }
            finally
            {
                if (statePushed)
                    editor.changingState--;
                SetShowingPanel(__instance, false);
            }

            return false;
        }

        internal static void Postfix(LevelEventType eventType, int eventIndex = 0)
        {
            if (!MultiTrackScope.IsOurType(eventType) || Main.MultiTrack == null)
                return;

            Main.activeChilden();
            if (Main.IsInsertFeatureActive() && InsertBpmMultiplierFeature.CaptureEnabled(Main.MultiTrackLevelEvent))
                InsertBpmMultiplierFeature.SyncFromSelection(Main.MultiTrackLevelEvent);
        }

        private static void ShowOurPanel(InspectorPanel panel, LevelEventType eventType)
        {
            PropertiesPanel propertiesPanel = ActivateOnlyOurPanel(panel, eventType);

            if (Main.Localizations != null)
                panel.title.text = Main.Localizations.GetValue("mth.MultiTrackHelper");

            LevelEvent levelEvent = ResolveLevelEvent(eventType);
            if (levelEvent == null || propertiesPanel == null)
            {
                // 取不到事件/面板时不留半截状态：模组缓存的当前事件与面板选中的事件一起清掉，
                // 否则之后的 activeChilden / OnValueChanged 会继续读写上一轮那个事件
                Main.MultiTrackLevelEvent = null;
                panel.selectedEvent = null;
                panel.selectedEventType = eventType;
                Main.Logger?.Log(propertiesPanel == null
                    ? "ShowPanel: 本模组的面板实例不存在（EnsureUiExists 未补建成功），面板保持空白"
                    : "ShowPanel: 取不到本模组的面板事件（LevelEventInfo 缺失或事件构造失败），面板保持空白");
                return;
            }

            Main.MultiTrackLevelEvent = levelEvent;
            panel.selectedEvent = levelEvent;
            panel.selectedEventType = levelEvent.eventType;

            // 缓存的面板可能留着"集中式 + 轴心偏移 + 同步移动轨道"的旧组合，渲染前先清掉再显示置灰结果
            MultiTrackFeature.ApplyGenerationModeConstraints(levelEvent);
            propertiesPanel.SetProperties(levelEvent, true);
            levelEvent.UpdatePanel();
            SelectTabs(panel, eventType);
        }

        /// <summary>只让本模组的面板可见，其余面板全部隐藏。</summary>
        private static PropertiesPanel ActivateOnlyOurPanel(InspectorPanel panel, LevelEventType eventType)
        {
            PropertiesPanel ours = null;
            List<PropertiesPanel> panels = panel.panelsList;
            if (panels == null)
                return null;

            for (int i = 0; i < panels.Count; i++)
            {
                PropertiesPanel candidate = panels[i];
                if (candidate == null)
                    continue;
                bool isOurs = candidate.levelEventType == eventType;
                if (isOurs)
                    ours = candidate;
                candidate.gameObject.SetActive(isOurs);
            }
            return ours;
        }

        /// <summary>
        /// 取出面板数据：saveSetting 打开时复用上一次的事件实例（内存缓存，不落盘），否则每次新建。
        /// </summary>
        private static LevelEvent ResolveLevelEvent(LevelEventType eventType)
        {
            if (Main.MultiTrack == null)
                return null;

            LevelEvent levelEvent;
            if (!Main.MultiTrack.saveSetting || !saves.TryGetValue(eventType, out levelEvent) || levelEvent == null)
            {
                LevelEventInfo levelEventInfo;
                if (GCS.settingsInfo == null
                    || !GCS.settingsInfo.TryGetValue(Main.MultiTrack.name, out levelEventInfo)
                    || levelEventInfo == null)
                {
                    return null;
                }
                levelEvent = new LevelEvent(0, eventType, levelEventInfo);
            }

            if (Main.MultiTrack.saveSetting)
                saves[eventType] = levelEvent;
            return levelEvent;
        }

        /// <summary>
        /// 注入时把缓存事件指回新建的 LevelEventInfo（它们此前挂着上一轮注入的那份）。
        /// LevelEvent.info 是普通公开字段，换引用不会改动任何面板数据；不做这一步的话，
        /// 编辑器重载后缓存的面板会用旧 info 渲染（旧按钮委托、旧语言的标签）。
        /// </summary>
        internal static void RebindCachedEvents(LevelEventInfo levelEventInfo)
        {
            if (levelEventInfo == null || saves.Count == 0)
                return;

            foreach (KeyValuePair<LevelEventType, LevelEvent> pair in saves)
            {
                LevelEvent cached = pair.Value;
                if (cached != null)
                    cached.info = levelEventInfo;
            }
        }

        private static void SelectTabs(InspectorPanel panel, LevelEventType eventType)
        {
            IEnumerator enumerator = panel.tabs.GetEnumerator();
            while (enumerator.MoveNext())
            {
                RectTransform rect = (RectTransform)enumerator.Current;
                if (rect == null)
                    continue;
                InspectorTab tab = rect.gameObject.GetComponent<InspectorTab>();
                if (tab != null)
                    tab.SetSelected(eventType == tab.levelEventType);
            }
        }

        private static void SetShowingPanel(InspectorPanel panel, bool value)
        {
            if (showingPanelField != null)
                showingPanelField.SetValue(panel, value);
        }
    }
}
