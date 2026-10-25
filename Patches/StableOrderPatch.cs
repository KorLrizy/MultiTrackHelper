using ADOFAI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace MultiTrackHelper.Patches
{
    internal static class StableOrderPatch
    {
        private const float HalfSpan = 0.05f;

        private sealed class OrderState
        {
            internal List<LevelEvent> SourceList;
            internal int Count = -1;
            internal readonly Dictionary<LevelEvent, float> Ratios = new Dictionary<LevelEvent, float>();
            internal bool IsRebuilding;
        }

        private static readonly ConditionalWeakTable<scrDecorationManager, OrderState> States =
            new ConditionalWeakTable<scrDecorationManager, OrderState>();
        private static readonly ConditionalWeakTable<scrDecorationManager, OrderState>.CreateValueCallback CreateState =
            _ => new OrderState();

        private static List<LevelEvent> ResolveDecorations(scrDecorationManager manager)
        {
            scnGame game = scnGame.instance;
            if (game == null || game.decManager != manager)
            {
                scnEditor editor = scnEditor.instance;
                game = editor == null ? null : editor.customLevel;
            }
            if (game == null || game.decManager != manager || game.levelData == null)
                return null;
            return game.levelData.decorations;
        }

        private static bool IsStale(OrderState state, List<LevelEvent> decorations) =>
            !ReferenceEquals(state.SourceList, decorations) || state.Count != decorations.Count;

        private static void Rebuild(OrderState state, List<LevelEvent> decorations)
        {
            state.Ratios.Clear();
            int count = decorations.Count;
            for (int i = 0; i < count; i++)
            {
                LevelEvent e = decorations[i];
                if (e != null)
                    state.Ratios[e] = (i + 1f) / (count + 1f);
            }
            state.SourceList = decorations;
            state.Count = count;
        }

        private static void ApplyZ(scrDecoration deco, OrderState state)
        {
            if (deco.syncFloorDepth || deco.decType == DecorationType.Prefab)
                return;
            scrObjectDecoration obj = deco as scrObjectDecoration;
            if (obj != null && obj.objectType == ObjectDecorationType.PlayerBubble)
                return;
            LevelEvent e = deco.sourceLevelEvent;
            Transform pivot = deco.pivotTrans;
            if (e == null || pivot == null || !state.Ratios.TryGetValue(e, out float ratio))
                return;

            // 保留原版 -depth、遮罩区间及轨道子层级，仅以有界距离打破同序号平局。
            float z = HalfSpan * ratio;
            if (obj != null && obj.objectType == ObjectDecorationType.Floor)
                z += HalfSpan;
            Vector3 pos = pivot.localPosition;
            if (pos.z != z)
                pivot.localPosition = new Vector3(pos.x, pos.y, z);
        }

        private static void ApplyFor(scrDecoration deco)
        {
            scrDecorationManager manager = deco.manager;
            if (manager == null)
                return;
            OrderState state = States.GetValue(manager, CreateState);
            if (!state.IsRebuilding)
            {
                List<LevelEvent> decorations = ResolveDecorations(manager);
                if (decorations == null)
                    return;
                if (IsStale(state, decorations))
                    Rebuild(state, decorations);
            }
            ApplyZ(deco, state);
        }

        private static void ApplyAll(scrDecorationManager manager, OrderState state)
        {
            List<scrDecoration> all = manager.allDecorations;
            for (int i = 0; i < all.Count; i++)
            {
                scrDecoration deco = all[i];
                if (deco != null)
                    ApplyZ(deco, state);
            }
        }

        [HarmonyPatch(typeof(scnGame), "UpdateDecorationObjects", new[] { typeof(bool) })]
        internal static class UpdateDecorationObjectsPatch
        {
            internal static void Prefix(scnGame __instance)
            {
                scrDecorationManager manager = __instance.decManager;
                List<LevelEvent> decorations = __instance.levelData?.decorations;
                if (manager == null || decorations == null)
                    return;
                OrderState state = States.GetValue(manager, CreateState);
                Rebuild(state, decorations);
                // 批量加载只建立一次索引，逐条创建期间不重复遍历全列表。
                state.IsRebuilding = true;
            }

            internal static Exception Finalizer(scnGame __instance, Exception __exception)
            {
                scrDecorationManager manager = __instance.decManager;
                if (manager != null && States.TryGetValue(manager, out OrderState state))
                    state.IsRebuilding = false;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(scrDecorationManager), "ResetDecorations")]
        internal static class ResetDecorationsPatch
        {
            internal static void Prefix(scrDecorationManager __instance)
            {
                List<LevelEvent> decorations = ResolveDecorations(__instance);
                if (decorations != null)
                    Rebuild(States.GetValue(__instance, CreateState), decorations);
            }
        }

        [HarmonyPatch(typeof(scrDecorationManager), "CreateDecoration")]
        internal static class CreateDecorationPatch
        {
            internal static void Postfix(scrDecorationManager __instance)
            {
                OrderState state = States.GetValue(__instance, CreateState);
                if (state.IsRebuilding)
                    return;
                List<LevelEvent> decorations = ResolveDecorations(__instance);
                if (decorations == null)
                    return;
                if (IsStale(state, decorations))
                    Rebuild(state, decorations);
                ApplyAll(__instance, state);
            }
        }

        [HarmonyPatch(typeof(scrDecoration), "Setup")]
        internal static class DecorationSetupPatch
        {
            internal static void Postfix(scrDecoration __instance) => ApplyFor(__instance);
        }

        [HarmonyPatch(typeof(scrDecoration), "UpdatePosition")]
        internal static class DecorationUpdatePositionPatch
        {
            // 原版每次更新轴心位置都会把 Z 归零。
            internal static void Postfix(scrDecoration __instance) => ApplyFor(__instance);
        }
    }
}
