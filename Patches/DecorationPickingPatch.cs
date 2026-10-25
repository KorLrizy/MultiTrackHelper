using ADOFAI;
using HarmonyLib;
using System;
using UnityEngine;

namespace MultiTrackHelper.Patches
{
    // 修复对象装饰的点击拾取：
    // 装饰的视觉由两层子对象嵌套构成——
    //   root（floor + position） → pivotTrans（localPosition = positionOffset） → childTransform（localPosition = pivotOffset）→ 贴图
    // 而编辑器的拾取碰撞体 editorCollider 只挂在 root 上。
    // 集中式多轨道的装饰 position 为 0、全部视觉偏移都在 pivotOffset 里，
    // 因此所有拾取碰撞体都堆在生成起点的砖块上：点击其他位置无法选中，
    // 点击起点会循环选中全部装饰。
    // 这里在 Setup 与每次 UpdatePosition 之后，把 editorCollider 对齐到最深的
    // 视觉层（childTransform 的世界坐标），使点击判定与所见位置一致。
    // 不改动事件数据，环绕动画（依赖 pivotOffset）不受影响。
    internal static class DecorationPickingPatch
    {
        private static void AlignCollider(scrDecoration deco)
        {
            LevelEvent e = deco.sourceLevelEvent;
            if (e == null || !DecorationAnchorSyncPatch.IsTileAnchored(e))
                return;
            if (!e.TryGet<object>("pivotOffset", out object p) || !(p is Vector2 pv) || pv == Vector2.zero)
                return;

            Transform visual = deco.childTransform;
            Component editorCollider = deco.editorCollider;
            if (visual == null || editorCollider == null)
                return;

            if ((Vector2)editorCollider.transform.position != (Vector2)visual.position)
                editorCollider.transform.position = visual.position;
        }

        [HarmonyPatch(typeof(scrDecoration), "Setup")]
        internal static class ScrDecorationSetupPatch
        {
            internal static void Postfix(scrDecoration __instance)
            {
                try
                {
                    AlignCollider(__instance);
                }
                catch (Exception ex)
                {
                    Main.Logger.Log("装饰拾取碰撞体修正失败: " + ex);
                }
            }
        }

        [HarmonyPatch(typeof(scrDecoration), "UpdatePosition")]
        internal static class ScrDecorationUpdatePositionPatch
        {
            internal static void Postfix(scrDecoration __instance)
            {
                try
                {
                    AlignCollider(__instance);
                }
                catch (Exception)
                {
                    // 每帧调用，静默
                }
            }
        }
    }
}
