using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MultiTrackHelper.Utils
{
    /// <summary>
    /// 借用游戏自带的确认弹窗 prefab 显示一行消息。
    /// 模板由 PopupPatch 在编辑器 Start 时克隆一份存进 popup；
    /// 这里只负责把它挂到当前弹窗容器下、写文本、并把"确定"按钮换成关闭自己。
    ///
    /// 组件引用按模板实例缓存：旧实现每弹一次都 Find 一遍子节点，
    /// 弹窗文本改不出来的情况下会静默什么都不显示，现在至少能提前判断并退出。
    /// </summary>
    internal static class Popup
    {
        /// <summary>游戏的 PopupType 里没有这个值，只是借一个不会与原生弹窗撞车的编号。</summary>
        private const int PopupTypeId = 233;

        /// <summary>克隆出来的弹窗模板实例（由 PopupPatch 写入）。</summary>
        internal static GameObject popup;

        private static TMP_Text text;
        private static Button okButton;

        /// <summary>记录模板实例；旧实例的组件缓存一并作废。</summary>
        internal static void Bind(GameObject template)
        {
            popup = template;
            text = null;
            okButton = null;
        }

        public static void ShowMessage(string message)
        {
            if (!EnsureCached())
                return;

            // 先脱离父级再 ShowPopup：游戏会把弹窗容器摆到屏幕中央并做缩放动画，
            // 模板跟着父级一起动会闪一下
            popup.transform.SetParent(null, false);
            popup.SetActive(true);
            scnEditor.instance.ShowPopup(true, (scnEditor.PopupType)PopupTypeId, false);
            popup.transform.SetParent(scnEditor.instance.popupWindow.transform, false);

            text.text = message;
            okButton.onClick.RemoveAllListeners();
            okButton.onClick.AddListener(Close);
        }

        private static void Close()
        {
            if (scnEditor.instance != null)
                scnEditor.instance.ShowPopup(false, (scnEditor.PopupType)PopupTypeId, false);
        }

        /// <summary>模板与编辑器实例都还在、且两个关键组件找得到，才允许弹。</summary>
        private static bool EnsureCached()
        {
            if (popup == null || scnEditor.instance == null)
                return false;

            if (text == null)
                text = FindComponent<TMP_Text>("popupText");
            if (okButton == null)
                okButton = FindComponent<Button>("buttonOk");

            if (text == null || okButton == null)
            {
                Main.Logger?.Log("警告: 消息弹窗模板缺少 popupText 或 buttonOk，跳过本次提示");
                return false;
            }
            return true;
        }

        private static T FindComponent<T>(string childName) where T : Component
        {
            Transform child = popup.transform.Find(childName);
            return child != null ? child.GetComponent<T>() : null;
        }
    }
}
