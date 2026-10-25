using ADOFAI;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace MultiTrackHelper.Utils
{
    // PACL2 的 BetterUndoRedo 接管撤销栈后，原版 SaveStateScope 落到它的"旧 SaveState"兜底：
    // 只记索引器赋值与增删，回滚时把装饰也当成 events 去 Remove（删不掉，装饰留在原地被打回默认值）。
    // 所以它生效时改成整批前后快照，用运行时派生的 LevelState 压进 PACL 的撤销栈；
    // 没装 PACL 或功能关着时照旧走原版 SaveStateScope。
    internal static class UndoScope
    {
        internal static IDisposable Begin(scnEditor editor)
        {
            if (editor != null && PaclUndoBridge.IsActive())
                return new SnapshotScope(editor);
            return new SaveStateScope(editor);
        }

        private static readonly AccessTools.FieldRef<LevelEvent, Dictionary<string, object>> dataRef =
            AccessTools.FieldRefAccess<LevelEvent, Dictionary<string, object>>("data");

        private static readonly MethodInfo unsavedChangesSetter =
            AccessTools.PropertySetter(typeof(scnEditor), "unsavedChanges");

        private sealed class EventState
        {
            private int floor;
            private bool active;
            private bool visible;
            private bool locked;
            private Dictionary<string, object> data;
            private Dictionary<string, bool> disabled;

            // 只做浅拷贝：本模组改值都是整值替换，不会就地改值里的对象
            internal static EventState Capture(LevelEvent e)
            {
                Dictionary<string, object> d = dataRef(e);
                return new EventState
                {
                    floor = e.floor,
                    active = e.active,
                    visible = e.visible,
                    locked = e.locked,
                    data = d == null ? null : new Dictionary<string, object>(d),
                    disabled = e.disabled == null ? null : new Dictionary<string, bool>(e.disabled)
                };
            }

            internal bool SameAs(EventState other)
            {
                return floor == other.floor && active == other.active && visible == other.visible && locked == other.locked
                    && SameDictionary(data, other.data) && SameDictionary(disabled, other.disabled);
            }

            // 就地改回字典内容，不换字典实例：面板与别的 mod 可能还拿着原引用
            internal void ApplyTo(LevelEvent e)
            {
                e.floor = floor;
                e.active = active;
                e.visible = visible;
                e.locked = locked;
                Dictionary<string, object> d = dataRef(e);
                if (d == null || data == null)
                    dataRef(e) = data == null ? null : new Dictionary<string, object>(data);
                else
                    Refill(d, data);
                if (e.disabled == null || disabled == null)
                    e.disabled = disabled == null ? null : new Dictionary<string, bool>(disabled);
                else
                    Refill(e.disabled, disabled);
            }

            private static void Refill<T>(Dictionary<string, T> target, Dictionary<string, T> source)
            {
                target.Clear();
                foreach (KeyValuePair<string, T> pair in source)
                    target[pair.Key] = pair.Value;
            }

            private static bool SameDictionary<T>(Dictionary<string, T> a, Dictionary<string, T> b)
            {
                if (a == null || b == null)
                    return a == b;
                if (a.Count != b.Count)
                    return false;
                foreach (KeyValuePair<string, T> pair in a)
                {
                    if (!b.TryGetValue(pair.Key, out T value) || !Equals(pair.Value, value))
                        return false;
                }
                return true;
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<LevelEvent>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();

            public bool Equals(LevelEvent x, LevelEvent y) => ReferenceEquals(x, y);

            public int GetHashCode(LevelEvent obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class SnapshotScope : IDisposable
        {
            private readonly scnEditor editor;
            private readonly bool record;
            private readonly LevelEvent[] eventsBefore;
            private readonly LevelEvent[] decorationsBefore;
            private readonly Dictionary<LevelEvent, EventState> statesBefore;
            private bool disposed;

            internal SnapshotScope(scnEditor editor)
            {
                this.editor = editor;
                // 与 PACL 自己的 scope 同一规则：嵌在别的撤销动作里（changingState > 0）时不单独压栈
                record = editor.initialized && editor.changingState == 0;
                if (record)
                {
                    eventsBefore = editor.events.ToArray();
                    decorationsBefore = editor.decorations.ToArray();
                    statesBefore = new Dictionary<LevelEvent, EventState>(ReferenceComparer.Instance);
                    CaptureStates(eventsBefore, statesBefore);
                    CaptureStates(decorationsBefore, statesBefore);
                }
                // 和原版 SaveStateScope 一样占住 changingState，挡住操作中途的选砖/面板切换各自压栈
                editor.changingState++;
            }

            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;
                editor.changingState--;
                if (!record)
                    return;
                try
                {
                    UndoStep step = UndoStep.Create(editor, eventsBefore, decorationsBefore, statesBefore);
                    if (step == null)
                        return;
                    if (PaclUndoBridge.Push(step.Apply))
                        unsavedChangesSetter?.Invoke(editor, new object[] { true });
                }
                catch (Exception e)
                {
                    Main.Logger.Log("UndoScope: 记录撤销点失败，本次操作不能撤销：" + e);
                }
            }

            private static void CaptureStates(LevelEvent[] items, Dictionary<LevelEvent, EventState> states)
            {
                foreach (LevelEvent e in items)
                {
                    if (e != null && !states.ContainsKey(e))
                        states.Add(e, EventState.Capture(e));
                }
            }
        }

        private sealed class UndoStep
        {
            private LevelEvent[] eventsBefore;
            private LevelEvent[] eventsAfter;
            private LevelEvent[] decorationsBefore;
            private LevelEvent[] decorationsAfter;
            private LevelEvent[] changed;
            private EventState[] changedBefore;
            private EventState[] changedAfter;
            private LevelEvent[] added;
            private EventState[] addedAfter;

            // 只留"被改过值"的事件的前后值；增删和顺序靠前后两份引用数组整体换回
            internal static UndoStep Create(scnEditor editor, LevelEvent[] eventsBefore, LevelEvent[] decorationsBefore,
                Dictionary<LevelEvent, EventState> statesBefore)
            {
                LevelEvent[] eventsAfter = editor.events.ToArray();
                LevelEvent[] decorationsAfter = editor.decorations.ToArray();

                var changed = new List<LevelEvent>();
                var before = new List<EventState>();
                var after = new List<EventState>();
                foreach (KeyValuePair<LevelEvent, EventState> pair in statesBefore)
                {
                    EventState now = EventState.Capture(pair.Key);
                    if (now.SameAs(pair.Value))
                        continue;
                    changed.Add(pair.Key);
                    before.Add(pair.Value);
                    after.Add(now);
                }

                if (changed.Count == 0 && SameSequence(eventsBefore, eventsAfter) && SameSequence(decorationsBefore, decorationsAfter))
                    return null;

                // 本次操作新建的对象（操作前没有的）：撤销期间别处可能就地改了它们，重做时只靠引用换回会带回被改过的数据，
                // 所以另存操作完成时的值，重做时核对并还原
                var added = new List<LevelEvent>();
                var addedAfter = new List<EventState>();
                var seen = new HashSet<LevelEvent>(ReferenceComparer.Instance);
                CollectAdded(eventsAfter, statesBefore, seen, added, addedAfter);
                CollectAdded(decorationsAfter, statesBefore, seen, added, addedAfter);

                return new UndoStep
                {
                    eventsBefore = eventsBefore,
                    eventsAfter = eventsAfter,
                    decorationsBefore = decorationsBefore,
                    decorationsAfter = decorationsAfter,
                    changed = changed.ToArray(),
                    changedBefore = before.ToArray(),
                    changedAfter = after.ToArray(),
                    added = added.ToArray(),
                    addedAfter = addedAfter.ToArray()
                };
            }

            private static void CollectAdded(LevelEvent[] items, Dictionary<LevelEvent, EventState> statesBefore,
                HashSet<LevelEvent> seen, List<LevelEvent> added, List<EventState> addedAfter)
            {
                foreach (LevelEvent e in items)
                {
                    if (e == null || statesBefore.ContainsKey(e) || !seen.Add(e))
                        continue;
                    added.Add(e);
                    addedAfter.Add(EventState.Capture(e));
                }
            }

            // PACL 在它的 UndoOrRedo 里调用；异常必须在这里吞掉，否则它不会把这一步移出栈，之后每次撤销都卡在这里
            internal void Apply(bool redo)
            {
                try
                {
                    scnEditor editor = scnEditor.instance;
                    if (editor == null || !editor.initialized)
                        return;

                    Replace(editor.events, redo ? eventsAfter : eventsBefore);
                    Replace(editor.decorations, redo ? decorationsAfter : decorationsBefore);
                    EventState[] states = redo ? changedAfter : changedBefore;
                    for (int i = 0; i < changed.Length; i++)
                        states[i].ApplyTo(changed[i]);

                    int repaired = 0;
                    if (redo)
                    {
                        for (int i = 0; i < added.Length; i++)
                        {
                            EventState now = EventState.Capture(added[i]);
                            if (!now.SameAs(addedAfter[i]))
                            {
                                addedAfter[i].ApplyTo(added[i]);
                                repaired++;
                            }
                        }
                    }

                    Main.Logger.Log($"UndoScope: {(redo ? "重做" : "撤销")}一步：事件 {editor.events.Count} 条、装饰 {editor.decorations.Count} 个、改值对象 {changed.Length} 个、新增对象 {added.Length} 个"
                        + (repaired > 0 ? $"，其中 {repaired} 个新增对象在撤销期间被别处改过，已按操作完成时的数据还原" : ""));
                    Refresh(editor);
                }
                catch (Exception e)
                {
                    Main.Logger.Log("UndoScope: " + (redo ? "重做" : "撤销") + "失败：" + e);
                }
            }

            // 走 List 基类方法：DecorationsArray 的 new Add 会逐项去戳装饰列表面板
            private static void Replace(List<LevelEvent> list, LevelEvent[] items)
            {
                list.Clear();
                list.AddRange(items);
            }

            private static void Refresh(scnEditor editor)
            {
                editor.DeselectAllDecorations();
                editor.ApplyEventsToFloors();
                editor.UpdateDecorationObjects();
                editor.propertyControlDecorationsList?.RefreshItemsList(true);
                if (editor.SelectionIsSingle())
                {
                    scrFloor floor = editor.selectedFloors[0];
                    editor.levelEventsPanel.ShowTabsForFloor(floor.seqID);
                    editor.ShowEventIndicators(floor);
                }
            }

            private static bool SameSequence(LevelEvent[] a, LevelEvent[] b)
            {
                if (a.Length != b.Length)
                    return false;
                for (int i = 0; i < a.Length; i++)
                {
                    if (!ReferenceEquals(a[i], b[i]))
                        return false;
                }
                return true;
            }
        }
    }

    // 不引用 PACL 程序集：类型全按名字反射；LevelState 的子类用 Reflection.Emit 在运行时派生，
    // Undo/Redo 只转调一个 Action<bool>，真正的回放逻辑留在本模组里
    internal static class PaclUndoBridge
    {
        private const string MainTypeName = "PACL2.Core.Main";
        private const string SaveStatePatchTypeName = "PACL2.Features.FixChartLoad.CustomSaveState.SaveStatePatch";
        private const string LevelStateTypeName = "PACL2.Features.FixChartLoad.CustomSaveState.LevelState";
        private const string DefaultLevelStateTypeName = "PACL2.Features.FixChartLoad.CustomSaveState.DefaultLevelState";
        // 与 PACL 旧 SaveState 的栈上限一致
        private const int MaxUndoStates = 100;

        // 0 = 未解析成功（PACL 可能还没加载，下次再试）；1 = 成功；-1 = 类型在但成员对不上，永久放弃
        private static int resolved;
        private static FieldInfo mainInstanceField;
        private static FieldInfo featureField;
        private static System.Reflection.PropertyInfo activeProperty;
        private static FieldInfo undoStatesField;
        private static FieldInfo redoStatesField;
        private static ConstructorInfo stateCtor;
        private static FieldInfo callbackField;
        // 清理未收尾状态用；对不上时只关掉清理，不影响压栈
        private static Type defaultStateType;
        private static FieldInfo stateChangedEventsField;
        private static FieldInfo currentStateField;
        private static FieldInfo pendingEventsField;
        private static FieldInfo pendingFloorsField;
        private static FieldInfo pendingValuesField;

        private static readonly AccessTools.FieldRef<scnEditor, int> saveStateLastFrameRef =
            AccessTools.FieldRefAccess<scnEditor, int>("saveStateLastFrame");

        internal static bool IsActive()
        {
            if (!Resolve())
                return false;
            try
            {
                object main = mainInstanceField.GetValue(null);
                if (main == null)
                    return false;
                object feature = featureField.GetValue(main);
                return feature != null && (bool)activeProperty.GetValue(feature, null);
            }
            catch (Exception e)
            {
                resolved = -1;
                Main.Logger.Log("PaclUndoBridge: 读取 PACL BetterUndoRedo 状态失败，按未启用处理：" + e.Message);
                return false;
            }
        }

        // 压一步可撤销的操作；与 PACL 自己的 scope 一样清空重做栈
        internal static bool Push(Action<bool> undoOrRedo)
        {
            if (!Resolve())
                return false;
            object state = stateCtor.Invoke(null);
            callbackField.SetValue(state, undoOrRedo);

            IList undo = (IList)undoStatesField.GetValue(null);
            IList redo = (IList)redoStatesField.GetValue(null);
            if (undo == null || redo == null)
                return false;
            if (undo.Count >= MaxUndoStates)
                undo.RemoveAt(0);
            undo.Add(state);
            redo.Clear();

            scnEditor editor = scnEditor.instance;
            if (editor != null)
                saveStateLastFrameRef(editor) = Time.frameCount;
            return true;
        }

        // 裸调 editor.SaveState（不经 SaveStateScope）时 PACL 压一个 DefaultLevelState，却没有 Dispose 替它收尾：
        // 它的数组一直是 null，撤到它就抛 NRE 且不出栈；它还挂在 currentState 上，把之后的增删改都记进去，
        // 等下一个 SaveStateScope.Dispose（最晚是撤销自己用的那个）才收尾，成了一步回放陈旧改动的幽灵撤销点
        internal static object CurrentState()
        {
            if (!Resolve() || currentStateField == null)
                return null;
            return currentStateField.GetValue(null);
        }

        // 只丢"还没收尾"的状态：已收尾的是正常撤销点，不能动
        internal static bool DropUnfinishedState(object state)
        {
            if (state == null || !IsUnfinished(state))
                return false;
            IList undo = (IList)undoStatesField.GetValue(null);
            if (undo != null)
            {
                for (int i = undo.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(undo[i], state))
                    {
                        undo.RemoveAt(i);
                        break;
                    }
                }
            }
            ClearCurrentState();
            return true;
        }

        // 撤销/重做前调用：此时没有打开的 scope，栈里凡是没收尾的都是孤儿
        internal static int PurgeUnfinishedStates()
        {
            if (!Resolve() || currentStateField == null)
                return 0;
            int removed = Purge((IList)undoStatesField.GetValue(null)) + Purge((IList)redoStatesField.GetValue(null));
            if (IsUnfinished(currentStateField.GetValue(null)))
                ClearCurrentState();
            return removed;
        }

        private static int Purge(IList states)
        {
            if (states == null)
                return 0;
            int removed = 0;
            for (int i = states.Count - 1; i >= 0; i--)
            {
                if (IsUnfinished(states[i]))
                {
                    states.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        private static bool IsUnfinished(object state)
        {
            return state != null && defaultStateType.IsInstanceOfType(state) && stateChangedEventsField.GetValue(state) == null;
        }

        private static void ClearCurrentState()
        {
            currentStateField.SetValue(null, null);
            (pendingEventsField.GetValue(null) as IList)?.Clear();
            (pendingFloorsField.GetValue(null) as IList)?.Clear();
            (pendingValuesField.GetValue(null) as IDictionary)?.Clear();
        }

        private static bool Resolve()
        {
            if (resolved != 0)
                return resolved > 0;

            Type main = FindType(MainTypeName);
            Type saveStatePatch = FindType(SaveStatePatchTypeName);
            Type levelState = FindType(LevelStateTypeName);
            if (main == null || saveStatePatch == null || levelState == null)
                return false;

            try
            {
                mainInstanceField = main.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                featureField = main.GetField("BetterUndoRedo", BindingFlags.Public | BindingFlags.Instance);
                activeProperty = featureField == null ? null : FindProperty(featureField.FieldType, "Active");
                undoStatesField = saveStatePatch.GetField("undoStates", BindingFlags.Public | BindingFlags.Static);
                redoStatesField = saveStatePatch.GetField("redoStates", BindingFlags.Public | BindingFlags.Static);
                if (mainInstanceField == null || featureField == null || activeProperty == null
                    || activeProperty.PropertyType != typeof(bool)
                    || undoStatesField == null || redoStatesField == null
                    || !typeof(IList).IsAssignableFrom(undoStatesField.FieldType)
                    || !typeof(IList).IsAssignableFrom(redoStatesField.FieldType))
                {
                    resolved = -1;
                    Main.Logger.Log("PaclUndoBridge: PACL 撤销栈成员对不上（版本变了？），改用原版撤销作用域");
                    return false;
                }

                Type stateType = BuildStateType(levelState);
                stateCtor = stateType.GetConstructor(Type.EmptyTypes);
                callbackField = stateType.GetField("callback");
                ResolveUnfinishedStateMembers(saveStatePatch);
                resolved = 1;
                Main.Logger.Log("PaclUndoBridge: 已接入 PACL 撤销栈");
                return true;
            }
            catch (Exception e)
            {
                resolved = -1;
                Main.Logger.Log("PaclUndoBridge: 接入 PACL 撤销栈失败，改用原版撤销作用域：" + e);
                return false;
            }
        }

        private static void ResolveUnfinishedStateMembers(Type saveStatePatch)
        {
            const BindingFlags publicStatic = BindingFlags.Public | BindingFlags.Static;
            Type stateType = FindType(DefaultLevelStateTypeName);
            FieldInfo changed = stateType?.GetField("changedEvents", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo current = saveStatePatch.GetField("currentState", publicStatic);
            FieldInfo events = saveStatePatch.GetField("changedEvents", publicStatic);
            FieldInfo floors = saveStatePatch.GetField("changedFloors", publicStatic);
            FieldInfo values = saveStatePatch.GetField("changedEventValues", publicStatic);
            if (changed == null || current == null || !current.FieldType.IsAssignableFrom(stateType)
                || events == null || !typeof(IList).IsAssignableFrom(events.FieldType)
                || floors == null || !typeof(IList).IsAssignableFrom(floors.FieldType)
                || values == null || !typeof(IDictionary).IsAssignableFrom(values.FieldType))
            {
                Main.Logger.Log("PaclUndoBridge: PACL 旧 SaveState 的成员对不上，不清理别的 mod 留下的未收尾撤销点");
                return;
            }
            defaultStateType = stateType;
            stateChangedEventsField = changed;
            pendingEventsField = events;
            pendingFloorsField = floors;
            pendingValuesField = values;
            currentStateField = current;
        }

        // public sealed class MtLevelState : LevelState { public Action<bool> callback; Undo() => callback(false); Redo() => callback(true); }
        private static Type BuildStateType(Type levelState)
        {
            AssemblyBuilder assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
                new AssemblyName("MultiTrackHelper.PaclUndo"), AssemblyBuilderAccess.Run);
            ModuleBuilder module = assembly.DefineDynamicModule("MultiTrackHelper.PaclUndo");
            TypeBuilder type = module.DefineType("MultiTrackHelper.PaclUndo.MtLevelState",
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, levelState);
            FieldBuilder callback = type.DefineField("callback", typeof(Action<bool>), FieldAttributes.Public);

            ConstructorInfo baseCtor = levelState.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            ConstructorBuilder ctor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            ILGenerator il = ctor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, baseCtor);
            il.Emit(OpCodes.Ret);

            MethodInfo invoke = typeof(Action<bool>).GetMethod("Invoke");
            DefineOverride(type, levelState.GetMethod("Undo"), callback, invoke, false);
            DefineOverride(type, levelState.GetMethod("Redo"), callback, invoke, true);
            return type.CreateType();
        }

        private static void DefineOverride(TypeBuilder type, MethodInfo baseMethod, FieldInfo callback, MethodInfo invoke, bool redo)
        {
            MethodBuilder method = type.DefineMethod(baseMethod.Name,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                typeof(void), Type.EmptyTypes);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, callback);
            il.Emit(redo ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Callvirt, invoke);
            il.Emit(OpCodes.Ret);
            type.DefineMethodOverride(method, baseMethod);
        }

        // 只按全名逐个程序集精确查找：PACL 未加载时每次操作都会再试，不能做全类型扫描
        private static Type FindType(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(name);
                if (type != null)
                    return type;
            }
            return null;
        }

        private static System.Reflection.PropertyInfo FindProperty(Type type, string name)
        {
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                System.Reflection.PropertyInfo property = t.GetProperty(name,
                    BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.GetGetMethod(true) != null)
                    return property;
            }
            return null;
        }
    }
}
