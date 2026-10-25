using HarmonyLib;
using MultiTrackHelper.Utils;
using System;

namespace MultiTrackHelper.Patches
{
    // 原版只有 SaveStateScope 的构造函数会调 SaveState；别的地方裸调（MappingHelper 的 ShowPanel 补丁就是）
    // 在 PACL 接管撤销栈时会留下一个永不收尾的撤销点。这里当场把它丢掉，撤销前再兜底清一遍。
    internal static class PaclUndoGuardPatches
    {
        /// <summary>SaveStateScope 构造器正在执行的层数，用来区分"正常压栈"与"裸调 SaveState"。</summary>
        private static int scopeCtorDepth;
        private static bool loggedDrop;

        /// <summary>
        /// 关闭模组时清零。计数只是进程内的守卫状态，禁用后不再有补丁来配平它，
        /// 留着会让下次启用时的第一次裸调被误判成"正常压栈"。
        /// </summary>
        internal static void Reset()
        {
            scopeCtorDepth = 0;
            loggedDrop = false;
        }

        [HarmonyPatch(typeof(SaveStateScope), MethodType.Constructor, new[] { typeof(scnEditor), typeof(bool), typeof(bool), typeof(bool) })]
        internal static class SaveStateScopeCtorPatch
        {
            /// <summary>
            /// __state 记录"本补丁的 Prefix 确实跑过并加了计数"：Finalizer 只在它为 true 时才减。
            /// 只加不减/只减不加都会让计数漂移，之后要么漏放行、要么把正常压栈误判成裸调。
            /// </summary>
            internal static void Prefix(out bool __state)
            {
                __state = false;
                scopeCtorDepth++;
                __state = true;
            }

            internal static void Finalizer(bool __state)
            {
                if (!__state || scopeCtorDepth <= 0)
                    return;
                scopeCtorDepth--;
            }
        }

        [HarmonyPatch(typeof(scnEditor), "SaveState")]
        internal static class SaveStatePatch
        {
            /// <summary>
            /// 一次 SaveState 调用的上下文，随 Harmony __state 走。
            /// 不用共享静态字段：SaveState 可能被别的补丁递归调用，共享字段会被内层覆盖，
            /// 外层收尾时拿到的"调用前状态"就变成了内层的，守卫会误判并丢掉不该丢的撤销点。
            /// </summary>
            internal struct BareSaveState
            {
                /// <summary>本次调用前 PACL 的 currentState；仅在 <see cref="tracked"/> 为 true 时有意义。</summary>
                internal object stateBefore;
                /// <summary>本补丁是否真的接管了这次调用（Scope 构造器内部调用 / PACL 未接管 时为 false）。</summary>
                internal bool tracked;
            }

            internal static void Prefix(out BareSaveState __state)
            {
                __state = default(BareSaveState);
                try
                {
                    if (scopeCtorDepth > 0 || !PaclUndoBridge.IsActive())
                        return;

                    __state.stateBefore = PaclUndoBridge.CurrentState();
                    __state.tracked = true;
                }
                catch (Exception e)
                {
                    Main.Logger.Log("PaclUndoGuard: 读取 PACL 撤销状态失败：" + e.Message);
                }
            }

            internal static void Postfix(BareSaveState __state)
            {
                if (!__state.tracked)
                    return;
                try
                {
                    // 只认这次调用新压的那个；PACL 在 changingState > 0 时直接返回，currentState 还是别人正在用的
                    object state = PaclUndoBridge.CurrentState();
                    if (state == null || ReferenceEquals(state, __state.stateBefore))
                        return;
                    if (PaclUndoBridge.DropUnfinishedState(state) && !loggedDrop)
                    {
                        loggedDrop = true;
                        Main.Logger.Log("PaclUndoGuard: 有代码绕过 SaveStateScope 直接调用 SaveState（常见于别的 mod 打开自己的面板），已丢掉它留下的未收尾撤销点，以后同类情况不再提示");
                    }
                }
                catch (Exception e)
                {
                    Main.Logger.Log("PaclUndoGuard: 清理未收尾撤销点失败：" + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(scnEditor), "UndoOrRedo")]
        internal static class UndoOrRedoPatch
        {
            internal static void Prefix(scnEditor __instance)
            {
                try
                {
                    if (__instance.changingState != 0 || !PaclUndoBridge.IsActive())
                        return;
                    int removed = PaclUndoBridge.PurgeUnfinishedStates();
                    if (removed > 0)
                        Main.Logger.Log($"PaclUndoGuard: 撤销前清掉了 {removed} 个未收尾的撤销点（否则 PACL 撤到它会报错并卡住整条撤销栈）");
                }
                catch (Exception e)
                {
                    Main.Logger.Log("PaclUndoGuard: 撤销前清理失败：" + e.Message);
                }
            }
        }
    }
}
