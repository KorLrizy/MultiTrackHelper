using ADOFAI;
using MultiTrackHelper.Utils;
using ADOFAI.LevelEditor.Controls;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
// 本文件里裸写 Property 指游戏的 ADOFAI.Property；面板模型的 Property 用别名，避免二义
using PropertySpec = MultiTrackHelper.PropertyCollection.Property;

namespace MultiTrackHelper.Patches
{
    /// <summary>
    /// 判断某个事件类型 / 控件的 PropertyInfo 是不是本模组注入的那一个（810）。
    /// 面板补丁一律先过这道门：只改自己面板里的控件，不碰游戏原生面板和其它 mod 的控件。
    /// </summary>
    internal static class MultiTrackScope
    {
        internal static bool IsOurType(LevelEventType type)
        {
            return (int)type == Main.MultiTrackEventType;
        }

        internal static bool IsOurs(ADOFAI.PropertyInfo info)
        {
            return info != null && info.levelEventInfo != null && IsOurType(info.levelEventInfo.type);
        }
    }

    /// <summary>
    /// Export 型控件在游戏里本意是"打开导出窗口"的按钮；本模组把它当通用按钮用。
    ///
    /// 渲染完全交给原生 PropertiesPanel.RenderControl（v3.3.1 起原生就有 case PropertyType.Export），
    /// 这里只在原生建好控件之后、且属于本模组事件类型时，把按钮的 onClick 换成模组定义的回调、
    /// 并按属性键改按钮文字。其它 mod 与游戏自己的 Export 控件保持原生行为。
    ///
    /// 关于"启用/停用"按钮的撤销作用域：原生那份监听用的是 new SaveStateScope（PACL2 下会落到
    /// 旧的 SaveState 兜底）。本模组所有 Export 控件的 canBeDisabled 都是 false，
    /// 而 PropertiesPanel.SetupCheckmark 只有在 canBeDisabled 或事件 isFake 时才让该按钮可见
    /// （flag2 = canBeDisabled || isFake，本模组的事件由 ShowPanel 直接 new 出来，isFake 恒为 false），
    /// 所以那段监听在本模组的面板上永远不会被点击；真正改关卡的入口（生成轨道/尺寸变化/粘贴缩放）
    /// 仍走 Utils/UndoScope.Begin，撤销修复不受影响。万一以后有人把某个按钮改成可停用，这里会告警。
    /// </summary>
    [HarmonyPatch(typeof(PropertiesPanel), "RenderControl")]
    internal static class ExportButtonPatch
    {
        internal static void Postfix(PropertiesPanel __instance, string propertyKey, ADOFAI.PropertyInfo propertyInfo)
        {
            if (!MultiTrackScope.IsOurs(propertyInfo) || propertyInfo.type != PropertyType.Export)
                return;

            if (propertyInfo.canBeDisabled)
                Main.Logger?.Log("警告: Export 控件 " + propertyInfo.name + " 被设为可停用，启用按钮会走原生 SaveStateScope，PACL2 下需要复查");

            UnityAction action = propertyInfo.value_default as UnityAction;
            if (action == null)
                return;

            Property property;
            if (__instance == null || !__instance.properties.TryGetValue(propertyKey, out property) || property == null)
                return;
            PropertyControl_Export export = property.control as PropertyControl_Export;
            if (export == null)
                return;

            // 原生 PropertyControl_Export.Awake 绑的是 ShowExport，这里整体换成我们的回调
            Button button = export.exportButton;
            if (button != null)
            {
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(action);
                button.name = propertyInfo.name;
            }
            ApplyButtonText(export, propertyInfo);
        }

        private static void ApplyButtonText(PropertyControl_Export export, ADOFAI.PropertyInfo info)
        {
            TMP_Text text = export.buttonText;
            if (text == null)
                return;

            if (info.customLocalizationKey == null)
            {
                string key = "editor." + info.levelEventInfo.name + "." + info.name;
                text.text = RDString.GetWithCheck(key, out bool exists, null);
                if (!exists)
                    text.text = RDString.GetWithCheck("editor." + info.name, out exists, null);
            }
            else
            {
                text.text = info.customLocalizationKey.Length == 0 ? "" : RDString.Get(info.customLocalizationKey, null);
            }
        }
    }

    /// <summary>本模组的 Export 控件不要标签文本：按钮自己带文字，标签位留空。</summary>
    [HarmonyPatch(typeof(ADOFAI.Property), "info", MethodType.Setter)]
    internal static class ExportLabelPatch
    {
        internal static void Postfix(ADOFAI.Property __instance)
        {
            if (__instance == null || __instance.info == null || __instance.label == null)
                return;
            if (__instance.info.type != PropertyType.Export || !MultiTrackScope.IsOurs(__instance.info))
                return;
            __instance.label.text = "";
        }
    }

    /// <summary>
    /// 防止按 Enter 时触发本模组按钮的 OnSubmit 冒泡（会把焦点丢进生成流程）。
    ///
    /// 判定靠控件所属的 ADOFAI.Property 及其 info：只有 info 属于本模组事件类型、且确实是 Export 控件
    /// （PropertyControl_Export 里的按钮）才拦。不再按按钮物体名 Contains("createButton") 做全局匹配——
    /// 那样会连带拦掉别的 mod、甚至游戏自己同名的按钮。
    /// </summary>
    [HarmonyPatch(typeof(UnityEngine.UI.Button), "OnSubmit")]
    internal static class ButtonSubmitGuardPatch
    {
        internal static bool Prefix(UnityEngine.UI.Button __instance)
        {
            if (__instance == null)
                return true;

            ADOFAI.Property property = __instance.GetComponentInParent<ADOFAI.Property>();
            if (property == null || property.info == null || !MultiTrackScope.IsOurs(property.info))
                return true;

            // 本模组面板里只有 Export 型控件带按钮（生成/尺寸变化/倍频/粘贴…）；其余原生按钮不拦
            return property.info.type != PropertyType.Export;
        }
    }

    /// <summary>
    /// Tile 控件支持隐藏 ThisTile / Start / End 三个快捷按钮：
    /// 隐藏哪些由控件的 hideButtons 位掩码决定（这是本模组给 PropertyInfo 加的自定义键，游戏本身不读）。
    /// </summary>
    [HarmonyPatch]
    internal static class TileHideButtonsPatch
    {
        internal static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(PropertyControl_Tile), "Setup");
        }

        internal static void Postfix(PropertyControl_Tile __instance)
        {
            if (__instance == null || !MultiTrackScope.IsOurs(__instance.propertyInfo))
                return;

            object raw;
            if (__instance.propertyInfo.dict == null
                || !__instance.propertyInfo.dict.TryGetValue("hideButtons", out raw)
                || !(raw is System.Collections.IEnumerable list))
            {
                return;
            }

            int mask = 0;
            foreach (object value in list)
            {
                if (value is int)
                    mask |= (int)value;
            }

            if ((mask & PropertySpec.THIS_TILE) != 0 && __instance.buttonThisTile != null)
                __instance.buttonThisTile.gameObject.SetActive(false);
            if ((mask & PropertySpec.START) != 0 && __instance.buttonFirstTile != null)
                __instance.buttonFirstTile.gameObject.SetActive(false);
            if ((mask & PropertySpec.END) != 0 && __instance.buttonLastTile != null)
                __instance.buttonLastTile.gameObject.SetActive(false);
        }
    }

    /// <summary>
    /// 帮助按钮与"启用/停用"按钮重叠时的布局修正。
    /// 只对本模组面板生效：原生 UpdateEnabledButton 已经按 helpExists 处理过，
    /// 这里只补偿本面板控件间距偏窄的情况，不改其它面板。
    /// </summary>
    [HarmonyPatch(typeof(PropertiesPanel), "UpdateEnabledButton")]
    internal static class EnabledButtonLayoutPatch
    {
        internal static void Postfix(ADOFAI.Property property, bool disabled)
        {
            if (property == null || property.info == null || !MultiTrackScope.IsOurs(property.info))
                return;
            if (!disabled && property.helpButton != null && property.helpButton.gameObject.activeInHierarchy)
                property.enabledButton.GetComponent<RectTransform>().offsetMax = new Vector2(-30, 0);
        }
    }

    /// <summary>
    /// 开关/颜色/下拉/向量/浮点对这些控件的值变化回调：
    /// 进入控件方法前记下旧值与当时的事件对象，方法返回后比对并回调 Main.MultiTrack.OnValueChanged；
    /// 回调返回 false 就把值写回旧值并刷新面板。
    ///
    /// 只挂本面板真的用到的控件（Bool / Color / Toggle / Vector2 / FloatPair）：
    /// 没有控件的类型不挂，免得白白接管全局同名方法。
    ///
    /// 只有"确实读到了旧值"（TryGet 成功）才参与；且返回时若选中的事件已经换成另一个对象，
    /// 整段跳过——否则会把上一个事件的旧值写进新事件。
    /// </summary>
    [HarmonyPatch]
    internal static class ValueChangePatch
    {
        internal struct Snapshot
        {
            internal bool captured;
            internal LevelEvent eventRef;
            internal object value;
        }

        internal static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PropertyControl_Bool), "SetValue");
            yield return AccessTools.Method(typeof(PropertyControl_Color), "OnEndEdit") ?? AccessTools.Method(typeof(PropertyControl_Color), "OnChange");
            yield return AccessTools.Method(typeof(PropertyControl_Toggle), "SelectVar");
            yield return AccessTools.Method(typeof(PropertyControl_Vector2), "SetVectorVals");
            yield return AccessTools.Method(typeof(PropertyControl_FloatPair), "Save");
        }

        internal static void Prefix(object __instance, PropertiesPanel ___propertiesPanel, ADOFAI.PropertyInfo ___propertyInfo, ref Snapshot __state)
        {
            __state = default(Snapshot);
            if (!ShouldTrack(__instance, ___propertiesPanel, ___propertyInfo))
                return;

            LevelEvent selectedEvent = ___propertiesPanel.inspectorPanel.selectedEvent;
            if (!selectedEvent.TryGet<object>(___propertyInfo.name, out __state.value))
                return;

            __state.eventRef = selectedEvent;
            __state.captured = true;
        }

        internal static void Postfix(PropertiesPanel ___propertiesPanel, ADOFAI.PropertyInfo ___propertyInfo, Snapshot __state)
        {
            if (!__state.captured || ___propertyInfo == null || ___propertiesPanel == null)
                return;

            InspectorPanel inspector = ___propertiesPanel.inspectorPanel;
            if (inspector == null)
                return;

            LevelEvent selectedEvent = inspector.selectedEvent;
            if (selectedEvent == null || !ReferenceEquals(selectedEvent, __state.eventRef))
                return;

            object newValue;
            if (!selectedEvent.TryGet<object>(___propertyInfo.name, out newValue))
                return;

            if (!Main.MultiTrack.OnValueChanged(selectedEvent, ___propertyInfo.name, __state.value, newValue))
            {
                selectedEvent[___propertyInfo.name] = __state.value;
                selectedEvent.UpdatePanel();
            }
        }

        /// <summary>设置面板里的"文本切换"控件（settingText）改的是显示模式，不是事件数据，跳过。</summary>
        private static bool ShouldTrack(object control, PropertiesPanel panel, ADOFAI.PropertyInfo info)
        {
            if (Main.MultiTrack == null || info == null || !MultiTrackScope.IsOurs(info))
                return false;
            if (panel == null || panel.inspectorPanel == null || panel.inspectorPanel.selectedEvent == null)
                return false;
            PropertyControl_Toggle toggle = control as PropertyControl_Toggle;
            return toggle == null || !toggle.settingText;
        }
    }

    /// <summary>
    /// 文本输入控件的值变化回调。游戏的 onEndEdit 是在 Setup 里用匿名委托注册的，
    /// 外面拿不到旧值，所以往 UnityEvent 的内部调用列表首尾各插一个自己构造的 invokable：
    /// 首部记旧值+事件引用，尾部比对并回调。
    ///
    /// 记账只记"自己插入的那两个 invokable 对象引用"：
    /// · 重新 Setup 时先看它们是否还在列表里——还在就跳过，不在就重新插入，避免回调丢失；
    /// · 需要替换时只移除自己的那两个，绝不 RemoveAllListeners（那会连游戏的写入逻辑一起删掉）；
    /// · 编辑器重载/关闭模组时按引用把自己的插桩摘干净，并清掉对面板/事件的引用。
    ///
    /// 委托执行时面板、inspector、模组实例都可能已经销毁（UnityEvent 的委托活得比场景久），
    /// 所以每次执行都重新判活。
    /// </summary>
    [HarmonyPatch]
    internal static class TextEndEditPatch
    {
        private sealed class Instrumentation
        {
            internal TMP_InputField field;
            internal PropertiesPanel panel;
            internal ADOFAI.PropertyInfo info;
            internal object headCall;
            internal object tailCall;

            // 一次编辑期间的临时状态
            internal bool captured;
            internal LevelEvent eventRef;
            internal object oldValue;

            internal bool IsAlive()
            {
                return field != null && panel != null && panel.inspectorPanel != null && Main.MultiTrack != null;
            }
        }

        private static readonly FieldInfo callsField = AccessTools.Field(typeof(UnityEventBase), "m_Calls");
        private static readonly FieldInfo runtimeCallsField = callsField != null
            ? AccessTools.Field(callsField.FieldType, "m_RuntimeCalls") : null;
        private static readonly FieldInfo needsUpdateField = callsField != null
            ? AccessTools.Field(callsField.FieldType, "m_NeedsUpdate") : null;
        private static readonly MethodInfo getDelegateMethod = AccessTools.Method(
            typeof(UnityEvent<string>), "GetDelegate", new[] { typeof(UnityAction<string>) });

        private static readonly List<Instrumentation> records = new List<Instrumentation>();

        private static bool reflectionBroken;

        internal static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PropertyControl_Text), "Setup");
        }

        internal static void Postfix(object __instance, PropertiesPanel ___propertiesPanel, ADOFAI.PropertyInfo ___propertyInfo)
        {
            if (!MultiTrackScope.IsOurs(___propertyInfo) || ___propertiesPanel == null)
                return;
            if (___propertiesPanel.inspectorPanel == null)
                return;

            TMP_InputField inputField = ResolveInputField(__instance);
            if (inputField == null || !EnsureReflectionHandles())
                return;

            object calls = callsField.GetValue(inputField.onEndEdit);
            if (calls == null)
                return;
            var runtime = runtimeCallsField.GetValue(calls) as System.Collections.IList;
            if (runtime == null)
                return;

            Instrumentation record = FindRecord(inputField);
            if (record != null && record.info == ___propertyInfo
                && ContainsCall(runtime, record.headCall) && ContainsCall(runtime, record.tailCall))
            {
                // 已经插过桩且仍在列表里，不重复插
                return;
            }

            if (record != null)
            {
                // 旧的插桩被面板重建清掉了，或属性换了：先摘掉自己的，再按新参数插回
                RemoveCall(runtime, record.headCall);
                RemoveCall(runtime, record.tailCall);
                records.Remove(record);
                // UnityEvent 会把调用列表缓存成 m_ExecutingCalls，改完必须标记重建
                needsUpdateField.SetValue(calls, true);
            }

            record = new Instrumentation
            {
                field = inputField,
                panel = ___propertiesPanel,
                info = ___propertyInfo
            };

            object head = getDelegateMethod.Invoke(inputField.onEndEdit, new object[] { BuildHead(record) });
            object tail = getDelegateMethod.Invoke(inputField.onEndEdit, new object[] { BuildTail(record) });
            if (head == null || tail == null)
                return;

            record.headCall = head;
            record.tailCall = tail;

            // m_RuntimeCalls 是 List<BaseInvokableCall>（internal 类型），走非泛型 IList
            runtime.Insert(0, head);
            runtime.Add(tail);
            needsUpdateField.SetValue(calls, true);
            records.Add(record);
        }

        /// <summary>摘掉所有自己插入的插桩，并清空对面板/事件的引用。编辑器重载与关闭模组时调用。</summary>
        internal static void ReleaseAll()
        {
            for (int i = 0; i < records.Count; i++)
            {
                Instrumentation record = records[i];
                if (record == null)
                    continue;
                if (record.field != null && record.headCall != null)
                {
                    var runtime = GetRuntime(record.field);
                    if (runtime != null)
                    {
                        RemoveCall(runtime, record.headCall);
                        RemoveCall(runtime, record.tailCall);
                        MarkDirty(record.field);
                    }
                }
                record.headCall = null;
                record.tailCall = null;
                record.panel = null;
                record.info = null;
                record.eventRef = null;
                record.oldValue = null;
            }
            records.Clear();
        }

        private static UnityAction<string> BuildHead(Instrumentation record)
        {
            return delegate(string value)
            {
                record.captured = false;
                if (!record.IsAlive())
                    return;

                LevelEvent selectedEvent = record.panel.inspectorPanel.selectedEvent;
                if (selectedEvent == null)
                    return;
                if (!selectedEvent.TryGet<object>(record.info.name, out record.oldValue))
                    return;

                record.eventRef = selectedEvent;
                record.captured = true;
            };
        }

        private static UnityAction<string> BuildTail(Instrumentation record)
        {
            return delegate(string value)
            {
                if (!record.captured || !record.IsAlive())
                    return;

                LevelEvent selectedEvent = record.panel.inspectorPanel.selectedEvent;
                if (selectedEvent == null || !ReferenceEquals(selectedEvent, record.eventRef))
                    return;

                object newValue;
                if (!selectedEvent.TryGet<object>(record.info.name, out newValue))
                    return;

                if (!Main.MultiTrack.OnValueChanged(selectedEvent, record.info.name, record.oldValue, newValue))
                {
                    selectedEvent[record.info.name] = record.oldValue;
                    selectedEvent.UpdatePanel();
                }
            };
        }

        private static TMP_InputField ResolveInputField(object control)
        {
            PropertyControl_Text text = control as PropertyControl_Text;
            return text != null ? text.inputField : null;
        }

        private static Instrumentation FindRecord(TMP_InputField inputField)
        {
            PruneDestroyed();
            for (int i = 0; i < records.Count; i++)
            {
                if (ReferenceEquals(records[i].field, inputField))
                    return records[i];
            }
            return null;
        }

        private static void PruneDestroyed()
        {
            for (int i = records.Count - 1; i >= 0; i--)
            {
                // Unity 的 == 会把已销毁对象判成 null，正是要清理的
                if (records[i].field == null)
                    records.RemoveAt(i);
            }
        }

        private static System.Collections.IList GetRuntime(TMP_InputField inputField)
        {
            if (inputField.onEndEdit == null)
                return null;
            object calls = callsField.GetValue(inputField.onEndEdit);
            return calls != null ? runtimeCallsField.GetValue(calls) as System.Collections.IList : null;
        }

        /// <summary>通知 UnityEvent 重建调用列表（它缓存了一份 m_ExecutingCalls）。</summary>
        private static void MarkDirty(TMP_InputField inputField)
        {
            if (inputField == null || callsField == null || needsUpdateField == null)
                return;
            object calls = callsField.GetValue(inputField.onEndEdit);
            if (calls != null)
                needsUpdateField.SetValue(calls, true);
        }

        private static bool ContainsCall(System.Collections.IList runtime, object call)
        {
            return call != null && runtime.Contains(call);
        }

        private static void RemoveCall(System.Collections.IList runtime, object call)
        {
            if (call != null && runtime.Contains(call))
                runtime.Remove(call);
        }

        private static bool EnsureReflectionHandles()
        {
            if (reflectionBroken)
                return false;
            if (callsField == null || runtimeCallsField == null || needsUpdateField == null || getDelegateMethod == null)
            {
                reflectionBroken = true;
                Main.Logger?.Log("警告: 取不到 TMP_InputField.onEndEdit 的内部委托列表，文本输入联动不会生效");
                return false;
            }
            return true;
        }
    }

    /// <summary>面板属性刷新后重新套用控件可见性列表。</summary>
    [HarmonyPatch(typeof(PropertiesPanel), "SetProperties")]
    internal static class ActiveChildPatch
    {
        internal static void Postfix(LevelEvent levelEvent, bool checkIfEnabled = true)
        {
            if (levelEvent == null || Main.MultiTrack == null)
                return;
            if (levelEvent.eventType == (LevelEventType)Main.MultiTrack.type)
                Main.activeChilden();
        }
    }

    /// <summary>MinMax 控件的范围拖动也会影响可见性（值跨界时某些项要跟着变）。</summary>
    [HarmonyPatch(typeof(ADOFAI.Editor.Components.MinMaxControl), "OnChange")]
    internal static class ActiveChildPatch2
    {
        internal static void Postfix()
        {
            Main.activeChilden();
        }
    }
}
