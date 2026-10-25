using ADOFAI;
using HarmonyLib;
using MultiTrackHelper.Features;
using System.Collections.Generic;

namespace MultiTrackHelper.Patches
{
    // 选中地砖变化后延迟一帧重新读取选中轨道上的倍频器（多选拖动时避免每块砖都重建数据）
    [HarmonyPatch(typeof(scnEditor), "OnSelectedFloorChange")]
    internal static class OnSelectedFloorChangePatch
    {
        internal static void Postfix()
        {
            if (Main.IsInsertFeatureActive() && InsertBpmMultiplierFeature.CaptureEnabled(Main.MultiTrackLevelEvent))
                InsertBpmMultiplierFeature.RequestSync(Main.MultiTrackLevelEvent);
        }
    }

    [HarmonyPatch(typeof(scnEditor), "Update")]
    internal static class InsertBpmMultiplierSyncPatch
    {
        internal static void Postfix()
        {
            InsertBpmMultiplierFeature.ProcessPending();
        }
    }

    [HarmonyPatch(typeof(scnGame), nameof(scnGame.LoadLevel))]
    internal static class ScnGameLoadLevelPatch
    {
        internal static void Postfix(scnGame __instance, bool __result)
        {
            if (!__result)
                return;
            scnEditor editor = scnEditor.instance;
            if (editor == null || !ADOBase.isLevelEditor || !ReferenceEquals(editor.customLevel, __instance))
                return;
            InsertBpmMultiplierFeature.ResetForLevel();
        }
    }

    [HarmonyPatch(typeof(scnEditor), "NewLevel")]
    internal static class ScnEditorNewLevelPatch
    {
        internal static void Prefix(scnEditor __instance, out List<float> __state)
        {
            __state = __instance.levelData.angleData;
        }

        internal static void Postfix(scnEditor __instance, List<float> __state)
        {
            // levelData.Setup() 会新建 angleData，引用未变说明玩家在保存对话框里取消了
            if (ReferenceEquals(__state, __instance.levelData.angleData))
                return;
            InsertBpmMultiplierFeature.ResetForLevel();
        }
    }
}
