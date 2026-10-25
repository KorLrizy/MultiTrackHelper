using HarmonyLib;
using MultiTrackHelper.Utils;
using System;

namespace MultiTrackHelper.Patches
{
    // 原版只有 SaveStateScope 的构造函数会调 SaveState；别的地方裸调（MappingHelper 的 ShowPanel 补丁就是）
    // 在 PACL 接管撤销栈时会留下一个永不收尾的撤销点。这里当场把它丢掉，撤销前再兜底清一遍。
    internal static class PaclUndoGuardPatches
    {
        private static int scopeCtorDepth;
        private static object stateBeforeBareSave;
        private static bool loggedDrop;

        [HarmonyPatch(typeof(SaveStateScope), MethodType.Constructor, new[] { typeof(scnEditor), typeof(bool), typeof(bool), typeof(bool) })]
        internal static class SaveStateScopeCtorPatch
        {
            internal static void Prefix() => scopeCtorDepth++;

            internal static void Finalizer() => scopeCtorDepth--;
        }

        [HarmonyPatch(typeof(scnEditor), "SaveState")]
        internal static class SaveStatePatch
        {
            internal static void Prefix(out bool __state)
            {
                __state = false;
                stateBeforeBareSave = null;
                try
                {
                    if (scopeCtorDepth > 0 || !PaclUndoBridge.IsActive())
                        return;
                    stateBeforeBareSave = PaclUndoBridge.CurrentState();
                    __state = true;
                }
                catch (Exception e)
                {
                    Main.Logger.Log("PaclUndoGuard: 读取 PACL 撤销状态失败：" + e.Message);
                }
            }

            internal static void Postfix(bool __state)
            {
                if (!__state)
                    return;
                try
                {
                    // 只认这次调用新压的那个；PACL 在 changingState > 0 时直接返回，currentState 还是别人正在用的
                    object state = PaclUndoBridge.CurrentState();
                    if (state == null || ReferenceEquals(state, stateBeforeBareSave))
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
                finally
                {
                    stateBeforeBareSave = null;
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
