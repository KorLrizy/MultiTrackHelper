using ADOFAI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MultiTrackHelper.Patches
{
    // 删砖要在原生平移之前抓被删砖上的装饰：OffsetFloorIDsInEvents 只搬 floor > start 的项，事后再筛就晚了一步。
    internal static class DecorationAnchorSyncPatch
    {
        private static bool inTileEdit;

        internal static bool IsTileAnchored(LevelEvent d)
        {
            if (!d.TryGet<object>("relativeTo", out object r) || r == null)
                return false;
            if (r is DecPlacementType t)
                return t == DecPlacementType.Tile;
            return (r as string) == "Tile";
        }

        [HarmonyPatch(typeof(scnEditor), "CreateFloor", new[] { typeof(float), typeof(bool), typeof(bool) })]
        internal static class CreateFloorFloatPatch
        {
            internal static void Prefix(out bool __state)
            {
                PaclDecorationUndo.TryRegister();
                __state = inTileEdit;
                inTileEdit = true;
            }

            internal static void Finalizer(bool __state) => inTileEdit = __state;
        }

        [HarmonyPatch(typeof(scnEditor), "CreateFloor", new[] { typeof(char), typeof(bool), typeof(bool) })]
        internal static class CreateFloorCharPatch
        {
            internal static void Prefix(out bool __state)
            {
                PaclDecorationUndo.TryRegister();
                __state = inTileEdit;
                inTileEdit = true;
            }

            internal static void Finalizer(bool __state) => inTileEdit = __state;
        }

        [HarmonyPatch(typeof(scnEditor), "DeleteFloor", new[] { typeof(int), typeof(bool) })]
        internal static class DeleteFloorPatch
        {
            internal static void Prefix(out bool __state)
            {
                PaclDecorationUndo.TryRegister();
                __state = inTileEdit;
                inTileEdit = true;
            }

            internal static void Finalizer(bool __state) => inTileEdit = __state;
        }

        // PACL 把 DeleteMultiSelection 整方法换掉了，但替换体仍在本 method 链里调 OffsetFloorIDsInEvents，所以上下文要在这里铺好
        [HarmonyPatch(typeof(scnEditor), nameof(scnEditor.DeleteMultiSelection))]
        internal static class DeleteMultiSelectionPatch
        {
            internal static void Prefix(out bool __state)
            {
                PaclDecorationUndo.TryRegister();
                __state = inTileEdit;
                inTileEdit = true;
            }

            internal static void Finalizer(bool __state) => inTileEdit = __state;
        }

        // PACL 是异步加载的，StartMod 那次注册常赶不上；编辑器重建和最早的编辑动作都再试挂一次
        [HarmonyPatch(typeof(scnEditor), "Awake")]
        internal static class PaclRegisterAwakePatch
        {
            internal static void Postfix() => PaclDecorationUndo.TryRegister();
        }

        [HarmonyPatch]
        internal static class PaclRegisterEditPatch
        {
            internal static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(scnEditor), "DeleteSingleSelection", new[] { typeof(bool) });
                yield return AccessTools.Method(typeof(scnEditor), "MultiCutFloors", Type.EmptyTypes);
                yield return AccessTools.Method(typeof(scnEditor), "DeletePrecedingFloors", Type.EmptyTypes);
                yield return AccessTools.Method(typeof(scnEditor), "DeleteSubsequentFloors", Type.EmptyTypes);
            }

            internal static void Prefix() => PaclDecorationUndo.TryRegister();
        }

        [HarmonyPatch(typeof(scnEditor), "OffsetFloorIDsInEvents")]
        internal static class OffsetFloorIDsPatch
        {
            // refreshDecSprites 是 private 字段；放在本补丁类里，取不到时只影响这一处，不连累外层的 inTileEdit
            private static readonly AccessTools.FieldRef<scnEditor, bool> refreshDecSpritesRef =
                AccessTools.FieldRefAccess<scnEditor, bool>("refreshDecSprites");

            internal static void Prefix(scnEditor __instance, int startFloorID, int offset, out List<LevelEvent> __state)
            {
                __state = null;
                if (!inTileEdit || offset >= 0)
                    return;

                // offset=-n 时被删的原 floor 区间是 [start, start+n-1]，用 long 防 int.MinValue 溢出
                long lastFloor = startFloorID - (long)offset - 1;
                __state = __instance.decorations
                    .Where(d => d.floor >= startFloorID && d.floor <= lastFloor && IsTileAnchored(d))
                    .ToList();
            }

            internal static void Postfix(scnEditor __instance, int startFloorID, int offset, List<LevelEvent> __state)
            {
                if (!inTileEdit)
                    return;

                List<LevelEvent> decorations = __instance.decorations;

                if (offset > 0)
                {
                    // 加砖：旧砖（floor == start）顺延，其上的装饰一并 +offset
                    int shifted = 0;
                    foreach (LevelEvent d in decorations)
                    {
                        if (d.floor == startFloorID && IsTileAnchored(d))
                        {
                            d.floor += offset;
                            shifted++;
                        }
                    }
                    if (shifted > 0)
                        Main.Logger.Log($"AnchorSync: 加砖，随旧砖顺延了 {shifted} 个装饰锚点 (+{offset})");
                    return;
                }

                if (__state == null || __state.Count == 0)
                    return;

                // 只从表里摘引用：游戏快照已经先存好了，但这层嵌套 scope 不进 PACL 的栈、会被提前重建，装饰必须此刻就扣下来
                int removed = 0;
                foreach (LevelEvent d in __state)
                {
                    if (!decorations.Contains(d))
                        continue;
                    decorations.Remove(d);
                    __instance.selectedDecorations.Remove(d);
                    removed++;
                }
                if (removed == 0)
                    return;

                refreshDecSpritesRef(__instance) = true;
                // OnDecorationUpdate 只置 pending 标记，真正的列表刷新在控件自己的 LateUpdate 里，不会在本次重建中途渲染
                __instance.propertyControlDecorationsList?.OnDecorationUpdate();
                Main.Logger.Log($"AnchorSync: 随删砖移除了 {removed} 个该砖上的装饰");
            }
        }
    }

    // PACL 的删砖 scope 只快照 events，装饰表不在账上：按 scope 身份记 before/after，在它的 Undo/Redo 之后补回装饰。
    internal static class PaclDecorationUndo
    {
        private const string SingleScopeTypeName = "PACL2.Features.FixChartLoad.CustomSaveState.Scope.AdofaiScope.DeleteFloorScope";
        private const string MultiScopeTypeName = "PACL2.Features.FixChartLoad.CustomSaveState.Scope.AdofaiScope.DeleteMultiFloorScope";
        private const string SelectFloorScopeTypeName = "PACL2.Features.FixChartLoad.CustomSaveState.Scope.AdofaiScope.SelectFloorScope";
        private const string PaclMainTypeName = "PACL2.Core.Main";

        private sealed class Snapshot
        {
            internal LevelEvent[] decs;
            internal int[] floors;
            internal LevelEvent[] selected;
            internal int floorCount;
        }

        private sealed class DecorationTransaction
        {
            internal scnEditor editor;
            internal Snapshot before;
            internal Snapshot after;
        }

        // net48 的 ConditionalWeakTable 没有 Clear，Reset 时整表换新
        private static ConditionalWeakTable<object, DecorationTransaction> transactions =
            new ConditionalWeakTable<object, DecorationTransaction>();

        private static Harmony storedHarmony;
        private static bool ready;
        private static FieldInfo paclMainField;
        private static FieldInfo paclFeatureField;
        private static System.Reflection.PropertyInfo paclActiveProperty;

        // 删 1 号砖的撤销补丁用；任一项取不到只关掉这一处补丁，不影响装饰事务
        private static Type singleScopeType;
        private static FieldInfo scopeIndexField;
        private static FieldInfo scopeAngleField;
        private static FieldInfo scopeEventsField;
        private static FieldInfo scopeBeforeSelectedField;
        private static FieldInfo scopeAfterSelectedField;
        private static MethodInfo selectFloorsMethod;
        private static readonly MethodInfo offsetFloorIDsMethod =
            AccessTools.Method(typeof(scnEditor), "OffsetFloorIDsInEvents", new[] { typeof(int), typeof(int) });
        private static readonly MethodInfo deleteFloorMethod =
            AccessTools.Method(typeof(scnEditor), "DeleteFloor", new[] { typeof(int), typeof(bool) });

        // PACL 由 JABootstrap 异步加载，StartMod 时它的类型可能还没出现，这里只存下 harmony 实例，真正挂接交给 TryRegister
        internal static void Register(Harmony harmony)
        {
            storedHarmony = harmony;
            TryRegister();
        }

        // 挂上之前由编辑器 Awake 和最早的编辑动作反复再试；挂上后 ready 挡住重复注册
        internal static void TryRegister()
        {
            if (ready || storedHarmony == null)
                return;

            var hooked = new List<KeyValuePair<MethodBase, MethodInfo>>();
            try
            {
                Type singleScope = FindType(SingleScopeTypeName);
                Type multiScope = FindType(MultiScopeTypeName);
                Type paclMain = FindType(PaclMainTypeName);
                if (singleScope == null || multiScope == null || paclMain == null)
                    return;

                // DeleteFloorScope(bool) 链到 (int)，所以只挂 int 版构造，不会重复
                var targets = new List<KeyValuePair<MethodBase, string[]>>
                {
                    CtorHook(singleScope.GetConstructor(new[] { typeof(int) }), nameof(ScopeHooks.OnConstructed)),
                    CtorHook(multiScope.GetConstructor(Type.EmptyTypes), nameof(ScopeHooks.OnConstructed)),
                    MethodHook(Declared(singleScope, "Dispose"), nameof(ScopeHooks.OnDisposed)),
                    MethodHook(Declared(multiScope, "Dispose"), nameof(ScopeHooks.OnDisposed)),
                    MethodHook(Declared(singleScope, "Undo"), nameof(ScopeHooks.ReadFloorCount), nameof(ScopeHooks.OnUndone)),
                    MethodHook(Declared(multiScope, "Undo"), nameof(ScopeHooks.ReadFloorCount), nameof(ScopeHooks.OnUndone)),
                    MethodHook(Declared(singleScope, "Redo"), nameof(ScopeHooks.ReadFloorCount), nameof(ScopeHooks.OnRedone)),
                    MethodHook(Declared(multiScope, "Redo"), nameof(ScopeHooks.ReadFloorCount), nameof(ScopeHooks.OnRedone))
                };
                if (targets.Any(t => t.Key == null))
                {
                    ClearReflectionCache();
                    return;
                }

                paclMainField = paclMain.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                paclFeatureField = paclMain.GetField("BetterUndoRedo", BindingFlags.Public | BindingFlags.Instance);
                paclActiveProperty = paclFeatureField == null ? null : FindProperty(paclFeatureField.FieldType, "Active");
                if (paclMainField == null || paclFeatureField == null || paclActiveProperty == null
                    || paclActiveProperty.PropertyType != typeof(bool))
                {
                    ClearReflectionCache();
                    return;
                }

                foreach (KeyValuePair<MethodBase, string[]> target in targets)
                {
                    HarmonyMethod prefix = target.Value[0] == null
                        ? null
                        : new HarmonyMethod(AccessTools.Method(typeof(ScopeHooks), target.Value[0]));
                    HarmonyMethod postfix = new HarmonyMethod(AccessTools.Method(typeof(ScopeHooks), target.Value[1]));
                    // 先记 handler 再 Patch，Patch 中途失败也能把这一步卸干净
                    if (prefix != null)
                        hooked.Add(new KeyValuePair<MethodBase, MethodInfo>(target.Key, prefix.method));
                    hooked.Add(new KeyValuePair<MethodBase, MethodInfo>(target.Key, postfix.method));
                    storedHarmony.Patch(target.Key, prefix, postfix);
                }

                ResolveFirstFloorFix(singleScope);
                ready = true;
                Main.Logger.Log("PaclUndo: 已接管 PACL 删砖 scope 的装饰事务");
            }
            catch (Exception e)
            {
                // 只回退本次动态挂上的 hook，别碰产品 patch 和其它 mod；保留 storedHarmony 以便下次再试
                foreach (KeyValuePair<MethodBase, MethodInfo> h in hooked)
                {
                    try
                    {
                        storedHarmony.Unpatch(h.Key, h.Value);
                    }
                    catch (Exception unpatchError)
                    {
                        Main.Logger.Log("PaclUndo: 回退单个 hook 失败：" + unpatchError);
                    }
                }
                ClearReflectionCache();
                Main.Logger.Log("PaclUndo: 注册 PACL 装饰 undo 兼容失败，已回退本次动态 hook：" + e);
            }
        }

        // StopMod 的 UnpatchAll(harmony.Id) 已经把动态 hook 卸了，这里只回到未注册状态
        internal static void Reset()
        {
            storedHarmony = null;
            ready = false;
            ClearReflectionCache();
            transactions = new ConditionalWeakTable<object, DecorationTransaction>();
        }

        private static void ClearReflectionCache()
        {
            paclMainField = null;
            paclFeatureField = null;
            paclActiveProperty = null;
            singleScopeType = null;
        }

        private static void ResolveFirstFloorFix(Type singleScope)
        {
            const BindingFlags field = BindingFlags.Public | BindingFlags.Instance;
            scopeIndexField = singleScope.GetField("index", field);
            scopeAngleField = singleScope.GetField("angle", field);
            scopeEventsField = singleScope.GetField("events", field);
            scopeBeforeSelectedField = singleScope.GetField("beforeSelected", field);
            scopeAfterSelectedField = singleScope.GetField("afterSelected", field);
            selectFloorsMethod = FindType(SelectFloorScopeTypeName)?.GetMethod("SelectFloors",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int[]) }, null);
            bool ok = scopeIndexField?.FieldType == typeof(int) && scopeAngleField?.FieldType == typeof(float)
                && scopeEventsField?.FieldType == typeof(LevelEvent[])
                && scopeBeforeSelectedField?.FieldType == typeof(int[]) && scopeAfterSelectedField?.FieldType == typeof(int[])
                && selectFloorsMethod != null && offsetFloorIDsMethod != null && deleteFloorMethod != null;
            singleScopeType = ok ? singleScope : null;
            if (!ok)
                Main.Logger.Log("PaclUndo: PACL 删砖 scope 的成员对不上，删 1 号砖的撤销补丁不启用");
        }

        // PACL 的 DeleteFloorScope.Undo/Redo 只在 index > 1 时动作（index = 被删的砖号），删掉 1 号砖后撤销、重做都是空操作。
        // 这里按它处理 index > 1 的同一套步骤补上 index == 1；只在它确实没动（砖数没变）时补，PACL 自己修好后自动让路
        private static void FixFirstFloor(object scope, int preFloorCount, bool redo)
        {
            if (!ready || scope == null || singleScopeType == null || scope.GetType() != singleScopeType)
                return;
            try
            {
                if ((int)scopeIndexField.GetValue(scope) != 1 || !FeatureActive())
                    return;
                scnEditor editor = scnEditor.instance;
                if (editor == null || !editor.initialized || preFloorCount < 0 || FloorCount(editor) != preFloorCount)
                    return;

                if (redo)
                {
                    deleteFloorMethod.Invoke(editor, new object[] { 1, true });
                    selectFloorsMethod.Invoke(null, new[] { scopeAfterSelectedField.GetValue(scope) });
                }
                else
                {
                    offsetFloorIDsMethod.Invoke(editor, new object[] { 0, 1 });
                    editor.InsertFloatFloor(0, (float)scopeAngleField.GetValue(scope));
                    var events = (LevelEvent[])scopeEventsField.GetValue(scope);
                    if (events != null && events.Length != 0)
                    {
                        editor.events.AddRange(events);
                        scnGame.instance.ApplyEventsToFloors(scrLevelMaker.instance.listFloors);
                    }
                    selectFloorsMethod.Invoke(null, new[] { scopeBeforeSelectedField.GetValue(scope) });
                }
                Main.Logger.Log($"PaclUndo: 补上了 PACL 漏掉的删 1 号砖{(redo ? "重做" : "撤销")}");
            }
            catch (Exception e)
            {
                Main.Logger.Log("PaclUndo: 删 1 号砖的撤销补丁失败：" + e);
            }
        }

        private static KeyValuePair<MethodBase, string[]> CtorHook(MethodBase original, string postfix) =>
            new KeyValuePair<MethodBase, string[]>(original, new string[] { null, postfix });

        private static KeyValuePair<MethodBase, string[]> MethodHook(MethodBase original, string postfix) =>
            new KeyValuePair<MethodBase, string[]>(original, new string[] { null, postfix });

        private static KeyValuePair<MethodBase, string[]> MethodHook(MethodBase original, string prefix, string postfix) =>
            new KeyValuePair<MethodBase, string[]>(original, new string[] { prefix, postfix });

        // 只按全名逐个程序集精确查找；不用 AccessTools.TypeByName，它找不到时会全类型扫描，而 TryRegister 会反复重试
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

        private static MethodInfo Declared(Type type, string name) =>
            type.GetMethod(name, BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

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

        private static bool FeatureActive()
        {
            object main = paclMainField.GetValue(null);
            if (main == null)
                return false;
            object feature = paclFeatureField.GetValue(main);
            if (feature == null)
                return false;
            return (bool)paclActiveProperty.GetValue(feature, null);
        }

        private static int FloorCount(scnEditor editor)
        {
            if (editor == null)
                return -1;
            LevelData data = editor.levelData;
            if (data == null)
                return -1;
            // 旧谱走 pathData：读档时 angleData 会是空列表，不能按它判砖数
            return data.isOldLevel ? data.pathData.Length : data.angleData.Count;
        }

        // 只存引用和 floor：深克隆会让 Gizmo 的 GetDecoration(原引用) 找不回来
        private static Snapshot Capture(scnEditor editor)
        {
            List<LevelEvent> decorations = editor.decorations;
            var snapshot = new Snapshot
            {
                decs = new LevelEvent[decorations.Count],
                floors = new int[decorations.Count],
                selected = editor.selectedDecorations.ToArray(),
                floorCount = FloorCount(editor)
            };
            for (int i = 0; i < decorations.Count; i++)
            {
                snapshot.decs[i] = decorations[i];
                snapshot.floors[i] = decorations[i].floor;
            }
            return snapshot;
        }

        private static void Restore(scnEditor editor, Snapshot snapshot)
        {
            List<LevelEvent> decorations = editor.decorations;
            // 走 List 基类：DecorationsArray 的 new Add 会按项去戳装饰列表面板
            decorations.Clear();
            decorations.AddRange(snapshot.decs);
            for (int i = 0; i < snapshot.decs.Length; i++)
                snapshot.decs[i].floor = snapshot.floors[i];

            List<LevelEvent> selected = editor.selectedDecorations;
            selected.Clear();
            foreach (LevelEvent d in snapshot.selected)
            {
                if (Array.IndexOf(snapshot.decs, d) >= 0)
                    selected.Add(d);
            }

            editor.UpdateDecorationObjects();
            if (editor.propertyControlDecorationsList != null)
                editor.propertyControlDecorationsList.OnDecorationUpdate();
        }

        private static void CaptureBefore(object scope)
        {
            if (!ready || scope == null)
                return;
            try
            {
                // int 版构造每个 scope 只走一次，已有事务就不覆盖，避免把 before 刷成删除之后的状态
                if (transactions.TryGetValue(scope, out _))
                    return;
                if (!FeatureActive())
                    return;
                scnEditor editor = scnEditor.instance;
                if (editor == null || !editor.initialized)
                    return;
                transactions.Add(scope, new DecorationTransaction
                {
                    editor = editor,
                    before = Capture(editor)
                });
            }
            catch (Exception e)
            {
                Main.Logger.Log("PaclUndo: before 快照失败：" + e);
            }
        }

        private static void CaptureAfter(object scope)
        {
            if (!ready || scope == null)
                return;
            try
            {
                if (!transactions.TryGetValue(scope, out DecorationTransaction tx)
                    || tx.before == null || tx.after != null)
                    return;
                if (!FeatureActive())
                    return;
                scnEditor editor = scnEditor.instance;
                if (!ReferenceEquals(editor, tx.editor))
                    return;
                tx.after = Capture(editor);
            }
            catch (Exception e)
            {
                Main.Logger.Log("PaclUndo: after 快照失败：" + e);
            }
        }

        private static void Replay(object scope, int preFloorCount, bool redo)
        {
            if (!ready || scope == null)
                return;
            try
            {
                if (!FeatureActive())
                    return;
                if (!transactions.TryGetValue(scope, out DecorationTransaction tx))
                    return;
                Snapshot before = tx.before;
                Snapshot after = tx.after;
                // 没有 after、或者这一步根本没让砖数变化（PACL 首砖 Undo 的 no-op），都不该只复活装饰
                if (before == null || after == null || before.floorCount == after.floorCount)
                    return;

                scnEditor editor = scnEditor.instance;
                if (editor == null || !editor.initialized || !ReferenceEquals(editor, tx.editor))
                    return;

                Snapshot want = redo ? after : before;
                Snapshot was = redo ? before : after;
                if (preFloorCount != was.floorCount || FloorCount(editor) != want.floorCount)
                {
                    Main.Logger.Log($"PaclUndo: 砖数对不上，跳过{(redo ? "重做" : "撤销")}的装饰回放（动作前 {preFloorCount}、应为 {was.floorCount}；动作后 {FloorCount(editor)}、应为 {want.floorCount}）");
                    return;
                }

                Restore(editor, want);
            }
            catch (Exception e)
            {
                Main.Logger.Log("PaclUndo: 装饰回放失败：" + e);
            }
        }

        internal static class ScopeHooks
        {
            internal static void OnConstructed(object __instance) => CaptureBefore(__instance);

            internal static void OnDisposed(object __instance) => CaptureAfter(__instance);

            internal static void ReadFloorCount(object __instance, out int __state)
            {
                __state = -1;
                if (!ready)
                    return;
                try
                {
                    __state = FloorCount(scnEditor.instance);
                }
                catch (Exception e)
                {
                    Main.Logger.Log("PaclUndo: 读取撤销前砖数失败：" + e);
                }
            }

            // 先补砖再回放装饰：Replay 要看到砖数已经变回来才会动
            internal static void OnUndone(object __instance, int __state)
            {
                FixFirstFloor(__instance, __state, false);
                Replay(__instance, __state, false);
            }

            internal static void OnRedone(object __instance, int __state)
            {
                FixFirstFloor(__instance, __state, true);
                Replay(__instance, __state, true);
            }
        }
    }
}
