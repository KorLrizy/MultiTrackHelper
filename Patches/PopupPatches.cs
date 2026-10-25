using ADOFAI;
using HarmonyLib;
using MultiTrackHelper.Utils;
using UnityEngine;

namespace MultiTrackHelper.Patches
{
    /// <summary>
    /// 编辑器 Start 时克隆游戏自带的确认弹窗 prefab 作为我们的消息弹窗模板：
    /// 先摘掉模板里的 scrTextChanger（它会把文本改成固定的本地化串），
    /// 之后由 Popup 自己写文本。
    /// </summary>
    [HarmonyPatch(typeof(scnEditor), "Start")]
    internal static class PopupPatch
    {
        internal static void Postfix()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.okPopupContainer == null || editor.popupWindow == null)
                return;

            // 编辑器重复 Start（重新载入关卡）时先清掉上一份，避免容器里越堆越多
            if (Popup.popup != null)
                UnityEngine.Object.Destroy(Popup.popup);

            GameObject template = UnityEngine.Object.Instantiate(
                editor.okPopupContainer, editor.popupWindow.transform);
            // 模板可能是未激活的，要连隐藏子物体一起摘（默认重载不含 inactive）
            foreach (scrTextChanger changer in template.GetComponentsInChildren<scrTextChanger>(true))
                UnityEngine.Object.Destroy(changer);

            Popup.Bind(template);
        }
    }
}
