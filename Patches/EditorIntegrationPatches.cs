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
        /// 兜底：正常由游戏 InspectorPanel.Init 依 GCS.settingsInfo 建出面板/标签页；
        /// 若因注册时序没建出来，手动补一份。正常情况下这里只做一次"已存在"检查就返回。
        /// </summary>
        internal static void EnsureUiExists()
        {
            InspectorPanel settingsPanel = scnEditor.instance != null ? scnEditor.instance.settingsPanel : null;
            if (settingsPanel == null || Main.MultiTrack == null || GCS.settingsInfo == null)
                return;

            if (FindOurTab(settingsPanel) != null)
            {
                Main.InsertUiInjected = true;
                return;
            }

            LevelEventInfo levelEventInfo;
            if (!GCS.settingsInfo.TryGetValue(Main.MultiTrack.name, out levelEventInfo) || levelEventInfo == null)
                return;

            GameObject panelObject = UnityEngine.Object.Instantiate(RDConstants.data.prefab_propertiesPanel);
            // 必须挂到面板容器下（与 InspectorPanel.Init 一致），否则根对象会被拉伸成整屏
            panelObject.transform.SetParent(settingsPanel.panels, false);
            panelObject.name = Main.MultiTrack.name;
            PropertiesPanel panel = panelObject.GetComponent<PropertiesPanel>();
            panel.levelEventType = Main.MultiTrack.EventType;
            panel.gameObject.SetActive(false);

            GameObject tabObject = UnityEngine.Object.Instantiate(RDConstants.data.prefab_tab);
            InspectorTab tab = tabObject.GetComponent<InspectorTab>();
            panel.Init(settingsPanel, levelEventInfo);
            tab.Init(Main.MultiTrack.EventType, settingsPanel);
            tab.SetSelected(false);
            tab.GetComponent<RectTransform>().AnchorPosY(8f - 68f * settingsPanel.tabs.childCount);
            tabObject.transform.SetParent(settingsPanel.tabs, false);

            Main.InsertUiInjected = true;
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
    /// 兜底：正常由游戏 InspectorPanel.Init 建出面板/标签页；
    /// Start 时发现没有我们类型就手动补一份。
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
                // PACL 接管撤销栈时，裸调旧 SaveState 会压一个永不收尾的状态（没有 SaveStateScope.Dispose
                // 替它填数组），撤到它就抛 NRE、出不了栈，整条撤销栈从此卡死。切面板不改关卡，PACL 下干脆不压。
                if (!PaclUndoBridge.IsActive())
                    editor.SaveState(true, false);
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
                return;

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

        /// <summary>取出面板数据：saveSetting 打开时复用上一次的事件实例，否则每次新建。</summary>
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
